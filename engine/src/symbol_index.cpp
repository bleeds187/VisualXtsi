#include "symbol_index.h"

#include "file_index.h"
#include "unicode.h"

#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <shared_mutex>
#include <sstream>
#include <thread>
#include <unordered_map>
#include <unordered_set>

namespace fs = std::filesystem;

namespace vx::symbols {

namespace {

constexpr uintmax_t kMaxFileBytes = 8u << 20; // bigger files are generated data, not code
const wchar_t* const kCppExtensions[] = {L".h", L".hpp", L".hh", L".hxx", L".inl", L".ipp",
                                         L".cpp", L".cc", L".cxx", L".c", L".ixx", L".cppm"};

std::wstring lowerW(std::wstring s)
{
    for (auto& c : s)
        c = static_cast<wchar_t>(towlower(c));
    return s;
}

std::u16string lower16(std::u16string s)
{
    for (auto& c : s)
        c = static_cast<char16_t>(towlower(c));
    return s;
}

uint64_t charMask(const std::u16string& lowerText)
{
    uint64_t mask = 0;
    for (char16_t c : lowerText)
        mask |= uint64_t(1) << (c < 128 ? (c & 63) : 63);
    return mask;
}

bool isCpp(const std::wstring& path)
{
    const std::wstring ext = lowerW(fs::path(path).extension().wstring());
    return std::any_of(std::begin(kCppExtensions), std::end(kCppExtensions), [&](const wchar_t* e) { return ext == e; });
}

struct FileSymbols {
    std::wstring path;
    long long stamp = 0;
    uintmax_t size = 0;
    std::vector<Symbol> symbols;
    std::vector<std::u16string> lowerNames;
    std::vector<uint64_t> masks;

    void prepare()
    {
        lowerNames.clear();
        masks.clear();
        for (const Symbol& s : symbols) {
            lowerNames.push_back(lower16(s.name));
            masks.push_back(charMask(lowerNames.back()));
        }
    }
};

fs::path cachePath()
{
    wchar_t buf[MAX_PATH * 2];
    DWORD n = GetEnvironmentVariableW(L"VX_SYMBOL_CACHE", buf, static_cast<DWORD>(std::size(buf)));
    if (n > 0 && n < std::size(buf))
        return fs::path(std::wstring(buf, n));
    n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, static_cast<DWORD>(std::size(buf)));
    fs::path base = n > 0 && n < std::size(buf) ? fs::path(std::wstring(buf, n)) : fs::temp_directory_path();
    return base / L"VisualXtsi" / L"symbols.cache";
}

class Index {
public:
    void start()
    {
        std::lock_guard lock(lifecycle_);
        if (running_)
            return;
        running_ = true;
        loadCache();
        const unsigned workers = std::max(1u, std::min(8u, std::thread::hardware_concurrency() / 2));
        for (unsigned i = 0; i < workers; ++i)
            workers_.emplace_back([this] { work(); });
        files::setListener(&Index::onFileChange);
    }

    void stop()
    {
        std::lock_guard lock(lifecycle_);
        if (!running_)
            return;
        files::setListener(nullptr);
        {
            std::lock_guard q(queueMutex_);
            stopping_ = true;
            queue_.clear();
        }
        queueChanged_.notify_all();
        for (auto& w : workers_)
            w.join();
        workers_.clear();
        std::unique_lock data(mutex_);
        files_.clear();
        cache_.clear();
        stopping_ = false;
        running_ = false;
    }

    void enqueue(const std::wstring& path, bool forget)
    {
        if (!isCpp(path))
            return;
        if (forget) {
            std::unique_lock data(mutex_);
            files_.erase(lowerW(path));
            dirty_ = true;
            return;
        }
        {
            std::lock_guard q(queueMutex_);
            if (!queued_.insert(lowerW(path)).second)
                return;
            queue_.push_back(path);
        }
        queueChanged_.notify_one();
    }

