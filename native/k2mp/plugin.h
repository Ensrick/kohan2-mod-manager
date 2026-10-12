// Shared state of Multiplayer Fixes: where the game lives, logging, settings, file hashing.
#pragma once
#include <cstdint>
#include <string>

struct K2Host;

namespace k2mp {

struct Context {
    uintptr_t exeBase = 0;
    std::wstring gameDir;  // folder of k2.exe
    std::wstring modsDir;  // <game>\Mods
    const K2Host* host = nullptr;
};

extern Context g;

// The game's work depot (where it writes saves): its own "%USERDATA%" folder + "\data", read from the game, so it is
// right whatever Windows says Documents is (redirected folders, the test rig). Falls back to Documents\Kohan2\data
// when the game's value cannot be read. Call it after start-up, not from K2Plugin_Start.
std::wstring UserDataDir();

// One line in Mods\plugins.log, prefixed "k2mp". printf-style, UTF-8; %ls for wide strings.
void Log(const char* fmt, ...);

// Mods\k2mp.ini, [features] section: `name = 1` or `0`. Missing file or key: `fallback`.
bool Setting(const wchar_t* name, bool fallback);

// The checksum the lobby compares when the host loads a saved game (KKC_Session::SendUpdateMapStatus 0x568660 via
// 0x702e3d): h = h * 7 + byte over the whole file, 32-bit wrap. Logged by the game as a signed "_crc". Matches the
// game's value for a real save (902,387 bytes -> -1815980196). False if the file cannot be read.
bool LobbyChecksum(const std::wstring& path, uint32_t* checksum);
uint32_t LobbyChecksum(const unsigned char* data, size_t size, uint32_t h = 0);

// SHA-256 of a file as 64 lowercase hex digits, and its size; false if it cannot be read.
bool HashFile(const std::wstring& path, std::string* hexDigest, uint64_t* size);

// Hashes the file once it has stopped changing (up to `timeoutMs`), on a background thread, and logs the result
// with `label`. For a save another thread of the game is still writing.
void HashWhenSettled(const std::wstring& path, const std::string& label, unsigned timeoutMs);

// UTF-16 to UTF-8, for logging.
std::string Utf8(const std::wstring& s);

}  // namespace k2mp
