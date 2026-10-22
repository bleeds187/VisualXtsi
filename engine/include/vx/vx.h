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

#define VX_ABI_VERSION 6u

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
} vx_config;

/* Applies settings to parses started afterwards. Environment variables override
 * them: VX_FULL_PREAMBLE=1 (no body skipping), VX_PCH_CACHE_MB, VX_PCH_DIR. */
VX_API void VX_CALL vx_configure(const vx_config* config);

/* Deletes every cached PCH that is not in use and forgets their state. Returns
 * the number of bytes freed. */
VX_API uint64_t VX_CALL vx_clear_pch_cache(void);

/* Drops the cached translation unit for `path`. */
VX_API void VX_CALL vx_release(const char16_t* path);

#ifdef __cplusplus
}
#endif

#endif