    std::vector<Hit> find(const std::u16string& query, size_t maxResults) const
    {
        // "Scope::name": the part after the last "::" matches names, the rest matches scopes.
        std::u16string q = lower16(query);
        std::u16string scopeQuery;
        if (size_t sep = q.rfind(u"::"); sep != std::u16string::npos) {
            scopeQuery = q.substr(0, sep);
            q = q.substr(sep + 2);
        }
        const std::wstring wq(q.begin(), q.end()), wscope(scopeQuery.begin(), scopeQuery.end());
        const uint64_t qmask = charMask(q);
        std::vector<std::pair<int, Hit>> hits;
        std::shared_lock data(mutex_);
        for (const auto& [key, f] : files_) {
            for (size_t i = 0; i < f.symbols.size(); ++i) {
                if ((f.masks[i] & qmask) != qmask)
                    continue;
                const Symbol& s = f.symbols[i];
                const std::wstring name(f.lowerNames[i].begin(), f.lowerNames[i].end());
                const std::wstring original(s.name.begin(), s.name.end());
                int score = wq.empty() ? 0 : files::score(wq, name, original);
                if (score < 0)
                    continue;
                if (!wscope.empty()) {
                    const std::wstring scope(s.scope.begin(), s.scope.end());
                    const int scopeScore = files::score(wscope, lowerW(scope), scope);
                    if (scopeScore < 0)
                        continue;
                    score += scopeScore / 2;
                }
                score += rankBonus(s);
                hits.push_back({score, Hit{s, f.path}});
            }
        }
        data.unlock();
        const size_t keep = std::min(maxResults, hits.size());
        std::partial_sort(hits.begin(), hits.begin() + keep, hits.end(), [](const auto& a, const auto& b) {
            if (a.first != b.first)
                return a.first > b.first;
            return a.second.symbol.name.size() < b.second.symbol.name.size();
        });
        std::vector<Hit> result;
        for (size_t i = 0; i < keep; ++i)
            result.push_back(std::move(hits[i].second));
        return result;
    }

    std::vector<Hit> lookup(const std::u16string& name, size_t maxResults) const
    {
        std::vector<Hit> hits;
        std::shared_lock data(mutex_);
        for (const auto& [key, f] : files_)
            for (const Symbol& s : f.symbols)
                if (s.name == name)
                    hits.push_back(Hit{s, f.path});
        data.unlock();
        std::stable_sort(hits.begin(), hits.end(), [](const Hit& a, const Hit& b) {
            return rankBonus(a.symbol) > rankBonus(b.symbol);
        });
        if (hits.size() > maxResults)
            hits.resize(maxResults);
        return hits;
    }

    Status status() const
    {
        Status st;
        {
            std::shared_lock data(mutex_);
            st.files = files_.size();
            for (const auto& [key, f] : files_)
                st.symbols += f.symbols.size();
        }
        std::lock_guard q(queueMutex_);
        st.pending = queue_.size() + busy_;
        return st;
    }

    void save()
    {
        std::lock_guard saving(saveMutex_); // one writer at a time
        std::shared_lock data(mutex_);
        if (!dirty_.exchange(false))
            return;
        const fs::path path = cachePath();
        std::error_code ec;
        fs::create_directories(path.parent_path(), ec);
        const fs::path tmp = fs::path(path).concat(L".tmp");
        {
            std::ofstream out(tmp, std::ios::binary);
            out << "vxsym 2\n";
            for (const auto& [key, f] : files_) {
                out << "F\t" << f.stamp << '\t' << f.size << '\t' << toUtf8(std::u16string(f.path.begin(), f.path.end()))
                    << '\n';
                for (const Symbol& s : f.symbols)
                    out << "S\t" << int(s.kind) << '\t' << int(s.definition) << '\t' << s.line << '\t' << s.column << '\t'
                        << toUtf8(s.name) << '\t' << toUtf8(s.scope) << '\t' << int(s.ue) << '\n';
            }
        }
        fs::rename(tmp, path, ec);
    }

private:
    static int rankBonus(const Symbol& s)
    {
        int bonus = s.definition ? 4 : 0;
        switch (s.kind) {
        case Kind::Class:
        case Kind::Struct:
        case Kind::Enum:
        case Kind::Union:
        case Kind::Delegate:
            bonus += 8;
            break;
        case Kind::Typedef:
        case Kind::Namespace:
            bonus += 4;
            break;
        default:
            break;
        }
        return bonus;
    }

    static void onFileChange(const std::wstring& path, files::Change change);

    void work()
    {
        for (;;) {
            std::wstring path;
            {
                std::unique_lock q(queueMutex_);
                queueChanged_.wait(q, [this] { return stopping_ || !queue_.empty(); });
                if (stopping_)
                    return;
                path = std::move(queue_.front());
                queue_.pop_front();
                queued_.erase(lowerW(path));
                ++busy_;
            }
            index(path);
            bool drained;
            {
                std::lock_guard q(queueMutex_);
                --busy_;
                drained = queue_.empty() && busy_ == 0;
            }
            if (drained) {
                // Cache entries no root asked for (say, engine files from a session that indexed
                // the engine) only cost memory now: drop them; save() keeps present files only.
                {
                    std::unique_lock data(mutex_);
                    cache_.clear();
                }
                save();
            }
        }
    }

