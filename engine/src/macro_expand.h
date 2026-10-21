#pragma once

#include <deque>
#include <set>
#include <string>
#include <vector>

namespace vx {

struct PPToken {
    std::string text;
    bool ident = false;
    bool spaceBefore = false;
    bool placemarker = false;
    std::set<std::string> hide; // names that must not expand again (Prosser hide set)
};

struct MacroDef {
    bool functionLike = false;
    bool variadic = false;            // last param is __VA_ARGS__ (or named ...)
    std::vector<std::string> params;  // includes __VA_ARGS__ when variadic
    std::vector<PPToken> body;
};

class MacroTable {
public:
    virtual ~MacroTable() = default;
    virtual const MacroDef* find(const std::string& name) = 0;
};

// Expands a token sequence the way the C preprocessor would, using definitions
// from `table`. __LINE__ and __FILE__ refer to the outermost expansion site.
class MacroExpander {
public:
    MacroExpander(MacroTable& table, unsigned line, std::string file)
        : table_(table), line_(line), file_(std::move(file)) {}

    std::vector<PPToken> expand(std::vector<PPToken> input);
    bool truncated() const { return truncated_; }

private:
    std::vector<PPToken> expandList(std::deque<PPToken> work);
    std::vector<PPToken> substitute(const MacroDef& def, const std::vector<std::vector<PPToken>>& args,
                                    const std::set<std::string>& hide);
    int paramIndex(const MacroDef& def, const PPToken& t) const;

    MacroTable& table_;
    unsigned line_;
    std::string file_;
    unsigned counter_ = 0;
    size_t steps_ = 0;
    bool truncated_ = false;
};

bool isIdentifier(const std::string& text);

// Joins tokens into readable C++: spaces where needed, newlines after ; { }
// and access specifiers, indented by brace depth.
std::string formatTokens(const std::vector<PPToken>& tokens);

} // namespace vx
