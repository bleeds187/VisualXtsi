/* VisualXtsi engine: flat C ABI.
 * All text offsets and lengths are in UTF-16 code units. */
#ifndef VX_VX_H
#define VX_VX_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VX_BUILDING_ENGINE
#    define VX_API __declspec(dllexport)
#  else
#    define VX_API __declspec(dllimport)
#  endif
#  define VX_CALL __cdecl
#else
#  define VX_API __attribute__((visibility("default")))
#  define VX_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define VX_ABI_VERSION 12u

typedef enum vx_kind {
    VX_KIND_PREPROCESSOR = 1, /* '#' plus directive name, e.g. "#define" */
    VX_KIND_MACRO = 2,        /* identifier #defined in the file, or ALL_CAPS */

    /* Semantic kinds (vx_semantic_tokens) */
    VX_KIND_LOCAL = 10,          /* local variable */
    VX_KIND_PARAMETER = 11,      /* function parameter or non-type template parameter */
    VX_KIND_FIELD = 12,          /* non-static data member */
    VX_KIND_GLOBAL = 13,         /* namespace-scope, static or static-member variable */
    VX_KIND_TYPE = 14,           /* class, struct, union, enum, typedef, alias, template type */
    VX_KIND_ENUM_CONSTANT = 15,
    VX_KIND_NAMESPACE = 16,
    VX_KIND_FUNCTION = 17,       /* free function */
    VX_KIND_METHOD = 18,         /* member function */
    VX_KIND_STATIC_METHOD = 19,
    VX_KIND_VIRTUAL_METHOD = 20
} vx_kind;

typedef struct vx_span {
    uint32_t start;
    uint32_t length;
    uint32_t kind; /* vx_kind */
} vx_span;

VX_API uint32_t VX_CALL vx_abi_version(void);

/* Lexically classifies a whole buffer. Writes at most `cap` spans to `out`
 * (sorted by start, non-overlapping) and returns the total number of spans
 * found. If the return value exceeds `cap`, call again with a larger buffer. */
VX_API uint32_t VX_CALL vx_classify(const char16_t* text, uint32_t len,
                                    vx_span* out, uint32_t cap);

typedef enum vx_status {
    VX_OK = 0,
    VX_ERR_ARGS = 1,
    VX_ERR_NOT_AVAILABLE = 2, /* libclang.dll could not be loaded */
    VX_ERR_PARSE = 3
} vx_status;

typedef enum vx_args_source {
    VX_ARGS_NONE = 0,             /* nothing found; defaults are used */
    VX_ARGS_COMPILE_COMMANDS = 1, /* compile_commands.json; argv[0] is the compiler */
    VX_ARGS_VCXPROJ = 2           /* Unreal Build Tool generated .vcxproj */
} vx_args_source;

/* Finds compile arguments for `path`: compile_commands.json in a parent
 * directory when it lists the file, else the generated .vcxproj of the Unreal
 * project (or engine) containing it (UE 4.27 and UE5 layouts; headers use the
 * arguments of the nearest source file of their module). Writes the arguments
 * as NUL-terminated UTF-16 strings to `out` and returns the number of char16_t
 * needed; `*source` receives a vx_args_source. */
VX_API uint32_t VX_CALL vx_discover_args(const char16_t* path, char16_t* out, uint32_t cap,
                                         uint32_t* source);

/* Parses `path` with libclang, or reparses it if it is already cached with the
 * same arguments (using the precompiled preamble). Arguments come from `args`
 * when argc > 0, else from compile_commands.json in `compdb_dir` when given,
 * else from vx_discover_args, else from defaults. `unsaved` (may be null) is
 * the current editor text of `path`. Safe to call from a background thread;
 * concurrent vx_macro_info calls on the same file return 0 instead of waiting.
 * Returns a vx_status. */
VX_API int32_t VX_CALL vx_parse(const char16_t* path, const char16_t* const* args, uint32_t argc,
                                const char16_t* compdb_dir, const char16_t* unsaved,
                                uint32_t unsaved_len);

