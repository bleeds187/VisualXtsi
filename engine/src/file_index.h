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

enum class Change { Added, Removed, Modified };
using Listener = void (*)(const std::wstring& path, Change change);

// Called (from background threads) for every indexed file as it is found, removed or
// changed on disk. One listener; setting it replays Added for every file already indexed.
void setListener(Listener listener);

// Adds the engine source tree an Unreal project builds against (from its discovered include
// paths, the folder ending in Engine\Source). Returns false if none was found.
bool addEngineOf(const std::wstring& path, const std::vector<std::wstring>& includeDirs);

} // namespace vx::files
