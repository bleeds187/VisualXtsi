#include "project_args.h"

#include "unicode.h"
#include "xml.h"

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <sstream>
#include <unordered_map>
#include <unordered_set>

namespace fs = std::filesystem;

namespace vx {

namespace {

fs::path toPath(const std::string& utf8)
{
    const std::u16string w = toUtf16(utf8);
    return fs::path(std::wstring(w.begin(), w.end()));
}

std::string fromPath(const fs::path& p)
{
    const std::wstring w = p.wstring();
    return toUtf8(std::u16string(w.begin(), w.end()));
}

std::string lower(std::string s)
{
    for (auto& c : s)
        c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
    return s;
}

// Absolute, lexically normalized, lowercase, backslash-separated: for comparisons.
std::string pathKey(const fs::path& p)
{
    std::string s = lower(fromPath(p.lexically_normal()));
    std::replace(s.begin(), s.end(), '/', '\\');
    return s;
}

std::vector<std::string> splitList(const std::string& s)
{
    std::vector<std::string> out;
    std::string cur;
    auto flush = [&] {
        size_t b = cur.find_first_not_of(" \t\r\n\"");
        size_t e = cur.find_last_not_of(" \t\r\n\"");
        if (b != std::string::npos)
            out.push_back(cur.substr(b, e - b + 1));
        cur.clear();
    };
    for (char c : s) {
        if (c == ';')
            flush();
        else
            cur.push_back(c);
    }
    flush();
    return out;
}

// Splits a command-line fragment on whitespace, honoring double quotes.
std::vector<std::string> splitCommandLine(const std::string& s)
{
    std::vector<std::string> out;
    std::string cur;
    bool quoted = false, any = false;
    for (char c : s) {
        if (c == '"') {
            quoted = !quoted;
            any = true;
        } else if (!quoted && (c == ' ' || c == '\t' || c == '\r' || c == '\n')) {
            if (any)
                out.push_back(cur);
            cur.clear();
            any = false;
        } else {
            cur.push_back(c);
            any = true;
        }
    }
    if (any)
        out.push_back(cur);
    return out;
}

struct Item {
    fs::path file;
    std::string key;
    std::string includeDirs;
    std::string forcedIncludes;
    std::string options;
};

struct Model {
    fs::path dir;
    std::unordered_map<std::string, std::string> props; // final values
    std::vector<Item> items;
};

class Evaluator {
public:
    Evaluator(const fs::path& vcxproj, const xml::Element& root) : root_(root)
    {
        const fs::path dir = vcxproj.parent_path();
        set("MSBuildProjectDirectory", fromPath(dir));
        set("MSBuildThisFileDirectory", fromPath(dir) + "\\");
        set("ProjectDir", fromPath(dir) + "\\");
        set("MSBuildProjectName", fromPath(vcxproj.stem()));
        // UBT writes the .sln (and so $(SolutionDir)) at the project root, two levels up.
        set("SolutionDir", fromPath(dir.parent_path().parent_path()) + "\\");
        chooseConfiguration();
    }

    void evaluate(Model& model)
    {
        for (auto& el : root_.children) {
            if (!conditionHolds(*el))
                continue;
            if (el->name == "PropertyGroup") {
                for (auto& p : el->children)
                    if (conditionHolds(*p))
                        set(p->name, expand(p->text));
            } else if (el->name == "ItemDefinitionGroup") {
                if (const xml::Element* cl = el->child("ClCompile"))
                    for (auto& m : cl->children)
                        if (conditionHolds(*m))
                            defaults_[m->name] = expand(m->text);
            }
        }
        // Item metadata is evaluated after all properties, as MSBuild does.
        for (auto& el : root_.children) {
            if (el->name != "ItemGroup" || !conditionHolds(*el))
                continue;
            for (auto& it : el->children) {
                const std::string* include = it->attribute("Include");
                if (it->name != "ClCompile" || !include || !conditionHolds(*it))
                    continue;
                Item item;
                item.file = (model.dir / toPath(expand(*include))).lexically_normal();
                item.key = pathKey(item.file);
                item.includeDirs = metadata(*it, "AdditionalIncludeDirectories");
                item.forcedIncludes = metadata(*it, "ForcedIncludeFiles");
                item.options = metadata(*it, "AdditionalOptions");
                model.items.push_back(std::move(item));
            }
        }
        model.props = props_;
    }

private:
    void set(const std::string& name, std::string value) { props_[lower(name)] = std::move(value); }

