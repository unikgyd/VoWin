// Wintun adapter discovery and bounded, isolated packet-capture lab. The DLL
// path is explicit and the temporary adapter is always closed by this process.
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <bcrypt.h>
#include "HostImsWfp.h"
#include <cstdio>
#include <cwchar>
#include <cstring>
#include <cctype>
#include <string>
#include <vector>

namespace {
using Adapter = void*;
using OpenAdapter = Adapter(WINAPI*)(LPCWSTR);
using CreateAdapter = Adapter(WINAPI*)(LPCWSTR, LPCWSTR, const GUID*);
using CloseAdapter = void(WINAPI*)(Adapter);
using GetAdapterLuid = void(WINAPI*)(Adapter, NET_LUID*);
using Session = void*;
using StartSession = Session(WINAPI*)(Adapter, DWORD);
using EndSession = void(WINAPI*)(Session);
using GetReadWaitEvent = HANDLE(WINAPI*)(Session);
using ReceivePacket = BYTE*(WINAPI*)(Session, DWORD*);
using ReleaseReceivePacket = void(WINAPI*)(Session, const BYTE*);
using AllocateSendPacket = BYTE*(WINAPI*)(Session, DWORD);
using SendPacket = void(WINAPI*)(Session, const BYTE*);
struct PcapInterface {
    PcapInterface* next;
    char* name;
    char* description;
    void* addresses;
    unsigned flags;
};
struct PcapHeader {
    timeval timestamp;
    unsigned capturedLength;
    unsigned wireLength;
};
using PcapFindAll = int(__cdecl*)(PcapInterface**, char*);
using PcapFreeAll = void(__cdecl*)(PcapInterface*);
using PcapOpenLive = void*(__cdecl*)(const char*, int, int, int, char*);
using PcapClose = void(__cdecl*)(void*);
using PcapSetNonblock = int(__cdecl*)(void*, int, char*);
using PcapNextEx = int(__cdecl*)(void*, PcapHeader**, const BYTE**);
using PcapDatalink = int(__cdecl*)(void*);
using PcapSendPacket = int(__cdecl*)(void*, const BYTE*, int);
using PcapGetError = const char*(__cdecl*)(void*);
using DivertOpen = HANDLE(WINAPI*)(const char*, int, INT16, UINT64);
using DivertClose = BOOL(WINAPI*)(HANDLE);
using DivertRecvEx = BOOL(WINAPI*)(HANDLE, void*, UINT, UINT*, UINT64,
                                  void*, UINT*, OVERLAPPED*);
using DivertSend = BOOL(WINAPI*)(HANDLE, const void*, UINT, UINT*, const void*);
using DivertCompileFilter = BOOL(WINAPI*)(const char*, int, char*, UINT,
                                         const char**, UINT*);
struct DivertNetworkAddress {
    INT64 timestamp;
    UINT32 layer:8;
    UINT32 event:8;
    UINT32 sniffed:1;
    UINT32 outbound:1;
    UINT32 loopback:1;
    UINT32 impostor:1;
    UINT32 ipv6:1;
    UINT32 ipChecksum:1;
    UINT32 tcpChecksum:1;
    UINT32 udpChecksum:1;
    UINT32 reserved1:8;
    UINT32 reserved2;
    UINT32 ifIdx;
    UINT32 subIfIdx;
    BYTE reserved3[56];
};
static_assert(sizeof(DivertNetworkAddress) == 80);

constexpr BYTE kLocal[16] = {
    0xfd, 0x7a, 0x6f, 0x57, 0x49, 0x4d, 0x53, 0x01,
    0, 0, 0, 0, 0, 0, 0, 1
};
constexpr BYTE kPeer[16] = {
    0xfd, 0x7a, 0x6f, 0x57, 0x49, 0x4d, 0x53, 0x01,
    0, 0, 0, 0, 0, 0, 0, 2
};

template<class T> T LoadExport(HMODULE library, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(library, name));
}

bool RewriteInboundSpiAndIcv(std::vector<BYTE>& packet, UINT32 inboundSpi) {
    if (packet.size() < 60 || packet[6] != 50) return false;
    const UINT32 wireSpi = htonl(inboundSpi);
    std::memcpy(packet.data() + 40, &wireSpi, sizeof(wireSpi));
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    const NTSTATUS opened = BCryptOpenAlgorithmProvider(&algorithm,
        BCRYPT_SHA1_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG);
    if (opened < 0) {
        std::printf("CNG HMAC-SHA1 provider failed: 0x%08lx\n", opened);
        return false;
    }
    UCHAR key[20]{}; // Same synthetic lab key installed in WFP; never a subscriber key.
    UCHAR tag[20]{};
    const NTSTATUS hashed = BCryptHash(algorithm, key, sizeof(key),
        packet.data() + 40, static_cast<ULONG>(packet.size() - 40 - 12),
        tag, sizeof(tag));
    const NTSTATUS closed = BCryptCloseAlgorithmProvider(algorithm, 0);
    if (hashed < 0 || closed < 0) {
        std::printf("CNG HMAC-SHA1 failed: hash=0x%08lx close=0x%08lx\n",
            hashed, closed);
        return false;
    }
    std::memcpy(packet.data() + packet.size() - 12, tag, 12);
    return true;
}

