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

} // extern "C"
