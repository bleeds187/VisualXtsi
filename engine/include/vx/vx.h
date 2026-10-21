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

#define VX_ABI_VERSION 2u

typedef enum vx_kind {
    VX_KIND_PREPROCESSOR = 1, /* '#' plus directive name, e.g. "#define" */
    VX_KIND_MACRO = 2         /* identifier #defined in the file, or ALL_CAPS */
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

/* Parses `path` with libclang, or reparses it if it is already cached with the
 * same arguments (using the precompiled preamble). Arguments come from `args`
 * when argc > 0, else from compile_commands.json in `compdb_dir` (may be null),
 * else from defaults. `unsaved` (may be null) is the current editor text of
 * `path`. Returns a vx_status. */
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

/* Drops the cached translation unit for `path`. */
VX_API void VX_CALL vx_release(const char16_t* path);

#ifdef __cplusplus
}
#endif

#endif
