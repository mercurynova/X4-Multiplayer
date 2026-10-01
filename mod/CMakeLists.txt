cmake_minimum_required(VERSION 3.25)

# X4MP native mod (task M0-08). Builds x4mp.dll, the X4Native extension, plus Catch2 tests for core/.
#
#   native/core/   pure C++, NO X4 SDK on its include path (enforced below, and by the test targets)
#   native/game/   thin adapters over X4; safe_remove.* is the only place that may mention RemoveComponent
#   native/main.cpp  X4N_EXTENSION entry point; the only target that gets the vendored SDK include directory
#
# Configure through the presets (see build.ps1): the vcpkg toolchain supplies FlatBuffers 25.2.10 (pinned in
# vcpkg.json), Catch2 and nlohmann-json, triplet x64-windows-static, static CRT (/MT).

project(x4mp_mod VERSION 0.1.0 LANGUAGES CXX)

option(X4MP_TESTS "Build the Catch2 unit tests and register the guard scripts with CTest" ON)

set(X4N_TAG "v9.0.0-611726")
set(X4N_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/x4native/${X4N_TAG}")
set(X4N_SDK_DIR "${X4N_DIR}/sdk")
set(X4MP_GAME_BUILD_PIN "900-611726" CACHE STRING "Supported X4 build (game version + build number); must match the vendored version_db")

if(NOT EXISTS "${X4N_SDK_DIR}/x4native_extension.h")
  message(FATAL_ERROR "Vendored X4Native SDK not found at ${X4N_SDK_DIR}")
endif()

# ---------------------------------------------------------------------------------------------
# Protocol library (M0-07). The parent supplies FlatBuffers 25.2.10 via vcpkg (find_package in there).
# ---------------------------------------------------------------------------------------------
add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../protocol/cpp" "${CMAKE_CURRENT_BINARY_DIR}/protocol")

# Our code: /W4 /permissive- and warnings are errors. Third-party and SDK headers are SYSTEM includes (/external:W0).
function(x4mp_set_warnings target)
  if(MSVC)
    target_compile_options(${target} PRIVATE /W4 /permissive- /WX /external:W0 /utf-8 /EHsc)
  else()
    target_compile_options(${target} PRIVATE -Wall -Wextra -Wpedantic -Werror)
  endif()
endfunction()

# ---------------------------------------------------------------------------------------------
# core/: pure C++ (no X4 headers). Include root is native/, so code says #include "core/log/log.h".
# ---------------------------------------------------------------------------------------------
add_library(x4mp_core STATIC
  native/core/log/log.cpp
  native/core/config/config.cpp
  native/core/net/net.cpp
  native/core/session/session.cpp
  native/core/version/version.cpp)
target_include_directories(x4mp_core PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/native")
target_compile_features(x4mp_core PUBLIC cxx_std_23)
target_compile_definitions(x4mp_core PRIVATE
  X4MP_VERSION_STRING="${PROJECT_VERSION}"
  X4MP_GAME_BUILD_PIN="${X4MP_GAME_BUILD_PIN}")
find_package(nlohmann_json CONFIG REQUIRED)
target_link_libraries(x4mp_core PUBLIC x4mp::wire PRIVATE nlohmann_json::nlohmann_json)
set_target_properties(x4mp_core PROPERTIES CXX_EXTENSIONS OFF)
x4mp_set_warnings(x4mp_core)

# ---------------------------------------------------------------------------------------------
# game/: the adapters. safe_remove has no SDK dependency yet, so this target does not get the SDK either.
# ---------------------------------------------------------------------------------------------
add_library(x4mp_game STATIC native/game/safe_remove.cpp)
target_include_directories(x4mp_game PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/native")
target_compile_features(x4mp_game PUBLIC cxx_std_23)
target_link_libraries(x4mp_game PUBLIC x4mp_core)
set_target_properties(x4mp_game PROPERTIES CXX_EXTENSIONS OFF)
x4mp_set_warnings(x4mp_game)

# ---------------------------------------------------------------------------------------------
# x4mp.dll: the X4Native extension. The only target with the vendored SDK on its include path.
# Exports come only from the X4N_EXTENSION / X4N_SHUTDOWN macros.
# ---------------------------------------------------------------------------------------------
add_library(x4mp SHARED native/main.cpp)
target_include_directories(x4mp SYSTEM PRIVATE "${X4N_SDK_DIR}")
target_link_libraries(x4mp PRIVATE x4mp_core x4mp_game)
target_compile_features(x4mp PRIVATE cxx_std_23)
set_target_properties(x4mp PROPERTIES PREFIX "" OUTPUT_NAME "x4mp" CXX_EXTENSIONS OFF)
x4mp_set_warnings(x4mp)
if(MSVC)
  target_compile_options(x4mp PRIVATE /guard:cf)
  target_link_options(x4mp PRIVATE /GUARD:CF)
endif()

# ---------------------------------------------------------------------------------------------
# Isolation check: nothing under core/ (or its tests) may see the X4 SDK.
# ---------------------------------------------------------------------------------------------
function(x4mp_assert_no_sdk target)
  foreach(_prop INCLUDE_DIRECTORIES INTERFACE_INCLUDE_DIRECTORIES INTERFACE_SYSTEM_INCLUDE_DIRECTORIES)
    get_target_property(_dirs ${target} ${_prop})
    if(_dirs)
      foreach(_d IN LISTS _dirs)
        if(_d MATCHES "third_party/x4native" OR _d MATCHES "x4native-sdk")
          message(FATAL_ERROR "${target} must not have the X4 SDK on its include path (${_prop}: ${_d})")
        endif()
      endforeach()
    endif()
  endforeach()
endfunction()

# ---------------------------------------------------------------------------------------------
# Tests (Catch2) + guards
# ---------------------------------------------------------------------------------------------
if(X4MP_TESTS)
  enable_testing()
  find_package(Catch2 3 CONFIG REQUIRED)
  include(Catch)

  # Separate target on purpose: only x4mp_core + Catch2. No SDK include dirs, so a test cannot even
  # #include an X4 header.
  add_executable(x4mp_core_tests
    tests/test_log.cpp
    tests/test_config.cpp
    tests/test_queue.cpp
    tests/test_net.cpp
    tests/test_no_env.cpp
    tests/test_session.cpp
    tests/test_version.cpp)
  target_compile_definitions(x4mp_core_tests PRIVATE X4MP_CORE_SRC_DIR="${CMAKE_CURRENT_SOURCE_DIR}/native/core")
  target_link_libraries(x4mp_core_tests PRIVATE x4mp_core Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_core_tests)
  catch_discover_tests(x4mp_core_tests TEST_PREFIX "core." PROPERTIES TIMEOUT 60)

  # game/ logic that needs no SDK (the removal guard) is tested the same way.
  add_executable(x4mp_game_tests tests/test_safe_remove.cpp)
  target_link_libraries(x4mp_game_tests PRIVATE x4mp_game Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_game_tests)
  catch_discover_tests(x4mp_game_tests TEST_PREFIX "game." PROPERTIES TIMEOUT 60)

  foreach(_t x4mp_core x4mp_game x4mp_core_tests x4mp_game_tests)
    x4mp_assert_no_sdk(${_t})
  endforeach()

  find_program(X4MP_POWERSHELL NAMES pwsh powershell)
  if(X4MP_POWERSHELL)
    set(_ps ${X4MP_POWERSHELL} -NoProfile -ExecutionPolicy Bypass -File)
    add_test(NAME guard.no_raw_remove
      COMMAND ${_ps} "${CMAKE_CURRENT_SOURCE_DIR}/tools/check-no-raw-remove.ps1" -Root "${CMAKE_CURRENT_SOURCE_DIR}/native")
    add_test(NAME guard.packaging
      COMMAND ${_ps} "${CMAKE_CURRENT_SOURCE_DIR}/tools/check-packaging.ps1" -ModDir "${CMAKE_CURRENT_SOURCE_DIR}" -Build "${X4MP_GAME_BUILD_PIN}")
  else()
    message(WARNING "PowerShell not found: the guard.* tests are not registered")
  endif()
endif()
