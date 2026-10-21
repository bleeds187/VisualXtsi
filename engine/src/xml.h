#pragma once

#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace vx::xml {

// Minimal XML DOM: enough for MSBuild project files (elements, attributes,
// text, comments, CDATA, processing instructions and the five named entities).
struct Element {
    std::string name; // local name (namespace prefix stripped)
    std::vector<std::pair<std::string, std::string>> attributes;
    std::vector<std::unique_ptr<Element>> children;
    std::string text; // concatenated character data directly inside this element

    const std::string* attribute(std::string_view key) const;
    const Element* child(std::string_view childName) const;
};

// Returns the root element, or null when the document is malformed.
std::unique_ptr<Element> parse(std::string_view document);

} // namespace vx::xml
