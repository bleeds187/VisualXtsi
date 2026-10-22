cmake_minimum_required(VERSION 3.20)
project(VisualXtsiEngine LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT so the DLL has no redistributable dependency inside the VSIX.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

if(MSVC)
  add_compile_options(/W4 /permissive- /utf-8)
endif()

# LLVM install providing include/clang-c, lib/libclang.lib and bin/libclang.dll.
if(DEFINED ENV{VX_LLVM_ROOT})
  set(_vx_llvm_default "$ENV{VX_LLVM_ROOT}")
else()
  set(_vx_llvm_default "C:/Program Files/LLVM")
endif()
set(VX_LLVM_ROOT "${_vx_llvm_default}" CACHE PATH "LLVM install root used for libclang")
file(TO_CMAKE_PATH "${VX_LLVM_ROOT}" VX_LLVM_ROOT_PATH)
file(GLOB _vx_resource_includes LIST_DIRECTORIES true "${VX_LLVM_ROOT_PATH}/lib/clang/*/include")
if(EXISTS "${VX_LLVM_ROOT_PATH}/include/clang-c/Index.h" AND EXISTS "${VX_LLVM_ROOT_PATH}/lib/libclang.lib"
   AND EXISTS "${VX_LLVM_ROOT_PATH}/bin/libclang.dll" AND _vx_resource_includes)
  set(VX_HAVE_LIBCLANG ON)
  list(GET _vx_resource_includes 0 VX_CLANG_RESOURCE_INCLUDE)
  message(STATUS "libclang: ${VX_LLVM_ROOT_PATH}")
else()
  set(VX_HAVE_LIBCLANG OFF)
  message(WARNING "libclang not found under VX_LLVM_ROOT='${VX_LLVM_ROOT}'; building without parsing support")
endif()

add_library(vxengine SHARED
  src/lexer.cpp
  src/classify.cpp
  src/macro_expand.cpp
  src/project_args.cpp
  src/unicode.cpp
  src/xml.cpp
)
target_include_directories(vxengine PUBLIC include PRIVATE src)
target_compile_definitions(vxengine PRIVATE VX_BUILDING_ENGINE)

if(VX_HAVE_LIBCLANG)
  target_sources(vxengine PRIVATE src/clang_session.cpp src/module_pch.cpp src/semantic_tokens.cpp)
  target_include_directories(vxengine PRIVATE "${VX_LLVM_ROOT_PATH}/include")
  # Delay-loaded so lexical classification works even when libclang.dll is missing.
  target_link_libraries(vxengine PRIVATE "${VX_LLVM_ROOT_PATH}/lib/libclang.lib" delayimp)
  target_link_options(vxengine PRIVATE /DELAYLOAD:libclang.dll)
  # Ship libclang.dll and clang's builtin headers next to the engine.
  add_custom_command(TARGET vxengine POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${VX_LLVM_ROOT_PATH}/bin/libclang.dll" "$<TARGET_FILE_DIR:vxengine>"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${VX_CLANG_RESOURCE_INCLUDE}" "$<TARGET_FILE_DIR:vxengine>/clang-resource/include")
else()
  target_sources(vxengine PRIVATE src/clang_stub.cpp)
endif()

enable_testing()
add_executable(vxengine_tests tests/test_classify.cpp)
target_link_libraries(vxengine_tests PRIVATE vxengine)
add_test(NAME vxengine_tests COMMAND vxengine_tests)

add_executable(vxengine_expand_tests tests/test_macro_expand.cpp src/macro_expand.cpp)
target_include_directories(vxengine_expand_tests PRIVATE src)
add_test(NAME vxengine_expand_tests COMMAND vxengine_expand_tests)

add_executable(vxengine_project_tests tests/test_project_args.cpp src/project_args.cpp src/xml.cpp src/unicode.cpp)
target_include_directories(vxengine_project_tests PRIVATE src)
target_compile_definitions(vxengine_project_tests PRIVATE VX_SAMPLES_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../tests/samples")
add_test(NAME vxengine_project_tests COMMAND vxengine_project_tests)

if(VX_HAVE_LIBCLANG)
  add_executable(vxengine_clang_tests tests/test_clang.cpp)
  target_link_libraries(vxengine_clang_tests PRIVATE vxengine)
  target_compile_definitions(vxengine_clang_tests PRIVATE VX_SAMPLES_DIR=u"${CMAKE_CURRENT_SOURCE_DIR}/../tests/samples"
                             VX_TEST_PCH_DIR="${CMAKE_CURRENT_BINARY_DIR}/test-pch")
  add_test(NAME vxengine_clang_tests COMMAND vxengine_clang_tests)

  add_executable(vxcli tools/vxcli.cpp)
  target_link_libraries(vxcli PRIVATE vxengine)
endif()
