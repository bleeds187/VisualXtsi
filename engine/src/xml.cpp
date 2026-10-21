#include "xml.h"

#include <cstdlib>

namespace vx::xml {

namespace {

class Parser {
public:
    explicit Parser(std::string_view s) : s_(s) {}

    std::unique_ptr<Element> document()
    {
        if (s_.substr(0, 3) == "\xEF\xBB\xBF")
            pos_ = 3;
        std::unique_ptr<Element> root;
        while (skipMisc()) {
            if (root)
                return nullptr; // two roots
            root = element();
            if (!root)
                return nullptr;
        }
        return root;
    }

private:
    bool startsWith(std::string_view p) const { return s_.substr(pos_, p.size()) == p; }

    void skipSpace()
    {
        while (pos_ < s_.size() && (s_[pos_] == ' ' || s_[pos_] == '\t' || s_[pos_] == '\r' || s_[pos_] == '\n'))
            ++pos_;
    }

    bool skipPast(std::string_view terminator)
    {
        size_t e = s_.find(terminator, pos_);
        if (e == std::string_view::npos)
            return false;
        pos_ = e + terminator.size();
        return true;
    }

    // Skips whitespace, comments, PIs and DOCTYPE. Returns true if an element follows.
    bool skipMisc()
    {
        for (;;) {
            skipSpace();
            if (startsWith("<!--")) {
                if (!skipPast("-->"))
                    return false;
            } else if (startsWith("<?")) {
                if (!skipPast("?>"))
                    return false;
            } else if (startsWith("<!")) {
                if (!skipPast(">"))
                    return false;
            } else {
                return startsWith("<");
            }
        }
    }

    std::string name()
    {
        size_t start = pos_;
        while (pos_ < s_.size() && !strchr(" \t\r\n/>=", s_[pos_]))
            ++pos_;
        std::string n(s_.substr(start, pos_ - start));
        if (size_t colon = n.find(':'); colon != std::string::npos)
            n.erase(0, colon + 1);
        return n;
    }

    static void appendDecoded(std::string& out, std::string_view raw)
    {
        for (size_t i = 0; i < raw.size(); ++i) {
            if (raw[i] != '&') {
                out.push_back(raw[i]);
                continue;
            }
            size_t semi = raw.find(';', i);
            if (semi == std::string_view::npos) {
                out.push_back('&');
                continue;
            }
            std::string_view ent = raw.substr(i + 1, semi - i - 1);
            if (ent == "amp")
                out.push_back('&');
            else if (ent == "lt")
                out.push_back('<');
            else if (ent == "gt")
                out.push_back('>');
            else if (ent == "quot")
                out.push_back('"');
            else if (ent == "apos")
                out.push_back('\'');
            else if (!ent.empty() && ent[0] == '#') {
                unsigned long cp = ent.size() > 1 && (ent[1] == 'x' || ent[1] == 'X')
                                       ? std::strtoul(std::string(ent.substr(2)).c_str(), nullptr, 16)
                                       : std::strtoul(std::string(ent.substr(1)).c_str(), nullptr, 10);
                if (cp < 0x80) {
                    out.push_back(static_cast<char>(cp));
                } else if (cp < 0x800) {
                    out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
                    out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                } else {
                    out.push_back(static_cast<char>(0xE0 | ((cp >> 12) & 0x0F)));
                    out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
                    out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                }
            } else {
                out.append(raw.substr(i, semi - i + 1));
            }
            i = semi;
        }
    }

    std::unique_ptr<Element> element()
    {
        ++pos_; // '<'
        auto el = std::make_unique<Element>();
        el->name = name();
        for (;;) {
            skipSpace();
            if (pos_ >= s_.size())
                return nullptr;
            if (startsWith("/>")) {
                pos_ += 2;
                return el;
            }
            if (s_[pos_] == '>') {
                ++pos_;
                break;
            }
            std::string key = name();
            skipSpace();
            if (pos_ >= s_.size() || s_[pos_] != '=')
                return nullptr;
            ++pos_;
            skipSpace();
            if (pos_ >= s_.size() || (s_[pos_] != '"' && s_[pos_] != '\''))
                return nullptr;
            const char quote = s_[pos_++];
            size_t end = s_.find(quote, pos_);
            if (end == std::string_view::npos)
                return nullptr;
            std::string value;
            appendDecoded(value, s_.substr(pos_, end - pos_));
            pos_ = end + 1;
            el->attributes.emplace_back(std::move(key), std::move(value));
        }
        // Content.
        for (;;) {
            size_t lt = s_.find('<', pos_);
            if (lt == std::string_view::npos)
                return nullptr;
            appendDecoded(el->text, s_.substr(pos_, lt - pos_));
            pos_ = lt;
            if (startsWith("</")) {
                return skipPast(">") ? std::move(el) : nullptr;
            } else if (startsWith("<!--")) {
                if (!skipPast("-->"))
                    return nullptr;
            } else if (startsWith("<![CDATA[")) {
                size_t start = pos_ + 9;
                size_t end = s_.find("]]>", start);
                if (end == std::string_view::npos)
                    return nullptr;
                el->text.append(s_.substr(start, end - start));
                pos_ = end + 3;
            } else if (startsWith("<?")) {
                if (!skipPast("?>"))
                    return nullptr;
            } else {
                auto child = element();
                if (!child)
                    return nullptr;
                el->children.push_back(std::move(child));
            }
        }
    }

    std::string_view s_;
    size_t pos_ = 0;
};

} // namespace

const std::string* Element::attribute(std::string_view key) const
{
    for (auto& [k, v] : attributes)
        if (k == key)
            return &v;
    return nullptr;
}

const Element* Element::child(std::string_view childName) const
{
    for (auto& c : children)
        if (c->name == childName)
            return c.get();
    return nullptr;
}

std::unique_ptr<Element> parse(std::string_view document)
{
    return Parser(document).document();
}

} // namespace vx::xml