    std::string get(const std::string& name) const
    {
        auto it = props_.find(lower(name));
        return it == props_.end() ? std::string() : it->second;
    }

    // Expands $(Prop) with current values (unknown -> empty) and %(Meta) with
    // ItemDefinitionGroup defaults.
    std::string expand(const std::string& s) const
    {
        std::string out;
        for (size_t i = 0; i < s.size(); ++i) {
            if ((s[i] == '$' || s[i] == '%') && i + 1 < s.size() && s[i + 1] == '(') {
                size_t end = s.find(')', i + 2);
                if (end != std::string::npos) {
                    std::string name = s.substr(i + 2, end - i - 2);
                    if (s[i] == '$') {
                        out += get(name);
                    } else {
                        auto d = defaults_.find(name);
                        if (d != defaults_.end())
                            out += d->second;
                    }
                    i = end;
                    continue;
                }
            }
            out.push_back(s[i]);
        }
        return out;
    }

    std::string metadata(const xml::Element& item, const char* name) const
    {
        for (auto& m : item.children)
            if (m->name == name && conditionHolds(*m))
                return expand(m->text);
        auto d = defaults_.find(name);
        return d == defaults_.end() ? std::string() : d->second;
    }

    // Supports the forms UBT emits: 'a'=='b' and 'a'!='b' clauses joined by "and".
    bool conditionHolds(const xml::Element& el) const
    {
        const std::string* cond = el.attribute("Condition");
        if (!cond)
            return true;
        const std::string c = expand(*cond);
        bool any = false;
        for (size_t pos = 0;;) {
            size_t q1 = c.find('\'', pos);
            if (q1 == std::string::npos)
                break;
            size_t q2 = c.find('\'', q1 + 1);
            if (q2 == std::string::npos)
                return false;
            size_t op = c.find_first_not_of(" ", q2 + 1);
            if (op == std::string::npos || c.compare(op, 2, "==") != 0 && c.compare(op, 2, "!=") != 0)
                return false;
            size_t q3 = c.find('\'', op + 2);
            size_t q4 = q3 == std::string::npos ? q3 : c.find('\'', q3 + 1);
            if (q4 == std::string::npos)
                return false;
            const bool equal = lower(c.substr(q1 + 1, q2 - q1 - 1)) == lower(c.substr(q3 + 1, q4 - q3 - 1));
            if (equal != (c[op] == '='))
                return false;
            any = true;
            pos = q4 + 1;
        }
        return any;
    }

    // Picks Configuration|Platform from the conditions in the file, preferring the editor
    // development build that UBT uses for IntelliSense.
    void chooseConfiguration()
    {
        std::vector<std::string> configs;
        std::function<void(const xml::Element&)> scan = [&](const xml::Element& e) {
            if (const std::string* cond = e.attribute("Condition")) {
                const std::string marker = "=='";
                size_t m = cond->find(marker);
                if (cond->find("$(Configuration)|$(Platform)") != std::string::npos && m != std::string::npos) {
                    size_t end = cond->find('\'', m + marker.size());
                    if (end != std::string::npos)
                        configs.push_back(cond->substr(m + marker.size(), end - m - marker.size()));
                }
            }
            for (auto& c : e.children)
                if (c->name == "PropertyGroup" || c->name == "ItemDefinitionGroup" || c->name == "ItemGroup" ||
                    c->name == "ImportGroup")
                    scan(*c);
        };
        scan(root_);
        std::string chosen;
        for (const char* pref : {"development_editor|x64", "development_editor|", "development|x64", "development"})
            for (auto& c : configs)
                if (chosen.empty() && lower(c).find(pref) == 0)
                    chosen = c;
        if (chosen.empty() && !configs.empty())
            chosen = configs.front();
        if (chosen.empty())
            chosen = "Development_Editor|x64";
        const size_t bar = chosen.find('|');
        set("Configuration", chosen.substr(0, bar));
        set("Platform", bar == std::string::npos ? "x64" : chosen.substr(bar + 1));
    }

