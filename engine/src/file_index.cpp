#include "file_index.h"

#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <filesystem>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <thread>
#include <unordered_map>

namespace fs = std::filesystem;

namespace vx::files {

namespace {

const wchar_t* const kExtensions[] = {L".h",   L".hpp", L".hh",  L".hxx", L".inl",      L".ipp",      L".cpp",
                                      L".cc",  L".cxx", L".c",   L".ixx", L".cppm",     L".cs",       L".ini",
                                      L".usf", L".ush", L".hlsl", L".json", L".uproject", L".uplugin"};
const wchar_t* const kSkippedDirs[] = {L"intermediate", L"binaries", L"saved",    L"deriveddatacache", L"content",
                                       L".git",         L".vs",      L".vscode", L".idea",            L"node_modules"};

std::wstring lower(std::wstring s)
{
    for (auto& c : s)
        c = static_cast<wchar_t>(towlower(c));
    return s;
}

bool wantedFile(const fs::path& p)
{
    const std::wstring ext = lower(p.extension().wstring());
    return std::any_of(std::begin(kExtensions), std::end(kExtensions), [&](const wchar_t* e) { return ext == e; });
}

bool skippedDir(const std::wstring& name)
{
    const std::wstring n = lower(name);
    return std::any_of(std::begin(kSkippedDirs), std::end(kSkippedDirs), [&](const wchar_t* d) { return n == d; });
}

// True if any folder of `p` below `root` is one we skip.
bool underSkippedDir(const fs::path& root, const fs::path& p)
{
    const fs::path rel = p.lexically_relative(root).parent_path();
    for (const fs::path& part : rel)
        if (skippedDir(part.wstring()))
            return true;
    return false;
}

uint64_t charMask(const std::wstring& lowerText)
{
    uint64_t mask = 0;
    for (wchar_t c : lowerText)
        mask |= uint64_t(1) << (c < 128 ? (c & 63) : 63);
    return mask;
}

struct Entry {
    std::wstring path;
    std::wstring name;
    std::wstring nameLower;
    std::wstring pathLower;
    uint64_t nameMask = 0;
    uint64_t pathMask = 0;
    bool alive = true;
};

class Index {
public:
    void add(const fs::path& p)
    {
        Entry e;
        e.path = p.lexically_normal().wstring();
        e.pathLower = lower(e.path);
        e.name = p.filename().wstring();
        e.nameLower = lower(e.name);
        e.nameMask = charMask(e.nameLower);
        e.pathMask = charMask(e.pathLower);
        std::unique_lock lock(mutex_);
        auto [it, inserted] = byPath_.try_emplace(e.pathLower, entries_.size());
        if (!inserted) {
            if (!entries_[it->second].alive) {
                entries_[it->second].alive = true;
                ++alive_;
            }
            return;
        }
        entries_.push_back(std::move(e));
        ++alive_;
    }

    void remove(const fs::path& p)
    {
        const std::wstring key = lower(p.lexically_normal().wstring());
        std::unique_lock lock(mutex_);
        if (auto it = byPath_.find(key); it != byPath_.end() && entries_[it->second].alive) {
            entries_[it->second].alive = false;
            --alive_;
        }
    }

    void removeUnder(const fs::path& dir)
    {
        const std::wstring prefix = lower(dir.lexically_normal().wstring()) + L"\\";
        std::unique_lock lock(mutex_);
        for (Entry& e : entries_) {
            if (e.alive && e.pathLower.compare(0, prefix.size(), prefix) == 0) {
                e.alive = false;
                --alive_;
            }
        }
    }

    size_t count() const
    {
        std::shared_lock lock(mutex_);
        return alive_;
    }

    std::vector<std::wstring> find(const std::wstring& query, size_t maxResults) const
    {
        const std::wstring q = lower(query);
        const bool byPath = q.find_first_of(L"\\/") != std::wstring::npos;
        std::wstring normalized = q;
        std::replace(normalized.begin(), normalized.end(), L'/', L'\\');
        const uint64_t qmask = charMask(normalized);
        std::vector<std::pair<int, const Entry*>> hits;
        std::shared_lock lock(mutex_);
        for (const Entry& e : entries_) {
            if (!e.alive || ((byPath ? e.pathMask : e.nameMask) & qmask) != qmask)
                continue;
            int s = byPath ? score(normalized, e.pathLower, e.path) : score(normalized, e.nameLower, e.name);
            if (s >= 0)
                hits.emplace_back(s, &e);
        }
        const size_t keep = std::min(maxResults, hits.size());
        std::partial_sort(hits.begin(), hits.begin() + keep, hits.end(), [](const auto& a, const auto& b) {
            return a.first != b.first ? a.first > b.first : a.second->path.size() < b.second->path.size();
        });
        std::vector<std::wstring> result;
        for (size_t i = 0; i < keep; ++i)
            result.push_back(hits[i].second->path);
        return result;
    }

