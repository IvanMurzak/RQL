cmake_minimum_required(VERSION 3.20)
project(rql_cpp VERSION 0.1.0 LANGUAGES CXX)

# C++17 minimum; override with -DCMAKE_CXX_STANDARD=20 (Unreal Engine 5.5+ builds modules as C++20).
if(NOT DEFINED CMAKE_CXX_STANDARD)
  set(CMAKE_CXX_STANDARD 17)
endif()
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

option(RQL_BUILD_TESTS "Build the RQL C++ tests" ON)
option(RQL_WARNINGS_AS_ERRORS "Treat warnings as errors for the library target" ON)
option(RQL_NO_RTTI "Build the library and tests without RTTI (-fno-rtti / /GR-), as Unreal modules do" OFF)
option(RQL_TEST_NO_RTTI_VARIANT "Also build and ctest a no-RTTI variant of the library and tests" ON)

# Shortest round-trip float formatting: std::to_chars for floating point is C++17 but not shipped by
# every standard library (older libstdc++/libc++). Detect it and fall back to a %.17g loop otherwise.
include(CheckCXXSourceCompiles)
set(CMAKE_REQUIRED_FLAGS "-std=c++17")
if(MSVC)
  set(CMAKE_REQUIRED_FLAGS "/std:c++17")
endif()
check_cxx_source_compiles("
#include <charconv>
int main() { char b[32]; double d = 0; auto r = std::to_chars(b, b + 32, 0.1); auto q = std::from_chars(b, r.ptr, d); (void)q; float f = 0.1f; (void)std::to_chars(b, b + 32, f); return 0; }
" RQL_HAVE_FLOAT_CHARCONV)
unset(CMAKE_REQUIRED_FLAGS)

if(MSVC)
  set(RQL_NO_RTTI_FLAG /GR-)
else()
  set(RQL_NO_RTTI_FLAG -fno-rtti)
endif()

set(RQL_SOURCES
  src/json.cpp src/codec.cpp src/result.cpp src/value.cpp src/reflection.cpp
  src/compiler.cpp src/pipeline.cpp src/encoder.cpp src/mutator.cpp src/engine.cpp src/adapters.cpp)

# rql_add_library(<target> <no_rtti:ON|OFF>)
function(rql_add_library target no_rtti)
  add_library(${target} ${RQL_SOURCES})
  target_include_directories(${target} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
  if(RQL_HAVE_FLOAT_CHARCONV)
    target_compile_definitions(${target} PRIVATE RQL_HAVE_FLOAT_CHARCONV=1)
  endif()
  if(MSVC)
    target_compile_options(${target} PRIVATE /W4 /permissive- /utf-8 /EHsc)
    if(RQL_WARNINGS_AS_ERRORS)
      target_compile_options(${target} PRIVATE /WX)
    endif()
  else()
    target_compile_options(${target} PRIVATE -Wall -Wextra -Wpedantic -Wshadow -Wnon-virtual-dtor)
    if(RQL_WARNINGS_AS_ERRORS)
      target_compile_options(${target} PRIVATE -Werror)
    endif()
  endif()
  if(no_rtti)
    target_compile_options(${target} PUBLIC ${RQL_NO_RTTI_FLAG})
  endif()
endfunction()

rql_add_library(rql ${RQL_NO_RTTI})

if(RQL_BUILD_TESTS)
  enable_testing()
  get_filename_component(RQL_REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)

  # rql_add_tests(<library> <suffix>)
  function(rql_add_tests lib suffix)
    add_executable(rql_conformance${suffix} tests/conformance_main.cpp tests/conformance_graph.cpp)
    add_executable(rql_unit${suffix} tests/unit_tests.cpp tests/conformance_graph.cpp)
    foreach(t rql_conformance${suffix} rql_unit${suffix})
      target_link_libraries(${t} PRIVATE ${lib})
      target_include_directories(${t} PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/tests)
      if(MSVC)
        target_compile_options(${t} PRIVATE /W4 /utf-8 /EHsc)
      else()
        target_compile_options(${t} PRIVATE -Wall -Wextra)
      endif()
    endforeach()
    add_test(NAME rql_conformance${suffix} COMMAND rql_conformance${suffix} "${RQL_REPO_ROOT}")
    add_test(NAME rql_unit${suffix} COMMAND rql_unit${suffix} "${RQL_REPO_ROOT}")
  endfunction()

  rql_add_tests(rql "")
  # Unreal compiles modules without RTTI: keep a no-RTTI variant in every ctest run.
  if(RQL_TEST_NO_RTTI_VARIANT AND NOT RQL_NO_RTTI)
    rql_add_library(rql_nortti ON)
    rql_add_tests(rql_nortti "_nortti")
  endif()
endif()
