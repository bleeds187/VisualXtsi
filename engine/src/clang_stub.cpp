// Built when libclang is unavailable at build time: parsing entry points report so.
#include "vx/vx.h"

extern "C" {

VX_API int32_t VX_CALL vx_parse(const char16_t*, const char16_t* const*, uint32_t, const char16_t*, const char16_t*,
                                uint32_t)
{
    return VX_ERR_NOT_AVAILABLE;
}

VX_API uint32_t VX_CALL vx_macro_info(const char16_t*, uint32_t, uint32_t, char16_t*, uint32_t)
{
    return 0;
}

VX_API void VX_CALL vx_release(const char16_t*) {}

VX_API uint32_t VX_CALL vx_semantic_tokens(const char16_t*, vx_span*, uint32_t)
{
    return 0;
}

VX_API int32_t VX_CALL vx_prepare_module_pch(const char16_t*)
{
    return VX_PCH_NOT_APPLICABLE;
}

VX_API void VX_CALL vx_configure(const vx_config*) {}

VX_API uint32_t VX_CALL vx_goto_definition(const char16_t*, uint32_t, uint32_t, char16_t*, uint32_t)
{
    return 0;
}

VX_API uint64_t VX_CALL vx_clear_pch_cache(void)
{
    return 0;
}

VX_API uint32_t VX_CALL vx_clang_version(char16_t*, uint32_t)
{
    return 0;
}

VX_API uint32_t VX_CALL vx_discover_args(const char16_t*, char16_t*, uint32_t, uint32_t* source)
{
    if (source)
        *source = VX_ARGS_NONE;
    return 0;
}

VX_API uint32_t VX_CALL vx_cursor_info(const char16_t*, uint32_t, uint32_t, uint32_t, char16_t*, uint32_t)
{
    return 0;
}

VX_API uint32_t VX_CALL vx_class_members(const char16_t*, uint32_t, uint32_t, uint32_t, char16_t*, uint32_t)
{
    return 0;
}

VX_API uint32_t VX_CALL vx_references_start(const char16_t*, uint32_t, uint32_t)
{
    return 0;
}

VX_API uint32_t VX_CALL vx_references_target(uint32_t, char16_t*, uint32_t)
{
    return 0;
}

VX_API void VX_CALL vx_references_progress(uint32_t, uint32_t* done, uint32_t* total, uint32_t* finished)
{
    if (done)
        *done = 0;
    if (total)
        *total = 0;
    if (finished)
        *finished = 1;
}

VX_API uint32_t VX_CALL vx_references_results(uint32_t, uint32_t, char16_t*, uint32_t)
{
    return 0;
}

VX_API void VX_CALL vx_references_cancel(uint32_t) {}

VX_API void VX_CALL vx_references_free(uint32_t) {}

} // extern "C"