bool CaptureSyntheticEspWithNpcap(Session session, const NET_LUID& luid,
                                  AllocateSendPacket allocateSend,
                                  SendPacket sendPacket,
                                  ReceivePacket receive,
                                  ReleaseReceivePacket release,
                                  GetReadWaitEvent waitEvent,
                                  bool sendFromNpcap) {
    wchar_t dllPath[MAX_PATH]{};
    const UINT systemLength = GetSystemDirectoryW(dllPath, MAX_PATH);
    if (systemLength == 0 || systemLength >= MAX_PATH - 17 ||
        wcscat_s(dllPath, L"\\Npcap\\wpcap.dll") != 0) return false;
    HMODULE library = LoadLibraryExW(dllPath, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!library) {
        std::printf("Npcap DLL load failed: %lu\n", GetLastError());
        return false;
    }
    const auto findAll = LoadExport<PcapFindAll>(library, "pcap_findalldevs");
    const auto freeAll = LoadExport<PcapFreeAll>(library, "pcap_freealldevs");
    const auto openLive = LoadExport<PcapOpenLive>(library, "pcap_open_live");
    const auto close = LoadExport<PcapClose>(library, "pcap_close");
    const auto setNonblock = LoadExport<PcapSetNonblock>(library, "pcap_setnonblock");
    const auto nextEx = LoadExport<PcapNextEx>(library, "pcap_next_ex");
    const auto datalink = LoadExport<PcapDatalink>(library, "pcap_datalink");
    const auto pcapSend = LoadExport<PcapSendPacket>(library, "pcap_sendpacket");
    const auto getError = LoadExport<PcapGetError>(library, "pcap_geterr");
    if (!findAll || !freeAll || !openLive || !close || !setNonblock ||
        !nextEx || !datalink || (sendFromNpcap && (!pcapSend || !getError))) {
        std::puts("Npcap DLL lacks required capture exports.");
        FreeLibrary(library);
        return false;
    }
    GUID guid{};
    if (ConvertInterfaceLuidToGuid(&luid, &guid) != NO_ERROR) {
        FreeLibrary(library);
        return false;
    }
    char guidText[40]{};
    std::snprintf(guidText, sizeof(guidText),
        "{%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
        guid.Data1, guid.Data2, guid.Data3, guid.Data4[0], guid.Data4[1],
        guid.Data4[2], guid.Data4[3], guid.Data4[4], guid.Data4[5],
        guid.Data4[6], guid.Data4[7]);
    char error[256]{};
    PcapInterface* devices = nullptr;
    void* capture = nullptr;
    bool matched = false;
    do {
        if (findAll(&devices, error) != 0) {
            std::printf("Npcap adapter enumeration failed: %s\n", error);
            break;
        }
        std::string adapterName;
        for (auto* device = devices; device; device = device->next) {
            if (!device->name) continue;
            std::string name = device->name;
            for (char& ch : name)
                ch = static_cast<char>(std::toupper(static_cast<unsigned char>(ch)));
            if (name.find(guidText) != std::string::npos) {
                adapterName = device->name;
                break;
            }
        }
        if (adapterName.empty()) {
            std::printf("Npcap did not enumerate temporary Wintun adapter %s\n",
                guidText);
            break;
        }
        capture = openLive(adapterName.c_str(), 256, 0, 100, error);
        if (!capture) {
            std::printf("Npcap open temporary adapter failed: %s\n", error);
            break;
        }
        const int linkType = datalink(capture);
        std::printf("Npcap temporary Wintun capture: DLT=%d\n", linkType);
        const int offset = linkType == 12 ? 0 : linkType == 0 ? 4 :
                           linkType == 1 ? 14 : -1;
        if (offset < 0 || setNonblock(capture, 1, error) != 0) {
            std::printf("Unsupported Npcap DLT/nonblocking mode: %s\n", error);
            break;
        }
        BYTE inbound[56]{};
        inbound[0] = 0x60;
        inbound[5] = 16;
        inbound[6] = 50;
        inbound[7] = 64;
        std::memcpy(inbound + 8, sendFromNpcap ? kLocal : kPeer, 16);
        std::memcpy(inbound + 24, sendFromNpcap ? kPeer : kLocal, 16);
        constexpr BYTE espPayload[16] = {
            0x11, 0x22, 0x33, 0x44, 0, 0, 0, 1,
            'V', 'o', 'W', 'i', 'n', 'E', 'S', 'P'
        };
        std::memcpy(inbound + 40, espPayload, sizeof(espPayload));
        if (sendFromNpcap) {
            if (pcapSend(capture, inbound, sizeof(inbound)) != 0) {
                std::printf("Npcap raw ESP send failed: %s\n", getError(capture));
                break;
            }
            const ULONGLONG until = GetTickCount64() + 5000;
            while (GetTickCount64() < until) {
                DWORD length = 0;
                BYTE* packet = receive(session, &length);
                if (!packet) {
                    if (GetLastError() != ERROR_NO_MORE_ITEMS) break;
                    WaitForSingleObject(waitEvent(session), 100);
                    continue;
                }
                matched = length == sizeof(inbound) &&
                    std::memcmp(packet, inbound, sizeof(inbound)) == 0;
                release(session, packet);
                if (matched) {
                    std::printf("Wintun received Npcap-sent outbound ESP: bytes=%lu\n",
                        length);
                    break;
                }
            }
            if (!matched)
                std::puts("Wintun did not receive the Npcap-sent outbound ESP packet.");
            break;
        }
        BYTE* sendBuffer = allocateSend(session, sizeof(inbound));
        if (!sendBuffer) {
            std::printf("WintunAllocateSendPacket failed: %lu\n", GetLastError());
            break;
        }
        std::memcpy(sendBuffer, inbound, sizeof(inbound));
        sendPacket(session, sendBuffer);
        const ULONGLONG until = GetTickCount64() + 5000;
        while (GetTickCount64() < until) {
            PcapHeader* header = nullptr;
            const BYTE* data = nullptr;
            const int next = nextEx(capture, &header, &data);
            if (next < 0) {
                std::printf("Npcap read failed: %d\n", next);
                break;
            }
            if (next == 1 && header && data &&
                header->capturedLength >= static_cast<unsigned>(offset + sizeof(inbound)) &&
                std::memcmp(data + offset, inbound, sizeof(inbound)) == 0) {
                matched = true;
                std::printf("Npcap captured injected inbound ESP before Winsock: bytes=%u\n",
                    header->capturedLength);
                break;
            }
            Sleep(10);
        }
        if (!matched) std::puts("Npcap did not capture the synthetic inbound ESP packet.");
    } while (false);
    if (capture) close(capture);
    if (devices) freeAll(devices);
    FreeLibrary(library);
    return matched;
}