    void clear()
    {
        std::unique_lock lock(mutex_);
        entries_.clear();
        byPath_.clear();
        alive_ = 0;
    }

private:
    mutable std::shared_mutex mutex_;
    std::vector<Entry> entries_;
    std::unordered_map<std::wstring, size_t> byPath_;
    size_t alive_ = 0;
};

Index g_index;

void scan(const fs::path& root, const fs::path& dir, const std::atomic<bool>& stop)
{
    std::error_code ec;
    for (auto it = fs::recursive_directory_iterator(dir, fs::directory_options::skip_permission_denied, ec);
         it != fs::recursive_directory_iterator() && !stop; it.increment(ec)) {
        if (ec)
            break;
        if (it->is_directory(ec)) {
            if (skippedDir(it->path().filename().wstring()))
                it.disable_recursion_pending();
        } else if (wantedFile(it->path()) && !underSkippedDir(root, it->path())) {
            g_index.add(it->path());
        }
    }
}

// One indexed folder: an initial scan, then change notifications until stopped.
class Root {
public:
    explicit Root(fs::path dir) : dir_(std::move(dir))
    {
        thread_ = std::thread([this] { run(); });
    }

    ~Root()
    {
        stop_ = true;
        // The worker blocks in ReadDirectoryChangesW: cancel its synchronous I/O.
        for (int i = 0; i < 100 && thread_.joinable(); ++i) {
            CancelSynchronousIo(thread_.native_handle());
            if (WaitForSingleObject(thread_.native_handle(), 20) == WAIT_OBJECT_0)
                break;
        }
        if (thread_.joinable())
            thread_.join();
    }

