#pragma once

#include "symbol_scanner.h"

#include <cstddef>
#include <string>
#include <vector>

// The symbol index behind Find Symbol and cross-file Go to Definition: declarations of every
// C/C++ file in the file index, scanned by background workers, kept current as files change,
// and persisted (with each file's time stamp and size) so a restart only rescans what changed.
namespace vx::symbols {

// Starts following the file index (idempotent). The cache lives in
// %LOCALAPPDATA%\VisualXtsi\symbols.cache (VX_SYMBOL_CACHE overrides the file).
void start();

struct Hit {
    Symbol symbol;
    std::wstring path;
};

// Fuzzy search over symbol names, best first; "Scope::name" also matches the scope.
std::vector<Hit> find(const std::u16string& query, size_t maxResults);

// Exact-name lookup (case-sensitive), definitions first; for Go to Definition.
std::vector<Hit> lookup(const std::u16string& name, size_t maxResults);

struct Status {
    size_t files = 0;   // files with symbols
    size_t pending = 0; // files waiting to be scanned
    size_t symbols = 0;
};
Status status();

// Writes the cache now (it is also written whenever the work queue drains).
void save();

// Forgets every symbol and the cache file, then rescans every indexed file.
void rebuild();

// Stops following the file index and forgets everything (tests).
void reset();

} // namespace vx::symbols
