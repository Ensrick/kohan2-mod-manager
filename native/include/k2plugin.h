// Kohan II plugin interface, version 1.
//
// A plugin is a 32-bit DLL in <game>\Mods\ listed in <game>\Mods\plugins.txt (the manager writes that list). The
// plugin loader (winmm.dll in the game folder) loads each listed DLL into k2.exe once the game's own code is running,
// and calls its K2Plugin_Start with the host below. Plugins find their way around k2.exe by RVA (the exe is
// relocated, so address = exeBase + RVA) and must check the bytes they patch first: after a game update they find
// different bytes and should stay off rather than patch blind.
#pragma once
#include <stdint.h>

#define K2_PLUGIN_API 1

typedef struct K2Host {
    uint32_t api;            // K2_PLUGIN_API
    uintptr_t exeBase;       // where k2.exe is loaded
    const wchar_t* gameDir;  // the folder holding k2.exe, no trailing backslash
    const wchar_t* modsDir;  // <gameDir>\Mods
    // Writes one line to <game>\Mods\plugins.log, prefixed with the time and `source`.
    void(__cdecl* log)(const char* source, const char* fmt, ...);
} K2Host;

// Every plugin exports this (extern "C", __cdecl). Return 0 when the plugin is running, anything else when it stayed
// off; the loader logs the result either way and never unloads a plugin.
typedef int(__cdecl* K2PluginStartFn)(const K2Host* host);
#define K2_PLUGIN_START "K2Plugin_Start"
