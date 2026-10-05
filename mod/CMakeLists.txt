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
  native/core/config/remembered.cpp
  native/core/crypto/crypto.cpp
  native/core/net/net.cpp
  native/core/session/session.cpp
  native/core/session/save_download.cpp
  native/core/session/manifest_cleanup.cpp
  native/core/version/version.cpp)
target_include_directories(x4mp_core PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/native")
target_compile_features(x4mp_core PUBLIC cxx_std_23)
target_compile_definitions(x4mp_core PRIVATE
  X4MP_VERSION_STRING="${PROJECT_VERSION}"
  X4MP_GAME_BUILD_PIN="${X4MP_GAME_BUILD_PIN}")
find_package(nlohmann_json CONFIG REQUIRED)
target_link_libraries(x4mp_core PUBLIC x4mp::wire PRIVATE nlohmann_json::nlohmann_json ws2_32 bcrypt)
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

# =============================================================================================
# BEGIN M2-04 host (mod host skeleton). Appended by M2-04; later tasks only add files under native/features/.
#   x4mp_game   += main thread check, game adapter, player guard (SDK-free)
#   x4mp_host   = native/host/** + native/features/**.cpp (SDK-free static lib; features are picked up by glob)
#   x4mp (DLL)  += game/x4_platform.cpp (the X4Native IPlatform) and links x4mp_host
#   x4mp_host_tests = Catch2 for the host and the game adapter (no SDK on its include path)
# =============================================================================================
target_sources(x4mp_game PRIVATE
  native/game/main_thread.cpp
  native/game/game_api.cpp
  native/game/player_guard.cpp)

file(GLOB_RECURSE X4MP_FEATURE_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/native/features/*.cpp")
add_library(x4mp_host STATIC
  native/host/redact.cpp
  native/host/host_log.cpp
  native/host/frame_budget.cpp
  native/host/build_check.cpp
  native/host/feature.cpp
  native/host/feature_list.cpp
  native/host/paths.cpp
  native/host/mod_host.cpp
  ${X4MP_FEATURE_SOURCES})
target_include_directories(x4mp_host PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/native")
target_compile_features(x4mp_host PUBLIC cxx_std_23)
target_link_libraries(x4mp_host PUBLIC x4mp_core x4mp_game shell32 ole32)
set_target_properties(x4mp_host PROPERTIES CXX_EXTENSIONS OFF)
x4mp_set_warnings(x4mp_host)

target_sources(x4mp PRIVATE native/game/x4_platform.cpp)
target_link_libraries(x4mp PRIVATE x4mp_host)

if(X4MP_TESTS)
  find_package(Catch2 3 CONFIG REQUIRED)
  include(Catch)
  add_executable(x4mp_host_tests
    tests/test_host_budget.cpp
    tests/test_host_registry.cpp
    tests/test_host_build_check.cpp
    tests/test_host_redact.cpp
    tests/test_host_modhost.cpp
    tests/test_game_api.cpp)
  target_link_libraries(x4mp_host_tests PRIVATE x4mp_host Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_host_tests)
  catch_discover_tests(x4mp_host_tests TEST_PREFIX "host." PROPERTIES TIMEOUT 60)
  # x4mp_assert_no_sdk is defined further down: run the isolation check for the new targets at the end of the directory.
  cmake_language(DEFER CALL x4mp_assert_no_sdk x4mp_host)
  cmake_language(DEFER CALL x4mp_assert_no_sdk x4mp_host_tests)

  # DLL smoke: loads the real x4mp.dll through a minimal fake X4NativeAPI (the contract M2-01 hostsim builds on).
  # Only this target and the DLL see the SDK; it includes nothing from core/ or host/.
  add_executable(x4mp_dll_smoke tests/dll_smoke/test_dll_smoke.cpp)
  target_include_directories(x4mp_dll_smoke SYSTEM PRIVATE "${X4N_SDK_DIR}")
  target_compile_definitions(x4mp_dll_smoke PRIVATE X4MP_DLL_PATH="$<TARGET_FILE:x4mp>")
  target_link_libraries(x4mp_dll_smoke PRIVATE Catch2::Catch2WithMain)
  target_compile_features(x4mp_dll_smoke PRIVATE cxx_std_23)
  add_dependencies(x4mp_dll_smoke x4mp)
  x4mp_set_warnings(x4mp_dll_smoke)
  catch_discover_tests(x4mp_dll_smoke TEST_PREFIX "dll." PROPERTIES TIMEOUT 60)
endif()
# =============================================================================================
# END M2-04 host
# =============================================================================================

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
    tests/test_remembered.cpp
    tests/test_queue.cpp
    tests/test_net.cpp
    tests/test_net_socket.cpp
    tests/test_no_env.cpp
    tests/test_session.cpp
    tests/test_crypto.cpp
    tests/test_retention.cpp
    tests/test_save_download.cpp
    tests/test_manifest_cleanup.cpp
    tests/test_session_handshake.cpp
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

  # netprobe: manual/CI smoke tool, connects to a real server and prints ServerHello (M1-N2). Core only, no SDK.
  add_executable(netprobe tools/netprobe/netprobe.cpp)
  target_link_libraries(netprobe PRIVATE x4mp_core)
  x4mp_set_warnings(netprobe)
  # Skipped (not failed) unless X4MP_TEST_SERVER=host:port is set. Env vars are fine in test tools; the mod
  # runtime never reads them.
  add_test(NAME smoke.netprobe_real_server COMMAND netprobe --from-env)
  set_tests_properties(smoke.netprobe_real_server PROPERTIES SKIP_RETURN_CODE 77 TIMEOUT 30)

  # x4mp-headless: joins a real server with the Session layer (join, ping, ClientReload + resume, save download).
  add_executable(x4mp-headless tools/headless/headless.cpp)
  target_link_libraries(x4mp-headless PRIVATE x4mp_core)
  x4mp_set_warnings(x4mp-headless)
  # Skipped unless X4MP_TEST_SERVER=host:port is set (joins, pings, ClientReload + resume).
  add_test(NAME smoke.headless_real_server COMMAND x4mp-headless --from-env --reload)
  set_tests_properties(smoke.headless_real_server PROPERTIES SKIP_RETURN_CODE 77 TIMEOUT 60)

  foreach(_t x4mp_core x4mp_game x4mp_core_tests x4mp_game_tests netprobe x4mp-headless)
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

# =============================================================================================
# X4MP_SPIKES begin (M2-005): throwaway native probe for in-game session 2. Default OFF: the normal build, CTest
# list and CI are unchanged. Everything lives under spikes/x4mp_probe/ (sources in src/, tests in tests/, the
# Lua/MD shim extension files at the folder root: content.xml, ui.xml, x4native.json, ui/, md/, t/).
# =============================================================================================
option(X4MP_SPIKES "Build the throwaway in-game spike DLLs (x4mp_probe)" OFF)
if(X4MP_SPIKES)
  set(X4MP_PROBE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/spikes/x4mp_probe")

  add_library(x4mp_probe_lib STATIC
    "${X4MP_PROBE_DIR}/src/probe.cpp"
    "${X4MP_PROBE_DIR}/src/s13.cpp"
    "${X4MP_PROBE_DIR}/src/probe_config.cpp")
  target_include_directories(x4mp_probe_lib SYSTEM PUBLIC "${X4N_SDK_DIR}")
  target_compile_features(x4mp_probe_lib PUBLIC cxx_std_23)
  target_link_libraries(x4mp_probe_lib PUBLIC x4mp_core PRIVATE nlohmann_json::nlohmann_json shell32 ole32)
  set_target_properties(x4mp_probe_lib PROPERTIES CXX_EXTENSIONS OFF)
  x4mp_set_warnings(x4mp_probe_lib)

  add_library(x4mp_probe SHARED "${X4MP_PROBE_DIR}/src/dll_main.cpp")
  target_link_libraries(x4mp_probe PRIVATE x4mp_probe_lib)
  set_target_properties(x4mp_probe PROPERTIES PREFIX "" OUTPUT_NAME "x4mp_probe" CXX_EXTENSIONS OFF)
  x4mp_set_warnings(x4mp_probe)
  if(MSVC)
    target_compile_options(x4mp_probe PRIVATE /guard:cf)
    target_link_options(x4mp_probe PRIVATE /GUARD:CF)
  endif()

  if(X4MP_TESTS)
    find_package(Catch2 3 CONFIG REQUIRED)
    include(Catch)
    add_executable(x4mp_probe_tests "${X4MP_PROBE_DIR}/tests/probe_tests.cpp" "${X4MP_PROBE_DIR}/tests/s13_tests.cpp")
    target_include_directories(x4mp_probe_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/tests")
    target_link_libraries(x4mp_probe_tests PRIVATE x4mp_probe_lib Catch2::Catch2WithMain ws2_32)
    x4mp_set_warnings(x4mp_probe_tests)
    catch_discover_tests(x4mp_probe_tests TEST_PREFIX "probe." PROPERTIES TIMEOUT 120)

    add_executable(x4mp_probe_smoke "${X4MP_PROBE_DIR}/tests/smoke.cpp")
    target_include_directories(x4mp_probe_smoke SYSTEM PRIVATE "${X4N_SDK_DIR}")
    target_compile_features(x4mp_probe_smoke PRIVATE cxx_std_23)
    x4mp_set_warnings(x4mp_probe_smoke)
    add_dependencies(x4mp_probe_smoke x4mp_probe)
    add_test(NAME probe.smoke COMMAND x4mp_probe_smoke "$<TARGET_FILE:x4mp_probe>")
    set_tests_properties(probe.smoke PROPERTIES TIMEOUT 60 PASS_REGULAR_EXPRESSION "SMOKE OK")
  endif()
endif()
# X4MP_SPIKES end

# =============================================================================================
# X4MP_HOSTSIM begin (M2-01): x4mp-hostsim, a fake X4Native host that loads x4mp.dll without X4 (sources in
# tools/hostsim/, scripts in tests/hostsim/*.hostsim), plus a stub extension that tests hostsim itself. Built whenever
# X4MP_TESTS is ON (the e2e HostSim step builds with build.ps1 -NoTest, which still builds every target).
# =============================================================================================
if(X4MP_TESTS)
  add_executable(x4mp-hostsim
    tools/hostsim/main.cpp
    tools/hostsim/host.cpp
    tools/hostsim/world.cpp
    tools/hostsim/admin_client.cpp)
  target_include_directories(x4mp-hostsim SYSTEM PRIVATE "${X4N_SDK_DIR}")
  target_include_directories(x4mp-hostsim PRIVATE tools/hostsim "${CMAKE_CURRENT_SOURCE_DIR}/native")  # native: game/game_api.h (PosRotPod layout check)
  target_compile_features(x4mp-hostsim PRIVATE cxx_std_23)
  target_link_libraries(x4mp-hostsim PRIVATE nlohmann_json::nlohmann_json ws2_32)
  set_target_properties(x4mp-hostsim PROPERTIES CXX_EXTENSIONS OFF)
  x4mp_set_warnings(x4mp-hostsim)

  add_library(x4mp_hostsim_stub SHARED tests/hostsim/stub_ext.cpp)
  target_include_directories(x4mp_hostsim_stub SYSTEM PRIVATE "${X4N_SDK_DIR}")
  target_compile_features(x4mp_hostsim_stub PRIVATE cxx_std_23)
  set_target_properties(x4mp_hostsim_stub PROPERTIES PREFIX "" OUTPUT_NAME "x4mp_hostsim_stub" CXX_EXTENSIONS OFF)
  x4mp_set_warnings(x4mp_hostsim_stub)

  # hostsim against its own stub extension (every fake of the host API) and against the real x4mp.dll (no server).
  add_test(NAME hostsim.stub
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp_hostsim_stub>" --ext-id x4mp_hostsim_stub
            --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/stub.hostsim")
  set_tests_properties(hostsim.stub PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
  add_test(NAME hostsim.real_dll
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/smoke.hostsim")
  set_tests_properties(hostsim.real_dll PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
  add_test(NAME hostsim.refuse_build
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/refuse_build.hostsim")
  set_tests_properties(hostsim.refuse_build PROPERTIES TIMEOUT 60 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
  # A failing script must exit with code 1 and print "HOSTSIM FAIL line N".
  add_test(NAME hostsim.failure_exit_code
    COMMAND ${CMAKE_COMMAND} -DHOSTSIM=$<TARGET_FILE:x4mp-hostsim> -DDLL=$<TARGET_FILE:x4mp_hostsim_stub>
            -DSCRIPT=${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/fail.hostsim
            -P "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/expect_fail.cmake")
  set_tests_properties(hostsim.failure_exit_code PROPERTIES TIMEOUT 60)

  # M3-04: the fake universe (tools/hostsim/world.*) and its commands, against the stub extension (DLL-free: no x4mp.dll, no server).
  foreach(hs_world world_objects world_ship)
    add_test(NAME hostsim.${hs_world}
      COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp_hostsim_stub>" --ext-id x4mp_hostsim_stub
              --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/${hs_world}.hostsim")
    set_tests_properties(hostsim.${hs_world} PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
  endforeach()
  add_test(NAME hostsim.world_failure_exit_code
    COMMAND ${CMAKE_COMMAND} -DHOSTSIM=$<TARGET_FILE:x4mp-hostsim> -DDLL=$<TARGET_FILE:x4mp_hostsim_stub>
            -DSCRIPT=${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/world_fail.hostsim
            -P "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/expect_fail.cmake")
  set_tests_properties(hostsim.world_failure_exit_code PROPERTIES TIMEOUT 60)
endif()
# X4MP_HOSTSIM end

# =============================================================================================
# BEGIN M2-03 core/mods (real extension list; docs/mod-management.md 8). Appended by M2-03.
#   x4mp_core       += core/mods/mods.cpp (scanner, hasher, cache, ExtensionProvider: IExtensionProvider)
#   x4mp_host       += host/extension_roots.cpp (Windows roots: install / Documents / Workshop; no file I/O)
#   x4mp_core_tests += tests/test_mods.cpp over tests/fixtures/mods/ (hand-made synthetic folders)
# =============================================================================================
target_sources(x4mp_core PRIVATE native/core/mods/mods.cpp)
target_sources(x4mp_host PRIVATE native/host/extension_roots.cpp)
if(X4MP_TESTS)
  target_sources(x4mp_core_tests PRIVATE tests/test_mods.cpp)
  target_compile_definitions(x4mp_core_tests PRIVATE X4MP_FIXTURE_DIR="${CMAKE_CURRENT_SOURCE_DIR}/tests/fixtures")
  target_sources(x4mp_host_tests PRIVATE tests/test_host_extension_roots.cpp)
endif()
# =============================================================================================
# END M2-03 core/mods
# =============================================================================================

# =============================================================================================
# X4MP_AUTHORITY begin (M2-08): core/authority (upload job bound to one connection generation, EntitySpawn encoder that cannot
# emit game_time 0, checkpoint message encoders), the headless authority driver, Catch2 tests and the live kill-the-link test.
# =============================================================================================
target_sources(x4mp_core PRIVATE
  native/core/authority/entity_spawn.cpp
  native/core/authority/upload_job.cpp
  native/core/authority/checkpoint_messages.cpp)

if(X4MP_TESTS)
  # x4mp-headless --authority (tools/headless/authority_driver.*): the checkpoint flow without X4.
  target_sources(x4mp-headless PRIVATE tools/headless/authority_driver.cpp)
  target_include_directories(x4mp-headless PRIVATE tools/headless)

  add_executable(x4mp_authority_tests
    tests/authority/test_entity_spawn.cpp
    tests/authority/test_upload_job.cpp)
  target_include_directories(x4mp_authority_tests PRIVATE tests/authority)
  target_compile_definitions(x4mp_authority_tests PRIVATE X4MP_NATIVE_SRC_DIR="${CMAKE_CURRENT_SOURCE_DIR}/native")
  target_link_libraries(x4mp_authority_tests PRIVATE x4mp_core Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_authority_tests)
  catch_discover_tests(x4mp_authority_tests TEST_PREFIX "authority." PROPERTIES TIMEOUT 120)
  x4mp_assert_no_sdk(x4mp_authority_tests)

  # Live test (M2-08): 50 runs, each against a fresh REAL server behind a proxy that kills the connection at a random byte of the
  # upload. Skipped (exit 77) unless X4MP_LIVE_SERVER_EXE names a published x4mp-server. Ports: X4MP_LIVE_PORT_BASE (default 47980).
  add_executable(x4mp_authority_live tests/authority/live_authority_upload.cpp tools/headless/authority_driver.cpp)
  target_include_directories(x4mp_authority_live PRIVATE tools/headless tests/authority)
  target_link_libraries(x4mp_authority_live PRIVATE x4mp_core ws2_32)
  x4mp_set_warnings(x4mp_authority_live)
  x4mp_assert_no_sdk(x4mp_authority_live)
  add_test(NAME live.authority_upload_kill COMMAND x4mp_authority_live)
  set_tests_properties(live.authority_upload_kill PROPERTIES SKIP_RETURN_CODE 77 TIMEOUT 1200)
endif()
# X4MP_AUTHORITY end

# =============================================================================================
# BEGIN M2-10 save control + self-test + janitor skeleton (native/features/{saves,selftest,janitor,diag}). Appended by M2-10.
#   x4mp_host        += nlohmann_json (the bridge payloads of the features are small JSON objects)
#   x4mp_features_tests = Catch2 over a real ModHost + FakePlatform bridge (no SDK)
#   hostsim.selftest = the real DLL: selftest=true, a chat-style trigger, the table in the mod log
# =============================================================================================
target_link_libraries(x4mp_host PRIVATE nlohmann_json::nlohmann_json)
if(X4MP_TESTS)
  add_executable(x4mp_features_tests tests/test_features_saves_selftest.cpp tests/test_janitor.cpp tests/test_knowledge.cpp)  # test_janitor: M3-13; test_knowledge: M3-23
  target_link_libraries(x4mp_features_tests PRIVATE x4mp_host Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_features_tests)
  catch_discover_tests(x4mp_features_tests TEST_PREFIX "features." PROPERTIES TIMEOUT 60)
  cmake_language(DEFER CALL x4mp_assert_no_sdk x4mp_features_tests)

  add_test(NAME hostsim.selftest
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/selftest.hostsim"
            --var "SCRIPT_DIR=${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim")
  set_tests_properties(hostsim.selftest PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")

  # Close-out A item 2: /reloadui restores the universe-ready gate (X4Native never repeats on_universe_ready)
  add_test(NAME hostsim.reload_universe_ready
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/reload_universe_ready.hostsim"
            --var "SCRIPT_DIR=${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim")
  set_tests_properties(hostsim.reload_universe_ready PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
endif()
# END M2-10

# =============================================================================================
# BEGIN M2-06 join feature (client join flow). Appended by M2-06.
#   x4mp_host       += host/status_json.cpp (bridge topic payload builders); features/join/*.cpp is picked up by the M2-04 glob
#   x4mp_host_tests += tests/test_join_json.cpp, tests/test_status_json.cpp
#   hostsim         += scenarios that need no server (refused build text, bad join input); the server scenarios run through
#                      tests/hostsim/join_flow_run.ps1 (not CI: tools/e2e.ps1 is M2-14's)
# =============================================================================================
target_sources(x4mp_host PRIVATE native/host/status_json.cpp)
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_join_json.cpp tests/test_status_json.cpp tests/test_join_mods_json.cpp)  # test_join_mods_json: M2-X3
  target_link_libraries(x4mp_host_tests PRIVATE nlohmann_json::nlohmann_json)
  add_test(NAME hostsim.join_refused_build
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/join_refused_build.hostsim")
  set_tests_properties(hostsim.join_refused_build PROPERTIES TIMEOUT 60 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
  add_test(NAME hostsim.join_no_server
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/join_no_server.hostsim")
  set_tests_properties(hostsim.join_no_server PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
endif()
# =============================================================================================
# END M2-06 join feature
# =============================================================================================

# =============================================================================================
# BEGIN M2-09 authority in game (features/authority/*.cpp is picked up by the M2-04 glob). Appended by M2-09.
#   x4mp_host_tests += tests/test_authority_flow.cpp (MD collector, plan, save file watcher, ledger/janitor, stash state, join seam)
#   hostsim.authority_role = the real DLL: the join dialog's authority role + password handling, no server
#   the server scenarios run through tests/hostsim/authority_flow_run.ps1 (not CI: tools/e2e.ps1 is M2-14's)
# =============================================================================================
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_authority_flow.cpp)
  add_test(NAME hostsim.authority_role
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/authority_role.hostsim")
  set_tests_properties(hostsim.authority_role PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
endif()
# END M2-09 authority in game
# BEGIN M2-07 reload survival. Appended by M2-07.
#   features/resume/*.cpp is picked up by the M2-04 features glob.
#   x4mp_host_tests += tests/test_resume_state.cpp (epoch rule, state round trip)
#   hostsim         += resume_stash_fallback (missing/corrupt stash -> clean fresh join); the 20-reload server run is
#                      tests/hostsim/reload_survival_run.ps1 (not CI: tools/e2e.ps1 is M2-14's)
# =============================================================================================
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_resume_state.cpp)
  add_test(NAME hostsim.resume_stash_fallback
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/resume_stash_fallback.hostsim")
  set_tests_properties(hostsim.resume_stash_fallback PROPERTIES TIMEOUT 60 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
endif()
# =============================================================================================
# END M2-07 reload survival
# =============================================================================================

# =============================================================================================
# BEGIN M2-12 launch.json + NodeStats (features/launch/*.cpp, features/stats/*.cpp are picked up by the M2-04 glob). Appended by M2-12.
#   x4mp_host_tests += tests/test_launch_stats.cpp (launch parsing / expiry / redaction / consumption, stats percentiles + wire)
#   hostsim.launch_expired = the real DLL: an expired launch.json is ignored and deleted; the connecting scenario needs the server and
#   runs through tests/hostsim/launch_stats_run.ps1 (not CI: tools/e2e.ps1 is M2-14's)
# =============================================================================================
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_launch_stats.cpp)
  add_test(NAME hostsim.launch_expired
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/launch_expired.hostsim")
  set_tests_properties(hostsim.launch_expired PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
endif()
# END M2-12 launch.json + NodeStats

# M3-07: remembered Join fields in x4mp.json (core/config/remembered.cpp is in x4mp_core, tests/test_remembered.cpp in x4mp_core_tests).
#   hostsim.remembered_fields = the real DLL: x4mp.remember / x4mp.remembered, restart survival, one-time migration, no password in any file
if(X4MP_TESTS)
  add_test(NAME hostsim.remembered_fields
    COMMAND x4mp-hostsim --dll "$<TARGET_FILE:x4mp>" --script "${CMAKE_CURRENT_SOURCE_DIR}/tests/hostsim/remembered_fields.hostsim")
  set_tests_properties(hostsim.remembered_fields PROPERTIES TIMEOUT 90 PASS_REGULAR_EXPRESSION "HOSTSIM OK")
endif()
# END M3-07

# =============================================================================================
# BEGIN M3-02 core/ghost (sample ring, Hermite + slerp, adaptive delay, extrapolation, path-error stats, NetMap + registries with stash
# adoption, Replication decode). Appended by M3-02. Pure C++: x4mp_ghost_tests has no SDK on its include path. The tests replace the
# global operator new to count allocations (the hot-path test), so they live in their own executable.
# =============================================================================================
target_sources(x4mp_core PRIVATE
  native/core/ghost/interpolator.cpp
  native/core/ghost/sync_stats.cpp
  native/core/ghost/registry.cpp
  native/core/ghost/replication.cpp)
if(X4MP_TESTS)
  add_executable(x4mp_ghost_tests
    tests/ghost/alloc_counter.cpp
    tests/ghost/test_math.cpp
    tests/ghost/test_interpolator.cpp
    tests/ghost/test_tracks.cpp
    tests/ghost/test_registry.cpp
    tests/ghost/test_replication.cpp
    tests/ghost/test_hot_path.cpp)
  target_include_directories(x4mp_ghost_tests PRIVATE tests/ghost)
  target_link_libraries(x4mp_ghost_tests PRIVATE x4mp_core Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_ghost_tests)
  catch_discover_tests(x4mp_ghost_tests TEST_PREFIX "ghost." PROPERTIES TIMEOUT 120)
  x4mp_assert_no_sdk(x4mp_ghost_tests)
endif()
# END M3-02 core/ghost

# =============================================================================================
# BEGIN M3-03 UDP realtime lane (core/net/udp_lane.h is header-only, so x4mp_core needs no new source). Appended by M3-03.
#   x4mp_core_tests    += tests/test_udp_lane.cpp (datagram layout, ack window, Binding/Active/Fallback state machine, loss)
#   x4mp_udp_alloc_tests = the lane's per-frame path allocates nothing (own exe: it replaces the global operator new)
#   smoke.headless_udp_real_server = headless joins a real server with --udp force and expects the lane Active (skipped without
#   X4MP_TEST_SERVER). The full live scenarios (5 % loss, UDP blocked -> TCP within 3 s) are tests/hostsim/udp_lane_run.ps1.
# =============================================================================================
if(X4MP_TESTS)
  target_sources(x4mp_core_tests PRIVATE tests/test_udp_lane.cpp)
  add_executable(x4mp_udp_alloc_tests tests/test_udp_lane_alloc.cpp tests/ghost/alloc_counter.cpp)
  target_include_directories(x4mp_udp_alloc_tests PRIVATE tests/ghost)
  target_link_libraries(x4mp_udp_alloc_tests PRIVATE x4mp_core Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_udp_alloc_tests)
  catch_discover_tests(x4mp_udp_alloc_tests TEST_PREFIX "udp." PROPERTIES TIMEOUT 60)
  x4mp_assert_no_sdk(x4mp_udp_alloc_tests)
  add_test(NAME smoke.headless_udp_real_server COMMAND x4mp-headless --from-env --udp force --expect-udp active --ping 4)
  set_tests_properties(smoke.headless_udp_real_server PROPERTIES SKIP_RETURN_CODE 77 TIMEOUT 60)
endif()
# END M3-03 UDP realtime lane

# =============================================================================================
# BEGIN M3-06 chat + player list (features/chat/*.cpp is picked up by the M2-04 features glob; the hub in chat_json.* is the join
# feature's hand-over point for ChatMessage / RosterUpdate frames). Appended by M3-06.
#   x4mp_host_tests += tests/test_chat.cpp (chat_send parsing, ChatSend wire, roster tracker + join/leave events, hub)
#   The Lua half is tested by tests/lua/test_chat.lua / test_players.lua (run_all.lua); the round trip through a real server is part of
#   tests/hostsim/join_flow.hostsim (tools/e2e.ps1 -Steps Publish,JoinFlow).
# =============================================================================================
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_chat.cpp)
endif()
# END M3-06 chat + player list

# =============================================================================================
# BEGIN M3-08 team factions in game (features/teams/*.cpp is picked up by the M2-04 features glob; team_hub() is the join feature's
# hand-over point for Welcome / TeamTable / TeamRelations / TeamMemberChanged). Appended by M3-08.
#   x4mp_host_tests += tests/test_teams.cpp (model, matrix -> MD call list, apply state machine)
#   The MD half (md/x4mp_teams.xml, libraries/*.xml) is checked by tools/validate-x4-xml.py, the Lua half by tests/lua/test_teams.lua;
#   the round trip with the real DLL is part of tests/hostsim/join_flow.hostsim.
# =============================================================================================
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_teams.cpp)
endif()
# END M3-08 team factions in game
# BEGIN M3-09 own-ship tracker (features/selfship/*.cpp is picked up by the M2-04 features glob; the SDK-free game adapter is added to
# x4mp_game here). Appended by M3-09.
#   x4mp_game        += game/selfship_api.cpp (per-frame reads of the player's ship: seat, sector, highway, dock, pose)
#   x4mp_host_tests  += tests/test_selfship.cpp (galaxy map, tracker rates/edges/flags, SETA guard, adapter reads, authority self-spawn on the seat)
#   x4mp_selfship_alloc_tests = the per-frame path (adapter + tracker + map lookup) allocates nothing (own exe: replaces operator new)
#   The live scenarios are tests/hostsim/selfship*.hostsim (tools/e2e.ps1 -Steps HostSim / HostSimPair).
# =============================================================================================
target_sources(x4mp_game PRIVATE native/game/selfship_api.cpp)
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_selfship.cpp)
  add_executable(x4mp_selfship_alloc_tests tests/test_selfship_alloc.cpp tests/ghost/alloc_counter.cpp)
  target_include_directories(x4mp_selfship_alloc_tests PRIVATE tests/ghost)
  target_link_libraries(x4mp_selfship_alloc_tests PRIVATE x4mp_host Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_selfship_alloc_tests)
  catch_discover_tests(x4mp_selfship_alloc_tests TEST_PREFIX "selfship." PROPERTIES TIMEOUT 60)
  cmake_language(DEFER CALL x4mp_assert_no_sdk x4mp_selfship_alloc_tests)
endif()
# END M3-09 own-ship tracker

# =============================================================================================
# BEGIN M3-11 avatars on the authority (features/avatars/*.cpp is picked up by the M2-04 features glob). Appended by M3-11.
#   x4mp_game       += game/avatars_api.cpp (team-faction ship enumeration for the binder; resolved by name, GameFns stays frozen)
#   x4mp_host_tests += tests/test_avatars.cpp (plan, records, binder, AvatarDirector over a fake world)
#   The MD half (md/x4mp_avatars.xml) is checked by tools/validate-x4-xml.py, the Lua half by tests/lua/test_avatars.lua; the live round trip
#   (real DLL as authority + FakeNode bots) is tests/hostsim/avatars_run.ps1.
# =============================================================================================
target_sources(x4mp_game PRIVATE native/game/avatars_api.cpp)
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_avatars.cpp)
endif()
# END M3-11 avatars on the authority

# =============================================================================================
# BEGIN M3-12 client takeover (features/avatars/avatar_takeover.cpp + avatars_client.cpp come with the features glob). Appended by M3-12.
#   x4mp_host_tests += tests/test_takeover.cpp (AvatarTakeover over a fake game: refusal/retry, guard, removal order, reload records, wire decoders)
#   The live scenario (client ends in its avatar, host copy removed after the guard, reload mid-takeover) is tests/hostsim/pair_scenario.hostsim
#   and tests/hostsim/takeover.hostsim (tools/e2e.ps1 -Steps HostSimPair, HostSim).
# =============================================================================================
if(X4MP_TESTS)
  target_sources(x4mp_host_tests PRIVATE tests/test_takeover.cpp)
endif()
# END M3-12 client takeover

# BEGIN M3-10 features/ghosts (client ghosts: spawn, dress, move, hide, adopt). Appended by M3-10.
#   x4mp_game  += game/ghosts_api.cpp (native calls on top of GameApi, pose convention, faction / ship lists by name)
#   x4mp_host  += features/ghosts/*.cpp (picked up by the M2-04 features glob)
#   x4mp_ghosts_tests = Catch2 over a fake game: driver (spawn / dress / move / hide / sector change / parked label / adoption /
#     5 Hz hints / budget), message decoding, string table, no allocation per frame, perf with 7 ghosts (own exe: it
#     replaces the global operator new like x4mp_ghost_tests)
#   hostsim.ghosts_* + tests/hostsim/ghosts_run.ps1 = the real DLL against the real server and FakeNode bots (CI step HostSimGhosts)
# =============================================================================================
target_sources(x4mp_game PRIVATE native/game/ghosts_api.cpp)
if(X4MP_TESTS)
  add_executable(x4mp_ghosts_tests
    tests/ghost/alloc_counter.cpp
    tests/ghosts/test_driver.cpp
    tests/ghosts/test_messages.cpp
    tests/ghosts/test_perf.cpp)
  target_include_directories(x4mp_ghosts_tests PRIVATE tests/ghost tests/ghosts)
  target_link_libraries(x4mp_ghosts_tests PRIVATE x4mp_host Catch2::Catch2WithMain)
  x4mp_set_warnings(x4mp_ghosts_tests)
  catch_discover_tests(x4mp_ghosts_tests TEST_PREFIX "ghosts." PROPERTIES TIMEOUT 120)
  cmake_language(DEFER CALL x4mp_assert_no_sdk x4mp_ghosts_tests)
endif()
# END M3-10 features/ghosts