bool CaptureSyntheticEspWithWinDivert(Session session, NET_IFINDEX index,
                                      AllocateSendPacket allocateSend,
                                      SendPacket sendPacket,
                                      const wchar_t* dllPath) {
    HMODULE library = LoadLibraryExW(dllPath, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!library) {
        std::printf("WinDivert DLL load failed: %lu\n", GetLastError());
        return false;
    }
    const auto open = LoadExport<DivertOpen>(library, "WinDivertOpen");
    const auto close = LoadExport<DivertClose>(library, "WinDivertClose");
    const auto receive = LoadExport<DivertRecvEx>(library, "WinDivertRecvEx");
    const auto compile = LoadExport<DivertCompileFilter>(library,
                                                         "WinDivertHelperCompileFilter");
    bool matched = false;
    HANDLE divert = INVALID_HANDLE_VALUE;
    HANDLE event = nullptr;
    do {
        if (!open || !close || !receive || !compile) {
            std::puts("WinDivert DLL lacks required read-only exports.");
            break;
        }
        char filter[320]{};
        std::snprintf(filter, sizeof(filter),
            "inbound and ipv6 and protocol == 50 and ifIdx == %lu and "
            "localAddr == fd7a:6f57:494d:5301::1 and "
            "remoteAddr == fd7a:6f57:494d:5301::2", index);
        const char* filterError = nullptr;
        UINT errorPosition = 0;
        if (!compile(filter, 0, nullptr, 0, &filterError, &errorPosition)) {
            std::printf("WinDivert filter invalid at %u: %s\n", errorPosition,
                filterError ? filterError : "unknown");
            break;
        }
        // SNIFF | RECV_ONLY: matching packets are copied, never diverted.
        divert = open(filter, 0, 0, 0x0001u | 0x0004u);
        if (divert == INVALID_HANDLE_VALUE) {
            std::printf("WinDivertOpen failed: %lu\n", GetLastError());
            break;
        }
        event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!event) break;
        OVERLAPPED overlapped{};
        overlapped.hEvent = event;
        BYTE captured[256]{};
        BYTE address[128]{};
        UINT capturedLength = 0;
        UINT addressLength = sizeof(address);
        const BOOL immediate = receive(divert, captured, sizeof(captured),
            &capturedLength, 0, address, &addressLength, &overlapped);
        const DWORD pending = immediate ? ERROR_SUCCESS : GetLastError();
        if (!immediate && pending != ERROR_IO_PENDING) {
            std::printf("WinDivertRecvEx setup failed: %lu\n", pending);
            break;
        }
        BYTE inbound[56]{};
        inbound[0] = 0x60;
        inbound[5] = 16;
        inbound[6] = 50;
        inbound[7] = 64;
        std::memcpy(inbound + 8, kPeer, 16);
        std::memcpy(inbound + 24, kLocal, 16);
        constexpr BYTE payload[16] = {
            0x11, 0x22, 0x33, 0x44, 0, 0, 0, 1,
            'V', 'o', 'W', 'i', 'n', 'E', 'S', 'P'
        };
        std::memcpy(inbound + 40, payload, sizeof(payload));
        BYTE* sendBuffer = allocateSend(session, sizeof(inbound));
        if (!sendBuffer) {
            std::printf("WintunAllocateSendPacket failed: %lu\n", GetLastError());
            if (!immediate) {
                CancelIoEx(divert, &overlapped);
                WaitForSingleObject(event, 5000);
            }
            break;
        }
        std::memcpy(sendBuffer, inbound, sizeof(inbound));
        sendPacket(session, sendBuffer);
        const DWORD wait = immediate ? WAIT_OBJECT_0 :
                           WaitForSingleObject(event, 5000);
        if (wait != WAIT_OBJECT_0) {
            std::printf("WinDivert sniff wait timed out: %lu\n", wait);
            CancelIoEx(divert, &overlapped);
            WaitForSingleObject(event, 5000);
            break;
        }
        DWORD transferred = 0;
        if (!immediate && !GetOverlappedResult(divert, &overlapped,
                                               &transferred, FALSE)) {
            std::printf("WinDivert sniff completion failed: %lu\n", GetLastError());
            break;
        }
        if (!immediate) capturedLength = transferred;
        matched = capturedLength == sizeof(inbound) &&
            std::memcmp(captured, inbound, sizeof(inbound)) == 0;
        std::printf("WinDivert scoped inbound ESP: bytes=%u; exact-match=%s\n",
            capturedLength, matched ? "yes" : "no");
    } while (false);
    if (event) CloseHandle(event);
    if (divert != INVALID_HANDLE_VALUE) close(divert);
    FreeLibrary(library);
    return matched;
}

