#pragma once

#include <atomic>
#include <cstdint>

// Settings applied through vx_configure. Environment variables, where documented in vx.h,
// override them.
namespace vx::config {

inline std::atomic<bool> skipHeaderBodies{true};
inline std::atomic<bool> sharedPch{true};
inline std::atomic<uint32_t> pchCacheMb{0}; // 0: default
inline std::atomic<uint32_t> argsSource{0}; // vx_config::args_source

} // namespace vx::config