/* Describes the macro used or defined at a 1-based line and UTF-16 column of a
 * parsed file. Writes four NUL-terminated UTF-16 strings to `out`: name,
 * definition text, definition location, and the fully expanded invocation
 * (empty when hovering the #define itself; a comment when it expands to
 * nothing). Returns the number of char16_t needed,
 * or 0 if there is no macro there. */
VX_API uint32_t VX_CALL vx_macro_info(const char16_t* path, uint32_t line, uint32_t column,
                                      char16_t* out, uint32_t cap);

/* Semantic classification of the identifiers in a parsed file (its own text
 * only, not included headers; tokens produced by macro expansion are left to
 * the lexical classifier). Offsets are UTF-16 units into the text that was
 * parsed. Writes at most `cap` spans (sorted, non-overlapping) and returns the
 * total. Results are cached until the next vx_parse. Blocks while the file is
 * being parsed, so call it from a background thread. */
VX_API uint32_t VX_CALL vx_semantic_tokens(const char16_t* path, vx_span* out, uint32_t cap);

typedef enum vx_pch_status {
    VX_PCH_READY = 0,          /* a current shared PCH exists (built now or earlier) */
    VX_PCH_NOT_APPLICABLE = 1, /* not part of an Unreal module, or too few shared headers */
    VX_PCH_FAILED = 2,         /* the module's common headers could not be precompiled */
    VX_PCH_BUSY = 3            /* another thread is building it */
} vx_pch_status;

/* Builds, if needed, the shared precompiled header of the Unreal module that
 * contains `path`: the engine and plugin headers most of the module's source
 * files include, precompiled once and cached under
 * %LOCALAPPDATA%\VisualXtsi\pch (least recently used entries are evicted past
 * VX_PCH_CACHE_MB, default 4096). New translation units of the module's files
 * then start from it. It is rebuilt when the module's arguments or any header
 * in it changes. Slow (seconds): call from a background thread. Returns a
 * vx_pch_status. */
VX_API int32_t VX_CALL vx_prepare_module_pch(const char16_t* path);

typedef struct vx_config {
    uint32_t skip_header_bodies; /* nonzero: skip function bodies in included headers (default 1) */
    uint32_t shared_pch;         /* nonzero: build and use per-module shared PCHs (default 1) */
    uint32_t pch_cache_mb;       /* PCH cache size limit in MB; 0 keeps the default (4096) */
    uint32_t args_source;        /* where discovered arguments come from: 0 compile_commands.json,
                                    else Unreal project files (default); 1 compile_commands.json
                                    only; 2 Unreal project files only */
} vx_config;

/* Applies settings to parses started afterwards. Environment variables override
 * them: VX_FULL_PREAMBLE=1 (no body skipping), VX_PCH_CACHE_MB, VX_PCH_DIR. */
VX_API void VX_CALL vx_configure(const vx_config* config);

/* Deletes every cached PCH that is not in use and forgets their state. Returns
 * the number of bytes freed. */
VX_API uint64_t VX_CALL vx_clear_pch_cache(void);

/* libclang's version string ("clang version 19.1.5"), NUL-terminated; returns the
 * number of char16_t needed (0: libclang unavailable). */
VX_API uint32_t VX_CALL vx_clang_version(char16_t* out, uint32_t cap);

/* Files to switch to from `path`: sources for a header, headers for a source,
 * best first (same folder; Unreal's mirrored Public/Classes <-> Private layout;
 * elsewhere in the module, i.e. under the folder holding its *.Build.cs; for a
 * source, a same-named #include resolved through the module's include paths).
 * Writes NUL-terminated UTF-16 paths to `out` and returns the number of
 * char16_t needed (0: none found). Touches the file system: call off the UI
 * thread. */
VX_API uint32_t VX_CALL vx_counterparts(const char16_t* path, char16_t* out, uint32_t cap);

/* File index for Open File in Solution. Roots are scanned on background threads
 * (skipping Intermediate, Binaries, Saved, DerivedDataCache, Content, .git, .vs)
 * and kept current with directory change notifications; searches never wait for
 * a scan and see whatever is indexed so far. */