bool InjectSyntheticEspWithWinDivert(Session session, NET_IFINDEX index,
                                    ReceivePacket receive, ReleaseReceivePacket release,
                                    GetReadWaitEvent waitEvent,
                                    const wchar_t* dllPath) {
    HMODULE library = LoadLibraryExW(dllPath, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!library) {
        std::printf("WinDivert DLL load failed: %lu\n", GetLastError());
        return false;
    }
    const auto open = LoadExport<DivertOpen>(library, "WinDivertOpen");
    const auto close = LoadExport<DivertClose>(library, "WinDivertClose");
    const auto send = LoadExport<DivertSend>(library, "WinDivertSend");
    bool matched = false;
    HANDLE divert = INVALID_HANDLE_VALUE;
    do {
        if (!open || !close || !send) {
            std::puts("WinDivert DLL lacks packet injection exports.");
            break;
        }
        // SEND_ONLY never captures or blocks unrelated packets.
        divert = open("false", 0, 0, 0x0008u);
        if (divert == INVALID_HANDLE_VALUE) {
            std::printf("WinDivert send-only open failed: %lu\n", GetLastError());
            break;
        }
        BYTE outbound[56]{};
        outbound[0] = 0x60;
        outbound[5] = 16;
        outbound[6] = 50;
        outbound[7] = 64;
        std::memcpy(outbound + 8, kLocal, 16);
        std::memcpy(outbound + 24, kPeer, 16);
        constexpr BYTE payload[16] = {
            0x55, 0x66, 0x77, 0x88, 0, 0, 0, 1,
            'V', 'o', 'W', 'i', 'n', 'S', 'e', 'n'
        };
        std::memcpy(outbound + 40, payload, sizeof(payload));
        DivertNetworkAddress address{};
        address.outbound = 1;
        address.ipv6 = 1;
        address.ifIdx = index; // Outbound route still makes the final choice.
        UINT sent = 0;
        if (!send(divert, outbound, sizeof(outbound), &sent, &address) ||
            sent != sizeof(outbound)) {
            std::printf("WinDivertSend failed: %lu; bytes=%u\n", GetLastError(), sent);
            break;
        }
        const ULONGLONG until = GetTickCount64() + 5000;
        while (GetTickCount64() < until) {
            DWORD length = 0;
            BYTE* packet = receive(session, &length);
            if (!packet) {
                if (GetLastError() != ERROR_NO_MORE_ITEMS) break;
                WaitForSingleObject(waitEvent(session), 100);
                continue;
            }
            matched = length == sizeof(outbound) &&
                std::memcmp(packet, outbound, sizeof(outbound)) == 0;
            release(session, packet);
            if (matched) {
                std::printf("Wintun received WinDivert-injected outbound ESP: bytes=%lu\n",
                    length);
                break;
            }
        }
        if (!matched)
            std::puts("Wintun did not receive the WinDivert-injected ESP packet.");
    } while (false);
    if (divert != INVALID_HANDLE_VALUE) close(divert);
    FreeLibrary(library);
    return matched;
}

bool ExchangeWithManagedWinDivert(Session session, ReceivePacket receive,
                                  ReleaseReceivePacket release,
                                  GetReadWaitEvent waitEvent,
                                  AllocateSendPacket allocateSend,
                                  SendPacket sendPacket) {
    BYTE outbound[56]{};
    outbound[0] = 0x60;
    outbound[5] = 16;
    outbound[6] = 50;
    outbound[7] = 64;
    std::memcpy(outbound + 8, kLocal, 16);
    std::memcpy(outbound + 24, kPeer, 16);
    constexpr BYTE tx[16] = {
        0x55, 0x66, 0x77, 0x88, 0, 0, 0, 1,
        'V', 'o', 'W', 'i', 'n', 'S', 'e', 'n'
    };
    std::memcpy(outbound + 40, tx, sizeof(tx));
    const ULONGLONG until = GetTickCount64() + 30000;
    bool matched = false;
    while (GetTickCount64() < until) {
        DWORD length = 0;
        BYTE* packet = receive(session, &length);
        if (!packet) {
            if (GetLastError() != ERROR_NO_MORE_ITEMS) break;
            WaitForSingleObject(waitEvent(session), 100);
            continue;
        }
        matched = length == sizeof(outbound) &&
            std::memcmp(packet, outbound, sizeof(outbound)) == 0;
        release(session, packet);
        if (matched) break;
    }
    if (!matched) {
        std::puts("Managed WinDivert did not send the expected ESP packet.");
        return false;
    }
    BYTE inbound[56]{};
    inbound[0] = 0x60;
    inbound[5] = 16;
    inbound[6] = 50;
    inbound[7] = 64;
    std::memcpy(inbound + 8, kPeer, 16);
    std::memcpy(inbound + 24, kLocal, 16);
    constexpr BYTE rx[16] = {
        0x11, 0x22, 0x33, 0x44, 0, 0, 0, 1,
        'V', 'o', 'W', 'i', 'n', 'E', 'S', 'P'
    };
    std::memcpy(inbound + 40, rx, sizeof(rx));
    BYTE* sendBuffer = allocateSend(session, sizeof(inbound));
    if (!sendBuffer) {
        std::printf("WintunAllocateSendPacket failed: %lu\n", GetLastError());
        return false;
    }
    std::memcpy(sendBuffer, inbound, sizeof(inbound));
    sendPacket(session, sendBuffer);
    std::puts("Managed WinDivert peer exchanged exact synthetic ESP packets.");
    Sleep(500); // Keep the temporary adapter alive until the receive completes.
    return true;
}

