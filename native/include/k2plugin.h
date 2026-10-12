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

// Optional export (extern "C", __cdecl): called on the game's own thread (the one that started the plugins) from its
// timer calls, at most every 50 ms, for as long as the game runs. The place to read or change game state: never do
// that from a thread of the plugin's own, the game's data is not guarded against other threads.
typedef void(__cdecl* K2PluginTickFn)(void);
#define K2_PLUGIN_TICK "K2Plugin_Tick"
