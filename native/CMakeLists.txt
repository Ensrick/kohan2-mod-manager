cmake_minimum_required(VERSION 3.20)
project(kohan2_plugins LANGUAGES CXX RC)

# k2.exe is a 32-bit program: configure with -A Win32 (build.ps1 does).
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "Kohan II is a 32-bit exe: configure with -A Win32")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")  # static CRT, no redist needed
set(COMMON_DEFS WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS)
set(COMMON_OPTS /W4 /permissive- /utf-8)

# Version resource for one DLL (see version.rc.in). VERSION is semver, a -dev.N / -beta.N suffix allowed.
function(k2_version_rc target id file product title comments version)
    string(REGEX MATCH "^([0-9]+)\\.([0-9]+)\\.([0-9]+)" _ "${version}")
    set(V_MAJOR ${CMAKE_MATCH_1})
    set(V_MINOR ${CMAKE_MATCH_2})
    set(V_PATCH ${CMAKE_MATCH_3})
    set(V_STRING "${version}")
    set(V_ID "${id}")
    set(V_FILE "${file}")
    set(V_PRODUCT "${product}")
    set(V_TITLE "${title}")
    set(V_COMMENTS "${comments}")
    configure_file(${CMAKE_SOURCE_DIR}/version.rc.in ${CMAKE_BINARY_DIR}/${target}_version.rc @ONLY)
    target_sources(${target} PRIVATE ${CMAKE_BINARY_DIR}/${target}_version.rc)
endfunction()

# ---------------------------------------------------------------- the loader: winmm.dll in the game folder
set(LOADER_VERSION "0.1.0")
add_library(loader SHARED loader/dllmain.cpp loader/winmm.def)
set_target_properties(loader PROPERTIES OUTPUT_NAME winmm)
target_include_directories(loader PRIVATE include)
target_compile_definitions(loader PRIVATE ${COMMON_DEFS} LOADER_VERSION="${LOADER_VERSION}")
target_compile_options(loader PRIVATE ${COMMON_OPTS})
target_link_options(loader PRIVATE /SAFESEH:NO)
k2_version_rc(loader "loader" "winmm.dll" "Kohan II Plugin Loader"
    "Kohan II plugin loader"
    "Loads the Kohan II plugins listed in Mods\\\\plugins.txt. Installed and removed by the Kohan II Mod Manager."
    "${LOADER_VERSION}")

# ---------------------------------------------------------------- plugin: Multiplayer Fixes
set(K2MP_VERSION "0.1.0-dev.1")
add_library(k2mp SHARED k2mp/k2mp.cpp)
target_include_directories(k2mp PRIVATE include)
target_compile_definitions(k2mp PRIVATE ${COMMON_DEFS} K2MP_VERSION="${K2MP_VERSION}")
target_compile_options(k2mp PRIVATE ${COMMON_OPTS})
k2_version_rc(k2mp "k2mp" "k2mp.dll" "Kohan II plugin"
    "Multiplayer Fixes"
    "Will let players who drop out of a multiplayer match rejoin it. Work in progress: this version only checks your k2.exe and changes nothing in the game yet."
    "${K2MP_VERSION}")

# ---------------------------------------------------------------- tests
# Stand-in for the game: an exe NAMED k2.exe that imports winmm.dll and calls timeGetTime from its own code, like the
# game does. test/loader_test.ps1 runs it next to the loader and checks Mods\plugins.log.
add_executable(fake_k2 test/fake_k2.cpp)
set_target_properties(fake_k2 PROPERTIES OUTPUT_NAME k2
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR}/fake_game RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_BINARY_DIR}/fake_game)
target_compile_definitions(fake_k2 PRIVATE ${COMMON_DEFS})
target_link_libraries(fake_k2 PRIVATE Winmm.lib)  # the SDK import library, not our loader target

# Maps a decrypted copy of k2.exe (never runs it) and checks k2mp's signatures against it.
add_executable(sigtest test/sigtest.cpp)
target_compile_definitions(sigtest PRIVATE ${COMMON_DEFS})
