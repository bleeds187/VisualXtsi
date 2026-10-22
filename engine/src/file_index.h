#pragma once

#include <cstddef>
#include <string>
#include <vector>

// The file index behind Open File in Solution: source and config files under a set of
// root folders, scanned in the background and kept current with directory change
// notifications. Paths are UTF-16 (Windows wide strings).
namespace vx::files {

// Adds a root (ignored if it lies inside an existing one; replaces roots inside it).
void addRoot(const std::wstring& dir);

// Adds the Unreal project containing `path` (the folder with the nearest *.uproject).
// Returns false when `path` is not inside one.
bool addProjectOf(const std::wstring& path);

size_t count();

// Best matches for a fuzzy `query` (case-insensitive subsequence; a query containing a
// path separator is matched against the whole path), best first.
std::vector<std::wstring> find(const std::wstring& query, size_t maxResults);

// Fuzzy score of a lowercase `query` against a lowercase `candidate` whose original
// spelling (for CamelCase word starts) is `original`; negative when it does not match.
int score(const std::wstring& query, const std::wstring& candidate, const std::wstring& original);

// Stops all watchers and forgets everything.
void reset();

} // namespace vx::files
