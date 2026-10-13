// The game's own script commands, run from the plugin: the same commands a player can put on the game's command line
// ("+GameSave ...") or in a script, queued the way KKC_Application::ParseCommandLine queues them. Game state: call Run
// only on the game's own thread (K2Plugin_Tick).
#pragma once
#include <cstdint>

namespace script {

// Finds the game's console. False when the code differs from the build this plugin knows.
bool Install(uintptr_t exeBase);

// Queues one command, e.g. L"IgnoreAsserts" or L"GameSave \"Save/X.RSG\"". False while the console does not exist
// yet, or if the call failed (logged).
bool Run(const wchar_t* command);

}  // namespace script