int RunRouteSmoke(HMODULE library, Adapter adapter, const NET_LUID& luid,
                  NET_IFINDEX index, bool withWfp, bool withInbound,
                  bool rawEspInbound, bool npcapEspCapture,
                  bool npcapEspSend, const wchar_t* winDivertDll,
                  bool winDivertSend, bool managedWinDivertPeer) {
    const auto start = LoadExport<StartSession>(library, "WintunStartSession");
    const auto end = LoadExport<EndSession>(library, "WintunEndSession");
    const auto waitEvent = LoadExport<GetReadWaitEvent>(library, "WintunGetReadWaitEvent");
    const auto receive = LoadExport<ReceivePacket>(library, "WintunReceivePacket");
    const auto release = LoadExport<ReleaseReceivePacket>(library, "WintunReleaseReceivePacket");
    const auto allocateSend = LoadExport<AllocateSendPacket>(library, "WintunAllocateSendPacket");
    const auto sendPacket = LoadExport<SendPacket>(library, "WintunSendPacket");
    if (!start || !end || !waitEvent || !receive || !release ||
        ((withInbound || rawEspInbound || npcapEspCapture || npcapEspSend ||
          (winDivertDll && !winDivertSend) || managedWinDivertPeer) &&
         (!allocateSend || !sendPacket))) {
        std::puts("Wintun packet-session exports are unavailable.");
        return 2;
    }
    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 2;
    Session session = nullptr;
    SOCKET udp = INVALID_SOCKET;
    SOCKET rawEsp = INVALID_SOCKET;
    MIB_UNICASTIPADDRESS_ROW address{};
    MIB_IPFORWARD_ROW2 route{};
    void* reservation = nullptr;
    bool addressAdded = false, routeAdded = false;
    int result = 2;
    do {
        session = start(adapter, 0x400000);
        if (!session) {
            std::printf("WintunStartSession failed: %lu\n", GetLastError());
            break;
        }
        InitializeUnicastIpAddressEntry(&address);
        address.InterfaceLuid = luid;
        address.Address.Ipv6.sin6_family = AF_INET6;
        std::memcpy(address.Address.Ipv6.sin6_addr.u.Byte, kLocal, 16);
        address.OnLinkPrefixLength = 128;
        address.DadState = IpDadStatePreferred;
        address.ValidLifetime = 60;
        address.PreferredLifetime = 60;
        const DWORD addAddress = CreateUnicastIpAddressEntry(&address);
        std::printf("Temporary Wintun IPv6 /128 add: 0x%08lx\n", addAddress);
        if (addAddress != NO_ERROR) break;
        addressAdded = true;

        InitializeIpForwardEntry(&route);
        route.InterfaceLuid = luid;
        route.DestinationPrefix.Prefix.Ipv6.sin6_family = AF_INET6;
        std::memcpy(route.DestinationPrefix.Prefix.Ipv6.sin6_addr.u.Byte,
                    kPeer, 16);
        route.DestinationPrefix.PrefixLength = 128;
        route.NextHop.Ipv6.sin6_family = AF_INET6; // On-link, :: next hop.
        route.Metric = 1;
        route.Protocol = RouteProtocolNetMgmt;
        route.Loopback = FALSE;
        route.AutoconfigureAddress = FALSE;
        route.Publish = FALSE;
        route.Immortal = FALSE;
        route.ValidLifetime = 60;
        route.PreferredLifetime = 60;
        const DWORD addRoute = CreateIpForwardEntry2(&route);
        std::printf("Temporary peer /128 route add: 0x%08lx\n", addRoute);
        if (addRoute != NO_ERROR) break;
        routeAdded = true;

        // Address creation may begin DAD. Do not use a tentative source.
        bool addressReady = false;
        for (int i = 0; i < 30; ++i) {
            auto query = address;
            if (GetUnicastIpAddressEntry(&query) == NO_ERROR &&
                query.DadState == IpDadStatePreferred) {
                addressReady = true;
                break;
            }
            Sleep(100);
        }
        if (!addressReady) {
            std::puts("Temporary Wintun source address never became preferred.");
            break;
        }
        if (managedWinDivertPeer) {
            result = ExchangeWithManagedWinDivert(session, receive, release,
                waitEvent, allocateSend, sendPacket) ? 0 : 2;
            break;
        }
        if (winDivertDll) {
            result = winDivertSend
                ? (InjectSyntheticEspWithWinDivert(session, index, receive,
                    release, waitEvent, winDivertDll) ? 0 : 2)
                : (CaptureSyntheticEspWithWinDivert(session, index,
                    allocateSend, sendPacket, winDivertDll) ? 0 : 2);
            break;
        }
        if (npcapEspCapture || npcapEspSend) {
            result = CaptureSyntheticEspWithNpcap(session, luid,
                allocateSend, sendPacket, receive, release, waitEvent,
                npcapEspSend) ? 0 : 2;
            break;
        }
        if (rawEspInbound) {
            rawEsp = socket(AF_INET6, SOCK_RAW, IPPROTO_ESP);
            if (rawEsp == INVALID_SOCKET) {
                std::printf("Raw ESP socket failed: WSA=%d\n", WSAGetLastError());
                break;
            }
            sockaddr_in6 local{};
            local.sin6_family = AF_INET6;
            std::memcpy(local.sin6_addr.u.Byte, kLocal, 16);
            const DWORD timeoutMs = 2000;
            if (bind(rawEsp, reinterpret_cast<sockaddr*>(&local), sizeof(local)) == SOCKET_ERROR ||
                setsockopt(rawEsp, SOL_SOCKET, SO_RCVTIMEO,
                    reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs)) == SOCKET_ERROR) {
                std::printf("Raw ESP socket bind/timeout failed: WSA=%d\n", WSAGetLastError());
                break;
            }
            BYTE inbound[56]{};
            inbound[0] = 0x60; // IPv6, fixed lab source/destination, no extension headers.
            inbound[4] = 0;
            inbound[5] = 16;
            inbound[6] = 50;
            inbound[7] = 64;
            std::memcpy(inbound + 8, kPeer, 16);
            std::memcpy(inbound + 24, kLocal, 16);
            constexpr BYTE espPayload[16] = {
                0x11, 0x22, 0x33, 0x44, 0, 0, 0, 1,
                'V', 'o', 'W', 'i', 'n', 'E', 'S', 'P'
            }; // Synthetic, intentionally not a valid authenticated SA.
            std::memcpy(inbound + 40, espPayload, sizeof(espPayload));
            BYTE* sendBuffer = allocateSend(session, sizeof(inbound));
            if (!sendBuffer) {
                std::printf("WintunAllocateSendPacket failed: %lu\n", GetLastError());
                break;
            }
            std::memcpy(sendBuffer, inbound, sizeof(inbound));
            sendPacket(session, sendBuffer);
            BYTE received[512]{};
            sockaddr_in6 from{};
            int fromLength = sizeof(from);
            const int count = recvfrom(rawEsp, reinterpret_cast<char*>(received),
                sizeof(received), 0, reinterpret_cast<sockaddr*>(&from), &fromLength);
            bool markerFound = false;
            for (int i = 0; count >= 0 && i + 16 <= count; ++i)
                if (std::memcmp(received + i, espPayload, sizeof(espPayload)) == 0)
                    markerFound = true;
            std::printf("Non-loopback raw ESP inbound: bytes=%d; WSA=%d; payload-match=%s\n",
                count, count == SOCKET_ERROR ? WSAGetLastError() : 0,
                markerFound ? "yes" : "no");
            if (markerFound && std::memcmp(from.sin6_addr.u.Byte, kPeer, 16) == 0)
                result = 0;
            break;
        }
        udp = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
        if (udp == INVALID_SOCKET) {
            std::printf("UDP socket failed: %d\n", WSAGetLastError());
            break;
        }
        sockaddr_in6 source{}, destination{};
        source.sin6_family = AF_INET6;
        source.sin6_port = htons(60000);
        std::memcpy(source.sin6_addr.u.Byte, kLocal, 16);
        destination.sin6_family = AF_INET6;
        destination.sin6_port = htons(60010);
        std::memcpy(destination.sin6_addr.u.Byte, kPeer, 16);
        if (setsockopt(udp, IPPROTO_IPV6, IPV6_UNICAST_IF,
                reinterpret_cast<const char*>(&index), sizeof(index)) == SOCKET_ERROR ||
            bind(udp, reinterpret_cast<sockaddr*>(&source), sizeof(source)) == SOCKET_ERROR) {
            std::printf("Pinned UDP bind failed: %d\n", WSAGetLastError());
            break;
        }
        const auto sendAndCapture = [&](const char* marker, int markerLength,
                                        UINT32 expectedSpi,
                                        std::vector<BYTE>* captured = nullptr) {
            const int sent = sendto(udp, marker, markerLength, 0,
                reinterpret_cast<sockaddr*>(&destination), sizeof(destination));
            std::printf("UDP send to isolated peer /128: bytes=%d; WSA=%d\n",
                sent, sent == SOCKET_ERROR ? WSAGetLastError() : 0);
            if (sent != markerLength) return -1;

            const ULONGLONG until = GetTickCount64() + 5000;
            while (GetTickCount64() < until) {
                DWORD length = 0;
                BYTE* packet = receive(session, &length);
                if (!packet) {
                    const DWORD error = GetLastError();
                    if (error != ERROR_NO_MORE_ITEMS) {
                        std::printf("WintunReceivePacket failed: %lu\n", error);
                        break;
                    }
                    WaitForSingleObject(waitEvent(session), 100);
                    continue;
                }
                const bool matched = length >= 40 && packet[0] >> 4 == 6 &&
                    std::memcmp(packet + 8, kLocal, 16) == 0 &&
                    std::memcmp(packet + 24, kPeer, 16) == 0;
                int nextHeader = matched ? packet[6] : -1;
                if (matched) {
                    std::printf("Non-loopback Wintun IPv6 packet: length=%lu; next-header=%u\n",
                        length, packet[6]);
                    if (expectedSpi) {
                        UINT32 wireSpi = 0;
                        if (length >= 48 && packet[6] == 50) {
                            std::memcpy(&wireSpi, packet + 40, sizeof(wireSpi));
                            wireSpi = ntohl(wireSpi);
                        }
                        std::printf("ESP wire SPI=0x%08lx; expected=0x%08lx\n",
                            wireSpi, expectedSpi);
                        if (wireSpi != expectedSpi) nextHeader = -1;
                        for (DWORD offset = 40;
                             offset + static_cast<DWORD>(markerLength) <= length;
                             ++offset) {
                            if (std::memcmp(packet + offset, marker,
                                            markerLength) == 0) {
                                std::puts("ERROR: ESP packet exposed the test marker.");
                                nextHeader = -1;
                                break;
                            }
                        }
                    }
                }
                if (matched && nextHeader >= 0 && captured)
                    captured->assign(packet, packet + length);
                release(session, packet);
                if (matched) return nextHeader;
            }
            return -1;
        };
        constexpr char clearMarker[] = "vowin-wintun-route-smoke";
        if (sendAndCapture(clearMarker, sizeof(clearMarker), 0) != IPPROTO_UDP) break;
        if (withWfp) {
            UINT32 clientSpi = 0, serverSpi = 0;
            const DWORD reserve = VoWinImsReserveV6(kLocal, kPeer, index,
                60000, 60001, &reservation, &clientSpi, &serverSpi);
            std::printf("Temporary WFP reserve: 0x%08lx; SPIs=%lu,%lu\n",
                reserve, clientSpi, serverSpi);
            if (reserve != ERROR_SUCCESS || !reservation ||
                clientSpi < 256 || serverSpi < 256 || clientSpi == serverSpi) break;
            const UINT8 authKey[20]{};
            const UINT8 cipherKey[16]{};
            const UINT32 outboundSpi = 0xABCDEF02u;
            const DWORD activate = VoWinImsActivateV6(&reservation,
                0xABCDEF01u, outboundSpi, 1, 3,
                authKey, sizeof(authKey), cipherKey, sizeof(cipherKey));
            std::printf("Temporary WFP ESP activate: 0x%08lx\n", activate);
            if (activate != ERROR_SUCCESS) break;
            if (withInbound) destination.sin6_port = htons(60000);
            constexpr char protectedMarker[] = "vowin-wintun-wfp-esp";
            std::vector<BYTE> protectedPacket;
            if (sendAndCapture(protectedMarker, sizeof(protectedMarker),
                               outboundSpi, withInbound ? &protectedPacket : nullptr) != 50) break;
            std::puts("Windows WFP produced outbound ESP on the isolated non-loopback adapter.");
            if (withInbound) {
                const DWORD timeoutMs = 1500;
                if (setsockopt(udp, SOL_SOCKET, SO_RCVTIMEO,
                    reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs)) == SOCKET_ERROR) {
                    std::printf("UDP receive timeout setup failed: %d\n", WSAGetLastError());
                    break;
                }
                const auto injectAsPeer = [&](const std::vector<BYTE>& outbound,
                                               bool corruptIcv) {
                    if (outbound.size() < 60 || outbound[6] != 50) return false;
                    auto inbound = outbound;
                    std::memcpy(inbound.data() + 8, kPeer, 16);
                    std::memcpy(inbound.data() + 24, kLocal, 16);
                    if (!RewriteInboundSpiAndIcv(inbound, clientSpi)) return false;
                    if (corruptIcv) inbound.back() ^= 0x01;
                    BYTE* sendBuffer = allocateSend(session, static_cast<DWORD>(inbound.size()));
                    if (!sendBuffer) {
                        std::printf("WintunAllocateSendPacket failed: %lu\n", GetLastError());
                        return false;
                    }
                    std::memcpy(sendBuffer, inbound.data(), inbound.size());
                    sendPacket(session, sendBuffer);
                    std::printf("Injected isolated peer ESP: bytes=%zu; corrupted-ICV=%s\n",
                        inbound.size(), corruptIcv ? "yes" : "no");
                    return true;
                };
                const auto receiveMarker = [&](const char* marker, int markerLength) {
                    char buffer[256]{};
                    sockaddr_in6 from{};
                    int fromLength = sizeof(from);
                    const int received = recvfrom(udp, buffer, sizeof(buffer), 0,
                        reinterpret_cast<sockaddr*>(&from), &fromLength);
                    const bool matched = received == markerLength &&
                        std::memcmp(buffer, marker, markerLength) == 0 &&
                        from.sin6_port == htons(60000) &&
                        std::memcmp(from.sin6_addr.u.Byte, kPeer, 16) == 0;
                    std::printf("Windows inbound UDP: bytes=%d; WSA=%d; marker-match=%s\n",
                        received, received == SOCKET_ERROR ? WSAGetLastError() : 0,
                        matched ? "yes" : "no");
                    return matched;
                };
                if (!injectAsPeer(protectedPacket, false) ||
                    !receiveMarker(protectedMarker, sizeof(protectedMarker))) break;
                constexpr char secondMarker[] = "vowin-wintun-wfp-auth-negative";
                std::vector<BYTE> secondPacket;
                if (sendAndCapture(secondMarker, sizeof(secondMarker),
                                   outboundSpi, &secondPacket) != 50) break;
                if (!injectAsPeer(secondPacket, true)) break;
                char rejected[256]{};
                const int badReceive = recv(udp, rejected, sizeof(rejected), 0);
                const int badError = badReceive == SOCKET_ERROR ? WSAGetLastError() : 0;
                std::printf("Corrupted-ICV inbound receive: bytes=%d; WSA=%d\n",
                    badReceive, badError);
                if (badReceive != SOCKET_ERROR || badError != WSAETIMEDOUT) break;
                if (!injectAsPeer(secondPacket, false) ||
                    !receiveMarker(secondMarker, sizeof(secondMarker))) break;
                const int preReplayReceive = recv(udp, rejected, sizeof(rejected), 0);
                const int preReplayError = preReplayReceive == SOCKET_ERROR ? WSAGetLastError() : 0;
                std::printf("Pre-replay empty-queue check: bytes=%d; WSA=%d\n",
                    preReplayReceive, preReplayError);
                if (preReplayReceive != SOCKET_ERROR ||
                    preReplayError != WSAETIMEDOUT) break;
                if (!injectAsPeer(secondPacket, false)) break;
                const int replayReceive = recv(udp, rejected, sizeof(rejected), 0);
                const int replayError = replayReceive == SOCKET_ERROR ? WSAGetLastError() : 0;
                std::printf("Replayed inbound ESP receive: bytes=%d; WSA=%d\n",
                    replayReceive, replayError);
                if (replayReceive != SOCKET_ERROR || replayError != WSAETIMEDOUT) {
                    std::puts("SECURITY GAP: this manual-SA path delivered a replayed ESP packet.");
                    break;
                }
                std::puts("Windows WFP accepted valid inbound ESP and rejected corrupted-tag and replayed packets.");
            }
        }
        result = 0;
    } while (false);

    if (udp != INVALID_SOCKET) closesocket(udp);
    if (rawEsp != INVALID_SOCKET) closesocket(rawEsp);
    for (int attempt = 0; reservation && attempt < 3; ++attempt) {
        const DWORD cleanup = VoWinImsRelease(&reservation);
        std::printf("WFP reservation cleanup %d: 0x%08lx; residual=%s\n",
            attempt + 1, cleanup, reservation ? "yes" : "no");
        if (cleanup != ERROR_SUCCESS) result = 2;
    }
    if (reservation) result = 2;
    if (routeAdded) {
        const DWORD cleanup = DeleteIpForwardEntry2(&route);
        std::printf("Peer /128 route cleanup: 0x%08lx\n", cleanup);
        if (cleanup != NO_ERROR) result = 2;
    }
    if (addressAdded) {
        const DWORD cleanup = DeleteUnicastIpAddressEntry(&address);
        std::printf("Wintun address cleanup: 0x%08lx\n", cleanup);
        if (cleanup != NO_ERROR) result = 2;
    }
    if (session) end(session);
    WSACleanup();
    return result;
}
}