    const fs::path& dir() const { return dir_; }

private:
    void run()
    {
        // Watch first, so nothing created during the initial scan is missed.
        HANDLE handle = CreateFileW(dir_.c_str(), FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                    nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        scan(dir_, dir_, stop_);
        if (handle == INVALID_HANDLE_VALUE)
            return;
        std::vector<char> buffer(64 * 1024);
        while (!stop_) {
            DWORD bytes = 0;
            if (!ReadDirectoryChangesW(handle, buffer.data(), static_cast<DWORD>(buffer.size()), TRUE,
                                       FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME, &bytes, nullptr,
                                       nullptr))
                break; // cancelled, or the folder went away
            if (stop_)
                break;
            if (bytes == 0) { // too many changes to report: look again
                scan(dir_, dir_, stop_);
                continue;
            }
            for (size_t offset = 0;;) {
                const auto* info = reinterpret_cast<const FILE_NOTIFY_INFORMATION*>(buffer.data() + offset);
                apply(info->Action, dir_ / std::wstring(info->FileName, info->FileNameLength / sizeof(wchar_t)));
                if (info->NextEntryOffset == 0)
                    break;
                offset += info->NextEntryOffset;
            }
        }
        CloseHandle(handle);
    }

    void apply(DWORD action, const fs::path& p)
    {
        if (underSkippedDir(dir_, p))
            return;
        std::error_code ec;
        switch (action) {
        case FILE_ACTION_ADDED:
        case FILE_ACTION_RENAMED_NEW_NAME:
            if (fs::is_directory(p, ec)) {
                if (!skippedDir(p.filename().wstring()))
                    scan(dir_, p, stop_); // a folder moved or copied in
            } else if (wantedFile(p)) {
                g_index.add(p);
            }
            break;
        case FILE_ACTION_REMOVED:
        case FILE_ACTION_RENAMED_OLD_NAME:
            g_index.remove(p);
            g_index.removeUnder(p); // in case it was a folder
            break;
        default:
            break;
        }
    }

    fs::path dir_;
    std::atomic<bool> stop_{false};
    std::thread thread_;
};

std::mutex g_rootsMutex;
std::vector<std::unique_ptr<Root>> g_roots;

bool isWithin(const fs::path& inner, const fs::path& outer)
{
    const std::wstring a = lower(inner.lexically_normal().wstring()) + L"\\";
    const std::wstring b = lower(outer.lexically_normal().wstring()) + L"\\";
    return a.compare(0, b.size(), b) == 0;
}

// Word starts: after a separator, or CamelCase / letter-digit transitions in the original text.
bool isBoundary(const std::wstring& original, size_t i)
{
    if (i == 0)
        return true;
    const wchar_t prev = original[i - 1], cur = original[i];
    if (prev == L'_' || prev == L'-' || prev == L'.' || prev == L' ' || prev == L'\\' || prev == L'/')
        return true;
    if (iswupper(cur) && iswlower(prev))
        return true;
    return iswdigit(cur) != iswdigit(prev);
}

// One greedy left-to-right subsequence pass. With `wordStartsFirst`, each query character
// first looks for its next occurrence at a word start. Returns -1 if `query` is not a
// subsequence.
int greedyScore(const std::wstring& query, const std::wstring& candidate, const std::wstring& original,
                bool wordStartsFirst)
{
    // The extension (".cpp") is not a word: matching its letters earns no word-start bonus.
    const size_t dot = candidate.rfind(L'.');
    const size_t extensionStart = dot == std::wstring::npos || dot == 0 ? candidate.size() : dot;
    auto wordStart = [&](size_t i) { return i <= extensionStart && isBoundary(original, i); };
    int total = 0;
    size_t previous = std::wstring::npos;
    size_t from = 0;
    for (wchar_t qc : query) {
        size_t hit = std::wstring::npos;
        if (wordStartsFirst)
            for (size_t i = from; i < candidate.size(); ++i)
                if (candidate[i] == qc && wordStart(i)) {
                    hit = i;
                    break;
                }
        if (hit == std::wstring::npos)
            hit = candidate.find(qc, from);
        if (hit == std::wstring::npos)
            return -1;
        int bonus = 1;
        if (previous != std::wstring::npos && hit == previous + 1)
            bonus += 6;
        if (hit == 0) {
            bonus += 10;
        } else if (wordStart(hit)) {
            bonus += 7;
            // Skipping words costs a little: "lhc" prefers LyraHeroComponent to LyraTextHotfixConfig.
            const size_t begin = previous == std::wstring::npos ? 0 : previous + 1;
            for (size_t i = begin + 1; i < hit; ++i)
                if (wordStart(i))
                    bonus -= 2;
        }
        total += bonus;
        previous = hit;
        from = hit + 1;
    }
    return total;
}

} // namespace

int score(const std::wstring& query, const std::wstring& candidate, const std::wstring& original)
{
    if (query.empty())
        return 0;
    int total = std::max(greedyScore(query, candidate, original, false), greedyScore(query, candidate, original, true));
    if (total < 0)
        return -1;
    // Prefer exact and prefix matches, then shorter candidates.
    const size_t dot = candidate.rfind(L'.');
    const std::wstring stem = candidate.substr(0, dot);
    if (stem == query || candidate == query)
        total += 60;
    else if (candidate.compare(0, query.size(), query) == 0)
        total += 25;
    total -= static_cast<int>(candidate.size() / 8);
    return std::max(total, 0);
}

void addRoot(const std::wstring& dirText)
{
    std::error_code ec;
    const fs::path dir = fs::path(dirText).lexically_normal();
    if (!fs::is_directory(dir, ec))
        return;
    std::lock_guard lock(g_rootsMutex);
    for (const auto& root : g_roots)
        if (isWithin(dir, root->dir()))
            return;
    std::erase_if(g_roots, [&](const std::unique_ptr<Root>& root) { return isWithin(root->dir(), dir); });
    g_roots.push_back(std::make_unique<Root>(dir));
}

bool addProjectOf(const std::wstring& path)
{
    std::error_code ec;
    for (fs::path dir = fs::path(path).parent_path(); !dir.empty(); dir = dir.parent_path()) {
        for (auto& e : fs::directory_iterator(dir, ec)) {
            if (lower(e.path().extension().wstring()) == L".uproject") {
                addRoot(dir.wstring());
                return true;
            }
        }
        if (dir == dir.root_path())
            break;
    }
    return false;
}

size_t count()
{
    return g_index.count();
}

std::vector<std::wstring> find(const std::wstring& query, size_t maxResults)
{
    return g_index.find(query, maxResults);
}

void reset()
{
    std::vector<std::unique_ptr<Root>> roots;
    {
        std::lock_guard lock(g_rootsMutex);
        roots.swap(g_roots);
    }
    roots.clear(); // stops and joins the watchers
    g_index.clear();
}

} // namespace vx::files