    void index(const std::wstring& path)
    {
        std::error_code ec;
        const fs::path p(path);
        const long long stamp = fs::last_write_time(p, ec).time_since_epoch().count();
        if (ec)
            return;
        const uintmax_t size = fs::file_size(p, ec);
        if (ec || size > kMaxFileBytes)
            return;
        const std::wstring key = lowerW(path);
        {
            // Unchanged since it was indexed (this session or a previous one): reuse.
            std::unique_lock data(mutex_);
            if (auto it = files_.find(key); it != files_.end() && it->second.stamp == stamp && it->second.size == size)
                return;
            if (auto it = cache_.find(key); it != cache_.end() && it->second.stamp == stamp && it->second.size == size) {
                files_[key] = std::move(it->second);
                cache_.erase(it);
                return;
            }
        }
        std::ifstream in(p, std::ios::binary);
        std::string bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        if (bytes.size() >= 3 && bytes.compare(0, 3, "\xEF\xBB\xBF") == 0)
            bytes.erase(0, 3);
        FileSymbols f;
        f.path = path;
        f.stamp = stamp;
        f.size = size;
        f.symbols = scan(toUtf16(bytes));
        f.prepare();
        std::unique_lock data(mutex_);
        files_[key] = std::move(f);
        dirty_ = true;
    }

    void loadCache()
    {
        std::ifstream in(cachePath(), std::ios::binary);
        std::string line;
        if (!std::getline(in, line) || line != "vxsym 2") // older caches are rebuilt
            return;
        FileSymbols* current = nullptr;
        std::unique_lock data(mutex_);
        while (std::getline(in, line)) {
            std::vector<std::string> fields;
            std::stringstream ss(line);
            for (std::string field; std::getline(ss, field, '\t');)
                fields.push_back(field);
            if (fields.size() == 4 && fields[0] == "F") {
                const std::u16string path16 = toUtf16(fields[3]);
                std::wstring path(path16.begin(), path16.end());
                FileSymbols& f = cache_[lowerW(path)];
                f.path = path;
                f.stamp = std::stoll(fields[1]);
                f.size = std::stoull(fields[2]);
                current = &f;
            } else if (current && fields.size() >= 6 && fields[0] == "S") {
                Symbol s;
                s.kind = static_cast<Kind>(std::stoi(fields[1]));
                s.definition = fields[2] == "1";
                s.line = static_cast<uint32_t>(std::stoul(fields[3]));
                s.column = static_cast<uint32_t>(std::stoul(fields[4]));
                s.name = toUtf16(fields[5]);
                s.scope = fields.size() > 6 ? toUtf16(fields[6]) : std::u16string();
                s.ue = fields.size() > 7 ? static_cast<uint8_t>(std::stoi(fields[7])) : 0;
                current->symbols.push_back(std::move(s));
            }
        }
        for (auto& [key, f] : cache_)
            f.prepare();
    }

    std::mutex lifecycle_;
    bool running_ = false;
    std::vector<std::thread> workers_;

    mutable std::mutex queueMutex_;
    std::condition_variable queueChanged_;
    std::deque<std::wstring> queue_;
    std::unordered_set<std::wstring> queued_;
    size_t busy_ = 0;
    bool stopping_ = false;

    mutable std::shared_mutex mutex_;
    std::unordered_map<std::wstring, FileSymbols> files_; // indexed and present
    std::unordered_map<std::wstring, FileSymbols> cache_; // loaded from disk, not yet confirmed
    std::atomic<bool> dirty_{false};
    std::mutex saveMutex_;
};

Index g_index;

void Index::onFileChange(const std::wstring& path, files::Change change)
{
    g_index.enqueue(path, change == files::Change::Removed);
}

} // namespace

void start()
{
    g_index.start();
}

std::vector<Hit> find(const std::u16string& query, size_t maxResults)
{
    return g_index.find(query, maxResults);
}

std::vector<Hit> lookup(const std::u16string& name, size_t maxResults)
{
    return g_index.lookup(name, maxResults);
}

Status status()
{
    return g_index.status();
}

void save()
{
    g_index.save();
}

void rebuild()
{
    // Restarting replays every indexed file (setListener), and with no cache all are scanned.
    g_index.stop();
    std::error_code ec;
    fs::remove(cachePath(), ec);
    g_index.start();
}

void reset()
{
    g_index.stop();
}

} // namespace vx::symbols