int wmain(int argc, wchar_t** argv) {
    const bool openOnly = argc == 4 && std::wcscmp(argv[1], L"--open-only") == 0;
    const bool createClose = argc == 3 && std::wcscmp(argv[1], L"--create-close") == 0;
    const bool routeSmoke = argc == 3 && std::wcscmp(argv[1], L"--route-smoke") == 0;
    const bool wfpPacket = argc == 3 && std::wcscmp(argv[1], L"--wfp-packet") == 0;
    const bool wfpBidirectional = argc == 3 && std::wcscmp(argv[1], L"--wfp-bidirectional") == 0;
    const bool rawEspInbound = argc == 3 && std::wcscmp(argv[1], L"--raw-esp-inbound") == 0;
    const bool npcapEspCapture = argc == 3 && std::wcscmp(argv[1], L"--npcap-esp-capture") == 0;
    const bool npcapEspSend = argc == 3 && std::wcscmp(argv[1], L"--npcap-esp-send") == 0;
    const bool winDivertSniff = argc == 4 && std::wcscmp(argv[1], L"--windivert-sniff") == 0;
    const bool winDivertSend = argc == 4 && std::wcscmp(argv[1], L"--windivert-send") == 0;
    const bool managedWinDivertPeer = argc == 3 && std::wcscmp(argv[1], L"--managed-windivert-peer") == 0;
    if (!openOnly && !createClose && !routeSmoke && !wfpPacket &&
        !wfpBidirectional && !rawEspInbound && !npcapEspCapture &&
        !npcapEspSend && !winDivertSniff && !winDivertSend && !managedWinDivertPeer) {
        std::puts("Usage: VoWinWintunImsLab.exe --open-only <absolute-wintun.dll> <existing-adapter-name> | --create-close <absolute-wintun.dll> | --route-smoke <absolute-wintun.dll> | --wfp-packet <absolute-wintun.dll> | --wfp-bidirectional <absolute-wintun.dll> | --raw-esp-inbound <absolute-wintun.dll> | --npcap-esp-capture <absolute-wintun.dll> | --npcap-esp-send <absolute-wintun.dll> | --windivert-sniff <absolute-wintun.dll> <absolute-WinDivert.dll> | --windivert-send <absolute-wintun.dll> <absolute-WinDivert.dll> | --managed-windivert-peer <absolute-wintun.dll>");
        return 1;
    }
    const DWORD attributes = GetFileAttributesW(argv[2]);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) ||
        std::wcslen(argv[2]) < 3 ||
        (argv[2][1] != L':' || argv[2][2] != L'\\')) {
        std::puts("An existing absolute DLL path is required.");
        return 2;
    }
    HMODULE library = LoadLibraryExW(argv[2], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
    if (!library) {
        std::printf("Wintun DLL load failed: %lu\n", GetLastError());
        return 2;
    }
    const auto open = LoadExport<OpenAdapter>(library, "WintunOpenAdapter");
    const auto create = LoadExport<CreateAdapter>(library, "WintunCreateAdapter");
    const auto close = LoadExport<CloseAdapter>(library, "WintunCloseAdapter");
    const auto getLuid = LoadExport<GetAdapterLuid>(library, "WintunGetAdapterLUID");
    if (!open || !create || !close || !getLuid) {
        std::puts("The Wintun DLL lacks required adapter discovery exports.");
        FreeLibrary(library);
        return 2;
    }
    if (winDivertSniff || winDivertSend) {
        const DWORD divertAttributes = GetFileAttributesW(argv[3]);
        if (divertAttributes == INVALID_FILE_ATTRIBUTES ||
            (divertAttributes & FILE_ATTRIBUTE_DIRECTORY) ||
            std::wcslen(argv[3]) < 3 || argv[3][1] != L':' ||
            argv[3][2] != L'\\') {
            std::puts("An existing absolute WinDivert DLL path is required.");
            FreeLibrary(library);
            return 2;
        }
    }
    Adapter adapter = createClose || routeSmoke || wfpPacket || wfpBidirectional ||
                      rawEspInbound || npcapEspCapture || npcapEspSend || winDivertSniff || winDivertSend || managedWinDivertPeer
        ? create(L"VoWin IMS Lab", L"VoWin", nullptr)
        : open(argv[3]);
    if (!adapter) {
        std::printf("Wintun adapter %s failed: %lu\n",
            createClose || routeSmoke || wfpPacket || wfpBidirectional || rawEspInbound || npcapEspCapture || npcapEspSend || winDivertSniff || winDivertSend || managedWinDivertPeer ? "create" : "open", GetLastError());
        FreeLibrary(library);
        return 2;
    }
    NET_LUID luid{};
    getLuid(adapter, &luid);
    NET_IFINDEX index = 0;
    const DWORD status = ConvertInterfaceLuidToIndex(&luid, &index);
    std::printf("Wintun adapter %s: LUID=0x%llx; index=%lu; lookup=0x%08lx\n",
        createClose || routeSmoke || wfpPacket || wfpBidirectional || rawEspInbound || npcapEspCapture || npcapEspSend || winDivertSniff || winDivertSend || managedWinDivertPeer ? "created" : "opened",
        static_cast<unsigned long long>(luid.Value), index, status);
    int result = status == ERROR_SUCCESS && index != 0 ? 0 : 2;
    if (result == 0 && (routeSmoke || wfpPacket || wfpBidirectional ||
                        rawEspInbound || npcapEspCapture || npcapEspSend ||
                        winDivertSniff || winDivertSend || managedWinDivertPeer))
        result = RunRouteSmoke(library, adapter, luid, index,
                               wfpPacket || wfpBidirectional, wfpBidirectional,
                               rawEspInbound, npcapEspCapture, npcapEspSend,
                               winDivertSniff || winDivertSend ? argv[3] : nullptr,
                               winDivertSend, managedWinDivertPeer);
    close(adapter);
    if (createClose || routeSmoke || wfpPacket || wfpBidirectional ||
        rawEspInbound || npcapEspCapture || npcapEspSend || winDivertSniff || winDivertSend || managedWinDivertPeer)
        std::puts("WintunCloseAdapter returned for newly created adapter.");
    FreeLibrary(library);
    return result;
}
