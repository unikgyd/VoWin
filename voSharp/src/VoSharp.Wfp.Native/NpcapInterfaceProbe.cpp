// Read-only Npcap binding check for a Windows-owned IMS PPP interface.
// Does not capture, inject packets, or alter the selected interface.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <iphlpapi.h>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <cctype>
#include <string>

namespace {
struct PcapInterface {
    PcapInterface* next;
    char* name;
    char* description;
    void* addresses;
    unsigned flags;
};
using FindAll = int(__cdecl*)(PcapInterface**, char*);
using FreeAll = void(__cdecl*)(PcapInterface*);
using OpenLive = void*(__cdecl*)(const char*, int, int, int, char*);
using Close = void(__cdecl*)(void*);
using Datalink = int(__cdecl*)(void*);

template<class T> T Export(HMODULE library, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(library, name));
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 2 || !argv[1][0]) {
        std::puts("Usage: VoWinNpcapInterfaceProbe.exe <Windows-interface-index>");
        return 1;
    }
    wchar_t* end = nullptr;
    const unsigned long parsed = std::wcstoul(argv[1], &end, 10);
    if (!end || *end || parsed == 0 || parsed > UINT32_MAX) return 2;
    NET_LUID luid{};
    GUID guid{};
    if (ConvertInterfaceIndexToLuid(static_cast<NET_IFINDEX>(parsed), &luid) != NO_ERROR ||
        ConvertInterfaceLuidToGuid(&luid, &guid) != NO_ERROR) {
        std::puts("Windows interface index could not be mapped to a GUID.");
        return 2;
    }
    char guidText[40]{};
    std::snprintf(guidText, sizeof(guidText),
        "{%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
        guid.Data1, guid.Data2, guid.Data3, guid.Data4[0], guid.Data4[1],
        guid.Data4[2], guid.Data4[3], guid.Data4[4], guid.Data4[5],
        guid.Data4[6], guid.Data4[7]);
    wchar_t dllPath[MAX_PATH]{};
    const UINT systemLength = GetSystemDirectoryW(dllPath, MAX_PATH);
    if (systemLength == 0 || systemLength >= MAX_PATH - 17 ||
        wcscat_s(dllPath, L"\\Npcap\\wpcap.dll") != 0) return 2;
    HMODULE library = LoadLibraryExW(dllPath, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!library) {
        std::printf("Npcap DLL unavailable: %lu\n", GetLastError());
        return 2;
    }
    const auto findAll = Export<FindAll>(library, "pcap_findalldevs");
    const auto freeAll = Export<FreeAll>(library, "pcap_freealldevs");
    const auto openLive = Export<OpenLive>(library, "pcap_open_live");
    const auto close = Export<Close>(library, "pcap_close");
    const auto datalink = Export<Datalink>(library, "pcap_datalink");
    int result = 2;
    PcapInterface* devices = nullptr;
    void* capture = nullptr;
    char error[256]{};
    do {
        if (!findAll || !freeAll || !openLive || !close || !datalink ||
            findAll(&devices, error) != 0) {
            std::printf("Npcap enumeration unavailable: %s\n", error);
            break;
        }
        std::string adapterName;
        for (auto* device = devices; device; device = device->next) {
            if (!device->name) continue;
            std::string upper = device->name;
            for (char& ch : upper)
                ch = static_cast<char>(std::toupper(static_cast<unsigned char>(ch)));
            if (upper.find(guidText) != std::string::npos) {
                adapterName = device->name;
                break;
            }
        }
        if (adapterName.empty()) {
            std::printf("Npcap does not enumerate interface index %lu GUID %s\n",
                parsed, guidText);
            break;
        }
        capture = openLive(adapterName.c_str(), 256, 0, 100, error);
        if (!capture) {
            std::printf("Npcap cannot open interface index %lu: %s\n",
                parsed, error);
            break;
        }
        const int linkType = datalink(capture);
        std::printf("Npcap opened interface index %lu GUID %s; DLT=%d\n",
            parsed, guidText, linkType);
        result = linkType == 12 ? 0 : 2; // DLT_RAW: IPv6 starts at byte zero.
    } while (false);
    if (capture) close(capture);
    if (devices && freeAll) freeAll(devices);
    FreeLibrary(library);
    return result;
}
