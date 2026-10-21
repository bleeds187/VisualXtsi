#include "macro_expand.h"

#include <algorithm>
#include <iterator>

namespace vx {

namespace {

constexpr size_t kMaxSteps = 200000;
constexpr size_t kMaxTokens = 50000;

std::string stringize(const std::vector<PPToken>& arg)
{
    std::string s = "\"";
    for (size_t i = 0; i < arg.size(); ++i) {
        if (i > 0 && arg[i].spaceBefore)
            s.push_back(' ');
        const bool literal = !arg[i].text.empty() && (arg[i].text.back() == '"' || arg[i].text.back() == '\'');
        for (char c : arg[i].text) {
            if (literal && (c == '"' || c == '\\'))
                s.push_back('\\');
            s.push_back(c);
        }
    }
    s.push_back('"');
    return s;
}

} // namespace

bool isIdentifier(const std::string& text)
{
    if (text.empty())
        return false;
    auto start = [](unsigned char c) { return c == '_' || c == '$' || (c | 0x20) - 'a' < 26u || c >= 0x80; };
    if (!start(static_cast<unsigned char>(text[0])))
        return false;
    return std::all_of(text.begin() + 1, text.end(), [&](char c) {
        return start(static_cast<unsigned char>(c)) || (c >= '0' && c <= '9');
    });
}

int MacroExpander::paramIndex(const MacroDef& def, const PPToken& t) const
{
    if (!def.functionLike || !t.ident)
        return -1;
    auto it = std::find(def.params.begin(), def.params.end(), t.text);
    return it == def.params.end() ? -1 : static_cast<int>(it - def.params.begin());
}

std::vector<PPToken> MacroExpander::expand(std::vector<PPToken> input)
{
    return expandList(std::deque<PPToken>(std::make_move_iterator(input.begin()), std::make_move_iterator(input.end())));
}

std::vector<PPToken> MacroExpander::expandList(std::deque<PPToken> work)
{
    std::vector<PPToken> out;
    while (!work.empty()) {
        if (++steps_ > kMaxSteps || out.size() > kMaxTokens) {
            truncated_ = true;
            out.insert(out.end(), std::make_move_iterator(work.begin()), std::make_move_iterator(work.end()));
            break;
        }
        PPToken t = std::move(work.front());
        work.pop_front();
        if (!t.ident || t.hide.contains(t.text)) {
            out.push_back(std::move(t));
            continue;
        }
        if (t.text == "__LINE__" || t.text == "__FILE__" || t.text == "__COUNTER__") {
            std::string value = t.text == "__LINE__" ? std::to_string(line_)
                              : t.text == "__FILE__" ? "\"" + file_ + "\""
                                                     : std::to_string(counter_++);
            out.push_back(PPToken{value, false, t.spaceBefore});
            continue;
        }
        const MacroDef* def = table_.find(t.text);
        if (!def) {
            out.push_back(std::move(t));
            continue;
        }

        std::set<std::string> hide = t.hide;
        std::vector<std::vector<PPToken>> args;
        if (def->functionLike) {
            // A function-like macro name not followed by '(' is left alone.
            if (work.empty() || work.front().text != "(") {
                out.push_back(std::move(t));
                continue;
            }
            size_t i = 1;
            int depth = 1;
            args.emplace_back();
            for (; i < work.size(); ++i) {
                const std::string& s = work[i].text;
                if (s == "(")
                    ++depth;
                else if (s == ")") {
                    if (--depth == 0)
                        break;
                } else if (s == "," && depth == 1) {
                    args.emplace_back();
                    continue;
                }
                args.back().push_back(work[i]);
            }
            if (i == work.size()) { // unterminated invocation
                out.push_back(std::move(t));
                continue;
            }
            std::set<std::string> rparenHide = work[i].hide;
            work.erase(work.begin(), work.begin() + static_cast<ptrdiff_t>(i) + 1);

            if (args.size() == 1 && args[0].empty() && def->params.empty())
                args.clear();
            if (def->variadic && args.size() > def->params.size()) {
                // Fold the extra arguments back into __VA_ARGS__, commas included.
                auto& va = args[def->params.size() - 1];
                for (size_t a = def->params.size(); a < args.size(); ++a) {
                    va.push_back(PPToken{",", false, false});
                    va.insert(va.end(), args[a].begin(), args[a].end());
                }
                args.resize(def->params.size());
            }
            args.resize(def->params.size());

            std::set<std::string> merged;
            std::set_intersection(hide.begin(), hide.end(), rparenHide.begin(), rparenHide.end(),
                                  std::inserter(merged, merged.end()));
            hide = std::move(merged);
        }
        hide.insert(t.text);

        auto repl = substitute(*def, args, hide);
        if (!repl.empty())
            repl.front().spaceBefore = t.spaceBefore;
        work.insert(work.begin(), std::make_move_iterator(repl.begin()), std::make_move_iterator(repl.end()));
    }
    return out;
}

std::vector<PPToken> MacroExpander::substitute(const MacroDef& def, const std::vector<std::vector<PPToken>>& args,
                                               const std::set<std::string>& hide)
{
    std::vector<PPToken> res;
    const auto& body = def.body;
    for (size_t i = 0; i < body.size(); ++i) {
        const PPToken& tok = body[i];

        if (def.functionLike && tok.text == "#" && i + 1 < body.size()) {
            int p = paramIndex(def, body[i + 1]);
            if (p >= 0) {
                res.push_back(PPToken{stringize(args[p]), false, tok.spaceBefore});
                ++i;
                continue;
            }
        }

        if (tok.text == "##" && !res.empty() && i + 1 < body.size()) {
            const PPToken& next = body[++i];
            int p = paramIndex(def, next);
            std::vector<PPToken> rhs = p >= 0 ? args[p] : std::vector<PPToken>{next};
            if (rhs.empty())
                continue; // pasting with a placemarker leaves the left side unchanged
            PPToken& lhs = res.back();
            if (lhs.placemarker) {
                lhs = rhs[0];
                lhs.placemarker = false;
            } else {
                lhs.text += rhs[0].text;
                lhs.ident = isIdentifier(lhs.text);
            }
            res.insert(res.end(), rhs.begin() + 1, rhs.end());
            continue;
        }

        int p = paramIndex(def, tok);
        if (p >= 0) {
            const bool pasteNext = i + 1 < body.size() && body[i + 1].text == "##";
            std::vector<PPToken> toks = pasteNext ? args[p] : expandList(std::deque<PPToken>(args[p].begin(), args[p].end()));
            if (toks.empty() && pasteNext) {
                PPToken marker;
                marker.placemarker = true;
                res.push_back(std::move(marker));
                continue;
            }
            if (!toks.empty())
                toks.front().spaceBefore = tok.spaceBefore;
            res.insert(res.end(), std::make_move_iterator(toks.begin()), std::make_move_iterator(toks.end()));
            continue;
        }

        res.push_back(tok);
    }

    std::erase_if(res, [](const PPToken& t) { return t.placemarker; });
    for (auto& t : res)
        t.hide.insert(hide.begin(), hide.end());
    return res;
}

std::string formatTokens(const std::vector<PPToken>& tokens)
{
    auto noSpaceBefore = [](const std::string& s) {
        return s == "," || s == ";" || s == ")" || s == "]" || s == "." || s == "->" || s == "++" || s == "--";
    };
    auto noSpaceAfter = [](const std::string& s) {
        return s == "(" || s == "[" || s == "." || s == "->" || s == "::" || s == "~" || s == "!";
    };
    // Tokens whose spacing depends on context (scope, templates, pointers, calls):
    // keep whatever spacing the macro's source had.
    auto sourceSpacing = [](const std::string& s) {
        return s == "::" || s == "<" || s == ">" || s == "*" || s == "&" || s == "&&" || s == "(";
    };

    std::string out;
    int depth = 0;
    bool lineStart = true;
    auto newline = [&] {
        if (!lineStart) {
            out.push_back('\n');
            lineStart = true;
        }
    };

    for (size_t i = 0; i < tokens.size(); ++i) {
        const std::string& s = tokens[i].text;
        const std::string* prev = i > 0 ? &tokens[i - 1].text : nullptr;
        const std::string* next = i + 1 < tokens.size() ? &tokens[i + 1].text : nullptr;

        if (s == "}") {
            depth = std::max(0, depth - 1);
            newline();
        }
        const bool accessLabel = s == ":" && prev && (*prev == "public" || *prev == "private" || *prev == "protected");
        if (lineStart) {
            out.append(static_cast<size_t>(depth) * 4, ' ');
        } else if (prev && noSpaceAfter(*prev)) {
        } else if (sourceSpacing(s) || (prev && (*prev == "<" || *prev == "::"))) {
            if (tokens[i].spaceBefore)
                out.push_back(' ');
        } else if (!noSpaceBefore(s) && !accessLabel) {
            out.push_back(' ');
        }
        out += s;
        lineStart = false;

        const bool pragmaEnd = s == ")" && i >= 3 && tokens[i - 3].text == "_Pragma";
        if (s == "{") {
            ++depth;
            newline();
        } else if (s == ";" || accessLabel || pragmaEnd) {
            newline();
        } else if (s == "}" && !(next && (*next == ";" || *next == ","))) {
            newline();
        }
    }
    while (!out.empty() && out.back() == '\n')
        out.pop_back();
    return out;
}

} // namespace vx