VX_API void VX_CALL vx_index_add_root(const char16_t* dir);
/* Adds the Unreal project containing `path` (nearest folder with a *.uproject);
 * returns 1 if there is one. */
VX_API uint32_t VX_CALL vx_index_add_project_of(const char16_t* path);
VX_API uint32_t VX_CALL vx_index_file_count(void);
/* Fuzzy search (subsequence, case-insensitive, word starts and runs score higher;
 * a query with a path separator matches whole paths). Writes up to `max_results`
 * NUL-terminated UTF-16 paths, best first; returns the number of char16_t needed. */
VX_API uint32_t VX_CALL vx_index_find_files(const char16_t* query, uint32_t max_results, char16_t* out,
                                            uint32_t cap);
/* Adds the engine source tree (…\Engine\Source) that the Unreal project
 * containing `path` builds against; returns 1 if found. Not done by default. */
VX_API uint32_t VX_CALL vx_index_add_engine_of(const char16_t* path);
/* Stops watching and forgets all roots, files and symbols. */
VX_API void VX_CALL vx_index_reset(void);

/* Symbol index (Find Symbol, cross-file Go to Definition): declarations in every
 * C/C++ file of the file index, found by a fast lexical scanner on background
 * threads, kept current as files change, and cached in
 * %LOCALAPPDATA%\VisualXtsi\symbols.cache (VX_SYMBOL_CACHE overrides) so that
 * only changed files are rescanned after a restart. */
typedef enum vx_symbol_kind {
    VX_SYMBOL_NAMESPACE = 1,
    VX_SYMBOL_CLASS = 2,
    VX_SYMBOL_STRUCT = 3,
    VX_SYMBOL_UNION = 4,
    VX_SYMBOL_ENUM = 5,
    VX_SYMBOL_ENUM_CONSTANT = 6,
    VX_SYMBOL_TYPEDEF = 7,
    VX_SYMBOL_FUNCTION = 8,
    VX_SYMBOL_METHOD = 9,
    VX_SYMBOL_FIELD = 10,
    VX_SYMBOL_VARIABLE = 11,
    VX_SYMBOL_MACRO = 12,
    VX_SYMBOL_DELEGATE = 13 /* Unreal DECLARE_*DELEGATE* / DECLARE_*EVENT* types */
} vx_symbol_kind;

/* Starts following the file index (idempotent). */
VX_API void VX_CALL vx_symbols_start(void);
/* Fuzzy search over symbol names, best first ("Scope::name" also matches the
 * scope). Each result is a NUL-terminated UTF-16 record of tab-separated fields:
 * kind, definition (0/1), line, column (1-based, UTF-16), name, scope, path.
 * Returns the number of char16_t needed. */
VX_API uint32_t VX_CALL vx_symbols_find(const char16_t* query, uint32_t max_results, char16_t* out, uint32_t cap);
VX_API void VX_CALL vx_symbols_status(uint32_t* files, uint32_t* pending, uint32_t* symbols);
/* Writes the cache now (it is also written whenever the scan queue drains). */
VX_API void VX_CALL vx_symbols_save(void);
/* Forgets every symbol and deletes the cache, then rescans every indexed file
 * in the background (starting the index if needed). */
VX_API void VX_CALL vx_symbols_rebuild(void);

/* Where the symbol at a 1-based line and UTF-16 column of `path` is defined:
 * macros go to their #define; otherwise the parsed file's definition if libclang
 * sees it, else matching definitions from the symbol index (same scope first);
 * on a definition, its separate declaration instead. Without a parse (or while
 * the file is being parsed) the symbol index is searched for the word under the
 * caret. Each result is a NUL-terminated UTF-16 record of tab-separated fields:
 * path, line, column (UTF-16), label. Returns the number of char16_t needed. */
VX_API uint32_t VX_CALL vx_goto_definition(const char16_t* path, uint32_t line, uint32_t column, char16_t* out,
                                           uint32_t cap);

/* Drops the cached translation unit for `path`. */
VX_API void VX_CALL vx_release(const char16_t* path);

#ifdef __cplusplus
}
#endif

#endif
