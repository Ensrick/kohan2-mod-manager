#include "plugin.h"

#include <windows.h>
#include <bcrypt.h>
#include <cstdarg>
#include <cstdio>
#include <thread>

#include "k2plugin.h"

#pragma comment(lib, "bcrypt.lib")

namespace k2mp {

Context g;

void Log(const char* fmt, ...) {
    char line[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(line, _TRUNCATE, fmt, ap);
    va_end(ap);
    if (g.host && g.host->log) g.host->log("k2mp", "%s", line);
}

bool Setting(const wchar_t* name, bool fallback) {
    const std::wstring ini = g.modsDir + L"\\k2mp.ini";
    return GetPrivateProfileIntW(L"features", name, fallback ? 1 : 0, ini.c_str()) != 0;
}

uint32_t LobbyChecksum(const unsigned char* data, size_t size, uint32_t h) {
    for (size_t i = 0; i < size; ++i) h = h * 7 + data[i];
    return h;
}

bool LobbyChecksum(const std::wstring& path, uint32_t* checksum) {
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    static thread_local unsigned char buf[1 << 16];
    uint32_t h = 0;
    DWORD got = 0;
    bool ok = true;
    while ((ok = ReadFile(f, buf, sizeof(buf), &got, nullptr) != 0) && got > 0) h = LobbyChecksum(buf, got, h);
    CloseHandle(f);
    if (!ok) return false;
    *checksum = h;
    return true;
}

bool HashFile(const std::wstring& path, std::string* hexDigest, uint64_t* size) {
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE h = nullptr;
    bool ok = BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0 &&
              BCryptCreateHash(alg, &h, nullptr, 0, nullptr, 0, 0) == 0;
    uint64_t total = 0;
    static thread_local unsigned char buf[1 << 16];
    DWORD got = 0;
    while (ok && ReadFile(f, buf, sizeof(buf), &got, nullptr) && got > 0) {
        ok = BCryptHashData(h, buf, got, 0) == 0;
        total += got;
    }
    unsigned char digest[32];
    ok = ok && BCryptFinishHash(h, digest, sizeof(digest), 0) == 0;
    if (h) BCryptDestroyHash(h);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    CloseHandle(f);
    if (!ok) return false;
    char hex[65];
    for (int i = 0; i < 32; ++i) sprintf_s(hex + i * 2, 3, "%02x", digest[i]);
    *hexDigest = hex;
    *size = total;
    return true;
}

namespace {

// Size and last-write time, or false if the file is missing or still open for writing: sharing only FILE_SHARE_READ
// makes the open fail while any other handle can write to it.
bool Snapshot(const std::wstring& path, uint64_t* size, FILETIME* written) {
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER sz;
    const bool ok = GetFileSizeEx(f, &sz) && GetFileTime(f, nullptr, nullptr, written);
    CloseHandle(f);
    *size = static_cast<uint64_t>(sz.QuadPart);
    return ok;
}

}  // namespace

void HashWhenSettled(const std::wstring& path, const std::string& label, unsigned timeoutMs) {
    std::thread([path, label, timeoutMs] {
        const ULONGLONG start = GetTickCount64();
        uint64_t lastSize = ~0ull;
        FILETIME lastTime = {};
        while (GetTickCount64() - start < timeoutMs) {
            Sleep(500);
            uint64_t size;
            FILETIME t;
            if (!Snapshot(path, &size, &t) || size == 0) continue;
            if (size == lastSize && CompareFileTime(&t, &lastTime) == 0) {
                std::string hex;
                uint64_t n;
                if (HashFile(path, &hex, &n))
                    Log("%s: %llu bytes, sha256 %s", label.c_str(), n, hex.c_str());
                else
                    Log("%s: could not read the file", label.c_str());
                return;
            }
            lastSize = size;
            lastTime = t;
        }
        Log("%s: the file did not appear or kept changing for %u s", label.c_str(), timeoutMs / 1000);
    }).detach();
}

std::string Utf8(const std::wstring& s) {
    if (s.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
    std::string o(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), o.data(), n, nullptr, nullptr);
    return o;
}

}  // namespace k2mp
