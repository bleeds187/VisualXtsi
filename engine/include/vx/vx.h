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

#define VX_ABI_VERSION 1u

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

#ifdef __cplusplus
}
#endif

#endif