    const xml::Element& root_;
    std::unordered_map<std::string, std::string> props_;
    std::map<std::string, std::string> defaults_;
};

std::shared_ptr<const Model> loadModel(const fs::path& vcxproj)
{
    static std::mutex mutex;
    static std::unordered_map<std::string, std::pair<fs::file_time_type, std::shared_ptr<const Model>>> cache;

    std::error_code ec;
    const auto stamp = fs::last_write_time(vcxproj, ec);
    if (ec)
        return nullptr;
    const std::string key = pathKey(vcxproj);
    {
        std::lock_guard lock(mutex);
        auto it = cache.find(key);
        if (it != cache.end() && it->second.first == stamp)
            return it->second.second;
    }

    std::ifstream in(vcxproj, std::ios::binary);
    std::stringstream buffer;
    buffer << in.rdbuf();
    auto root = xml::parse(buffer.str());
    if (!root || root->name != "Project")
        return nullptr;
    auto model = std::make_shared<Model>();
    model->dir = vcxproj.parent_path();
    Evaluator(vcxproj, *root).evaluate(*model);

    std::lock_guard lock(mutex);
    cache[key] = {stamp, model};
    return model;
}

// Number of leading path components two normalized keys share.
size_t commonComponents(const std::string& a, const std::string& b)
{
    size_t n = 0, i = 0;
    while (i < a.size() && i < b.size() && a[i] == b[i]) {
        if (a[i] == '\\')
            ++n;
        ++i;
    }
    return n;
}

bool isHeader(const fs::path& p)
{
    const std::string ext = lower(fromPath(p.extension()));
    return ext == ".h" || ext == ".hpp" || ext == ".hh" || ext == ".hxx" || ext == ".inl";
}

void appendOption(const std::string& opt, std::vector<std::string>& args, std::vector<std::string>& forced)
{
    if (opt.size() < 2 || (opt[0] != '/' && opt[0] != '-'))
        return;
    const std::string body = opt.substr(1);
    if (body.rfind("std:", 0) == 0) {
        std::string std = body.substr(4);
        if (std == "c++latest")
            std = "c++2c";
        args.push_back("-std=" + std);
    } else if (body.rfind("D", 0) == 0 && body.size() > 1) {
        args.push_back("-D" + body.substr(1));
    } else if (body.rfind("I", 0) == 0 && body.size() > 1) {
        args.push_back("-I" + body.substr(1));
    } else if (body.rfind("FI", 0) == 0 && body.size() > 2) {
        forced.push_back(body.substr(2));
    }
    // Other MSVC switches (/Yu, /Zc:, /permissive-, ...) have no bearing on parsing.
}

} // namespace

bool argsFromVcxproj(const std::string& vcxproj, const std::string& path, VcxprojArgs& out)
{
    const fs::path project = toPath(vcxproj);
    auto model = loadModel(project);
    if (!model)
        return false;
    const fs::path file = toPath(path).lexically_normal();
    const std::string key = pathKey(file);
    // Only files inside the project's tree ($(SolutionDir), two levels above ProjectFiles).
    const std::string rootKey = pathKey(model->dir.parent_path().parent_path()) + "\\";
    if (key.rfind(rootKey, 0) != 0)
        return false;

    // The file's own item, else the .cpp sharing the most leading directories
    // (for headers: a source file of the same module).
    const Item* best = nullptr;
    size_t bestScore = 0;
    for (const Item& item : model->items) {
        if (item.key == key) {
            best = &item;
            break;
        }
        size_t score = commonComponents(item.key, key);
        if (score > bestScore) {
            bestScore = score;
            best = &item;
        }
    }
    auto prop = [&](const char* name) {
        auto it = model->props.find(lower(name));
        return it == model->props.end() ? std::string() : it->second;
    };
    if (!best && prop("NMakeIncludeSearchPath").empty())
        return false;

    std::vector<std::string> args = {"-x", isHeader(file) ? "c++-header" : "c++", "-fms-extensions",
                                      "-fms-compatibility", "-fms-compatibility-version=19.40"};
    std::vector<std::string> forced;
    bool haveStd = false;

    for (const std::string& d : splitList(prop("NMakePreprocessorDefinitions")))
        args.push_back("-D" + d);

    std::vector<std::string> options;
    for (const std::string& source : {prop("AdditionalOptions"), best ? best->options : std::string()})
        for (const std::string& o : splitCommandLine(source))
            appendOption(o, options, forced);
    for (const std::string& o : options)
        haveStd |= o.rfind("-std=", 0) == 0;
    if (!haveStd)
        args.push_back("-std=c++20");
    args.insert(args.end(), options.begin(), options.end());

    const std::string itemForced = best ? best->forcedIncludes : std::string();
    for (const std::string& f : splitList(itemForced.empty() ? prop("NMakeForcedIncludes") : itemForced))
        forced.push_back(f);
    for (const std::string& f : forced) {
        fs::path p = toPath(f);
        if (p.is_relative())
            p = model->dir / p;
        const std::string name = lower(fromPath(p.filename()));
        std::error_code ec;
        // The shared PCH header is a precompiled-header stub: parsing it as text pulls in
        // whole engine modules and fails; the module's own includes are enough.
        if (name.find("sharedpch") != std::string::npos || !fs::exists(p, ec))
            continue;
        args.push_back("-include");
        args.push_back(fromPath(p.lexically_normal()));
    }

    const std::string itemIncludes = best ? best->includeDirs : std::string();
    std::unordered_set<std::string> seen;
    for (const std::string& list : {itemIncludes.empty() ? prop("NMakeIncludeSearchPath") : itemIncludes,
                                    prop("IncludePath")}) {
        for (const std::string& d : splitList(list)) {
            fs::path p = toPath(d);
            if (p.is_relative())
                p = model->dir / p;
            p = p.lexically_normal();
            if (seen.insert(pathKey(p)).second)
                args.push_back("-I" + fromPath(p));
        }
    }

    out.args = std::move(args);
    out.vcxproj = fromPath(project);
    out.basedOn = best ? fromPath(best->file) : std::string();
    out.projectRoot = fromPath(model->dir.parent_path().parent_path());
    out.moduleSources.clear();
    if (best) {
        // Same settings and the same Source\<Module> directory (UE4 projects share one setting
        // set across modules, so the directory is what separates them there).
        const std::string anchor = "\\source\\";
        std::string moduleDir = best->key.substr(0, best->key.rfind('\\') + 1);
        if (size_t s = best->key.find(anchor); s != std::string::npos) {
            size_t end = best->key.find('\\', s + anchor.size());
            if (end != std::string::npos)
                moduleDir = best->key.substr(0, end + 1);
        }
        for (const Item& item : model->items) {
            if (out.moduleSources.size() >= 300)
                break;
            if (item.key.rfind(moduleDir, 0) == 0 && item.includeDirs == best->includeDirs &&
                item.forcedIncludes == best->forcedIncludes && item.options == best->options)
                out.moduleSources.push_back(fromPath(item.file));
        }
    }
    return true;
}

bool argsFromUnrealProject(const std::string& path, VcxprojArgs& out)
{
    std::error_code ec;
    fs::path file = toPath(path);
    // The closest ancestor with UBT project files: a game project (next to its .uproject)
    // or an engine source tree (Engine/Intermediate/ProjectFiles).
    for (fs::path dir = file.parent_path(); !dir.empty(); dir = dir.parent_path()) {
        const fs::path projectFiles = dir / L"Intermediate" / L"ProjectFiles";
        if (fs::is_directory(projectFiles, ec)) {
            std::vector<fs::path> candidates;
            std::string preferred;
            for (auto& e : fs::directory_iterator(dir, ec))
                if (lower(fromPath(e.path().extension())) == ".uproject")
                    preferred = lower(fromPath(e.path().stem())) + ".vcxproj";
            for (auto& e : fs::directory_iterator(projectFiles, ec))
                if (lower(fromPath(e.path().extension())) == ".vcxproj")
                    candidates.push_back(e.path());
            // Try the project named after the .uproject (or UE5/UE4 for the engine) first.
            std::stable_partition(candidates.begin(), candidates.end(), [&](const fs::path& p) {
                const std::string n = lower(fromPath(p.filename()));
                return n == preferred || n == "ue5.vcxproj" || n == "ue4.vcxproj";
            });
            for (const fs::path& c : candidates)
                if (argsFromVcxproj(fromPath(c), path, out))
                    return true;
        }
        if (dir == dir.root_path())
            break;
    }
    return false;
}

} // namespace vx
