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

add_library(vxengine SHARED
  src/lexer.cpp
  src/classify.cpp
)
target_include_directories(vxengine PUBLIC include PRIVATE src)
target_compile_definitions(vxengine PRIVATE VX_BUILDING_ENGINE)

enable_testing()
add_executable(vxengine_tests tests/test_classify.cpp)
target_link_libraries(vxengine_tests PRIVATE vxengine)
add_test(NAME vxengine_tests COMMAND vxengine_tests)
