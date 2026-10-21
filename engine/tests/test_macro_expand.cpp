// Tests for the preprocessor-style expander, independent of libclang.
#include "macro_expand.h"

#include <cstdio>
#include <map>
#include <string>
#include <vector>

namespace {

int failures = 0;

// Splits on spaces; a token preceded by a space gets spaceBefore.
std::vector<vx::PPToken> lex(const std::string& s)
{
    std::vector<vx::PPToken> out;
    bool space = false;
    size_t i = 0;
    while (i < s.size()) {
        if (s[i] == ' ') {
            space = true;
            ++i;
            continue;
        }
        size_t j = s.find(' ', i);
        if (j == std::string::npos)
            j = s.size();
        vx::PPToken t;
        t.text = s.substr(i, j - i);
        t.ident = vx::isIdentifier(t.text);
        t.spaceBefore = space && !out.empty();
        out.push_back(t);
        space = false;
        i = j;
    }
    return out;
}

class Table : public vx::MacroTable {
public:
    void object(const std::string& name, const std::string& body) { defs_[name] = {false, false, {}, lex(body)}; }
    void function(const std::string& name, std::vector<std::string> params, const std::string& body)
    {
        const bool variadic = !params.empty() && params.back() == "__VA_ARGS__";
        defs_[name] = {true, variadic, std::move(params), lex(body)};
    }
    const vx::MacroDef* find(const std::string& name) override
    {
        auto it = defs_.find(name);
        return it == defs_.end() ? nullptr : &it->second;
    }

private:
    std::map<std::string, vx::MacroDef> defs_;
};

std::string join(const std::vector<vx::PPToken>& toks)
{
    std::string s;
    for (auto& t : toks) {
        if (!s.empty())
            s.push_back(' ');
        s += t.text;
    }
    return s;
}

void check(const char* name, Table& table, const std::string& input, const std::string& expected, unsigned line = 1)
{
    vx::MacroExpander e(table, line, "f.h");
    std::string got = join(e.expand(lex(input)));
    if (got == expected)
        return;
    ++failures;
    std::printf("FAIL %s\n  got:      %s\n  expected: %s\n", name, got.c_str(), expected.c_str());
}

} // namespace

int main()
{
    Table t;
    t.object("A", "B + 1");
    t.object("B", "2");
    check("object chain", t, "A", "2 + 1");

    t.object("SELF", "SELF + 1");
    check("self reference stops", t, "SELF", "SELF + 1");

    t.function("F", {"x"}, "( x * x )");
    check("function-like", t, "F ( A )", "( 2 + 1 * 2 + 1 )");
    check("name without parens", t, "F + 1", "F + 1");
    check("nested parens in args", t, "F ( ( 1 , 2 ) )", "( ( 1 , 2 ) * ( 1 , 2 ) )");

    t.function("STR", {"x"}, "# x");
    check("stringize is not expanded", t, "STR ( A )", "\"A\"");

    t.function("CAT", {"a", "b"}, "a ## b");
    check("paste", t, "CAT ( foo , bar )", "foobar");
    check("paste empty left", t, "CAT ( , bar )", "bar");
    check("paste does not expand args", t, "CAT ( A , B )", "AB");

    t.function("INNER", {"A", "B", "C", "D"}, "A ## B ## C ## D");
    t.function("COMBINE", {"A", "B", "C", "D"}, "INNER ( A , B , C , D )");
    t.object("FILE_ID", "My_h");
    t.function("BODY", {"__VA_ARGS__"}, "COMBINE ( FILE_ID , _ , __LINE__ , _BODY ) ;");
    t.object("My_h_42_BODY", "int generated ;");
    check("generated body chain", t, "BODY ( )", "int generated ; ;", 42);

    t.function("V", {"x", "__VA_ARGS__"}, "x : __VA_ARGS__");
    check("variadic", t, "V ( 1 , 2 , 3 )", "1 : 2 , 3");

    t.object("LATE", "F");
    check("rescan picks up following args", t, "LATE ( 3 )", "( 3 * 3 )");

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
