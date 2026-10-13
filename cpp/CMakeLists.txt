cmake_minimum_required(VERSION 3.20)
project(rql_cpp VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

option(RQL_BUILD_TESTS "Build the RQL C++ tests" ON)
option(RQL_WARNINGS_AS_ERRORS "Treat warnings as errors for the library target" ON)

# Shortest round-trip float formatting: std::to_chars for floating point is C++17 but not shipped by
# every standard library (older libstdc++/libc++). Detect it and fall back to a %.17g loop otherwise.
include(CheckCXXSourceCompiles)
set(CMAKE_REQUIRED_FLAGS "-std=c++17")
check_cxx_source_compiles("
#include <charconv>
int main() { char b[32]; double d = 0; auto r = std::to_chars(b, b + 32, 0.1); auto q = std::from_chars(b, r.ptr, d); (void)q; float f = 0.1f; (void)std::to_chars(b, b + 32, f); return 0; }
" RQL_HAVE_FLOAT_CHARCONV)
unset(CMAKE_REQUIRED_FLAGS)

add_library(rql
  src/json.cpp
  src/codec.cpp
  src/result.cpp
  src/value.cpp
  src/reflection.cpp
  src/compiler.cpp
  src/pipeline.cpp
  src/encoder.cpp
  src/mutator.cpp
  src/engine.cpp
)
target_include_directories(rql PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
if(RQL_HAVE_FLOAT_CHARCONV)
  target_compile_definitions(rql PRIVATE RQL_HAVE_FLOAT_CHARCONV=1)
endif()
if(MSVC)
  target_compile_options(rql PRIVATE /W4 /permissive- /utf-8)
  if(RQL_WARNINGS_AS_ERRORS)
    target_compile_options(rql PRIVATE /WX)
  endif()
else()
  target_compile_options(rql PRIVATE -Wall -Wextra -Wpedantic -Wshadow -Wnon-virtual-dtor)
  if(RQL_WARNINGS_AS_ERRORS)
    target_compile_options(rql PRIVATE -Werror)
  endif()
endif()

if(RQL_BUILD_TESTS)
  enable_testing()
  add_executable(rql_conformance tests/conformance_main.cpp tests/conformance_graph.cpp)
  add_executable(rql_unit tests/unit_tests.cpp tests/conformance_graph.cpp)
  foreach(t rql_conformance rql_unit)
    target_link_libraries(${t} PRIVATE rql)
    target_include_directories(${t} PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/tests)
    if(MSVC)
      target_compile_options(${t} PRIVATE /W4 /utf-8)
    else()
      target_compile_options(${t} PRIVATE -Wall -Wextra)
    endif()
  endforeach()
  get_filename_component(RQL_REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)
  add_test(NAME rql_conformance COMMAND rql_conformance "${RQL_REPO_ROOT}")
  add_test(NAME rql_unit COMMAND rql_unit "${RQL_REPO_ROOT}")
endif()
