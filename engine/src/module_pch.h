#pragma once

#include "project_args.h"

#include <cstdint>
#include <string>
#include <vector>

// Shared precompiled headers per Unreal module (see vx_prepare_module_pch in vx.h).
namespace vx::pch {

// Stable key for a module's parse arguments, ignoring the per-file language (-x).
std::string moduleKey(const std::vector<std::string>& args);

// Path of a current PCH for `key`, or empty. Checks the manifest at most every few minutes.
std::string ready(const std::string& key);

// Builds the PCH for `key` if there is no current one. Blocking. Returns a vx_pch_status.
int prepare(const std::string& key, const std::vector<std::string>& args, const VcxprojArgs& project);

// Called when a parse could not use the PCH: it is deleted and not rebuilt this session.
void markBad(const std::string& key);

// Deletes every cached PCH not in use; returns the bytes freed.
uint64_t clearCache();

} // namespace vx::pch
