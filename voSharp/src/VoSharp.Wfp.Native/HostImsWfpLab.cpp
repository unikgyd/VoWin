// Fixed, non-carrier loopback and isolated Wintun WFP exercises.
#include <winsock2.h>
#include <ws2tcpip.h>
#include "HostImsWfp.h"
#include <fwpmu.h>
#include <iphlpapi.h>
#include <rpc.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <vector>

namespace {
constexpr wchar_t kFilterName[] = L"VoWin experimental IMS protected SIP port";
constexpr UINT8 kPacketPeer[16] = {
    0xfd, 0x7a, 0x6f, 0x57, 0x49, 0x4d, 0x53, 0x00,
    0, 0, 0, 0, 0, 0, 0, 2
}; // fd7a:6f57:494d:5300::2/128, active-store lab alias only.
constexpr UINT8 kWintunLocal[16] = {
    0xfd, 0x7a, 0x6f, 0x57, 0x49, 0x4d, 0x53, 0x01,
    0, 0, 0, 0, 0, 0, 0, 1
};
constexpr UINT8 kWintunPeer[16] = {
    0xfd, 0x7a, 0x6f, 0x57, 0x49, 0x4d, 0x53, 0x01,
    0, 0, 0, 0, 0, 0, 0, 2
};

bool IsLabAddressPair(const UINT8* local, const UINT8* remote) {
    UINT8 loopback[16]{};
    loopback[15] = 1;
    UINT8 originalPeer[16]{};
    originalPeer[15] = 2;
    const bool original = std::memcmp(local, loopback, 16) == 0 &&
        std::memcmp(remote, originalPeer, 16) == 0;
    const bool packetForward = std::memcmp(local, loopback, 16) == 0 &&
        std::memcmp(remote, kPacketPeer, 16) == 0;
    const bool packetReverse = std::memcmp(local, kPacketPeer, 16) == 0 &&
        std::memcmp(remote, loopback, 16) == 0;
    const bool wintunForward = std::memcmp(local, kWintunLocal, 16) == 0 &&
        std::memcmp(remote, kWintunPeer, 16) == 0;
    return original || packetForward || packetReverse || wintunForward;
}

bool IsLabFilter(const FWPM_FILTER0& filter) {
    if (!filter.displayData.name ||
        std::wcscmp(filter.displayData.name, kFilterName) != 0) return false;
    const UINT8* localAddress = nullptr;
    const UINT8* remoteAddress = nullptr;
    bool port = false;
    for (UINT32 i = 0; i < filter.numFilterConditions; ++i) {
        const auto& condition = filter.filterCondition[i];
        const auto& value = condition.conditionValue;
        if (IsEqualGUID(condition.fieldKey, FWPM_CONDITION_IP_LOCAL_ADDRESS) &&
            value.type == FWP_BYTE_ARRAY16_TYPE && value.byteArray16) {
            localAddress = value.byteArray16->byteArray16;
        } else if (IsEqualGUID(condition.fieldKey, FWPM_CONDITION_IP_REMOTE_ADDRESS) &&
                   value.type == FWP_BYTE_ARRAY16_TYPE && value.byteArray16) {
            remoteAddress = value.byteArray16->byteArray16;
        } else if (IsEqualGUID(condition.fieldKey, FWPM_CONDITION_IP_LOCAL_PORT) &&
                   value.type == FWP_UINT16) {
            port = value.uint16 == 60000 || value.uint16 == 60001 ||
                value.uint16 == 60010 || value.uint16 == 60011;
        }
    }
    return localAddress && remoteAddress &&
        IsLabAddressPair(localAddress, remoteAddress) && port;
}

bool IsLabSa(const IPSEC_SA_DETAILS1* details) {
    if (!details || details->traffic.ipVersion != FWP_IP_VERSION_V6) return false;
    if (details->traffic.localPort != 60000 &&
        details->traffic.localPort != 60001 &&
        details->traffic.localPort != 60010 &&
        details->traffic.localPort != 60011) return false;
    return IsLabAddressPair(details->traffic.localV6Address,
                            details->traffic.remoteV6Address);
}

DWORD AuditResidualObjects() {
    HANDLE engine = nullptr;
    DWORD status = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr,
                                   nullptr, &engine);
    if (status != ERROR_SUCCESS) return status;
    UINT32 filtersFound = 0, contextsFound = 0;
    HANDLE filterEnum = nullptr;
    status = FwpmFilterCreateEnumHandle0(engine, nullptr, &filterEnum);
    if (status == ERROR_SUCCESS) {
        for (;;) {
            FWPM_FILTER0** entries = nullptr;
            UINT32 count = 0;
            status = FwpmFilterEnum0(engine, filterEnum, 100, &entries, &count);
            if (status == ERROR_SUCCESS) {
                for (UINT32 i = 0; i < count; ++i)
                    if (IsLabFilter(*entries[i])) ++filtersFound;
            }
            if (entries) FwpmFreeMemory0(reinterpret_cast<void**>(&entries));
            if (status != ERROR_SUCCESS || count == 0) break;
        }
        const DWORD close = FwpmFilterDestroyEnumHandle0(engine, filterEnum);
        if (status == ERROR_SUCCESS) status = close;
    }
    HANDLE saEnum = nullptr;
    if (status == ERROR_SUCCESS)
        status = IPsecSaContextCreateEnumHandle0(engine, nullptr, &saEnum);
    if (status == ERROR_SUCCESS) {
        for (;;) {
            IPSEC_SA_CONTEXT1** entries = nullptr;
            UINT32 count = 0;
            status = IPsecSaContextEnum1(engine, saEnum, 100, &entries, &count);
            if (status == ERROR_SUCCESS) {
                for (UINT32 i = 0; i < count; ++i)
                    if (IsLabSa(entries[i]->inboundSa) ||
                        IsLabSa(entries[i]->outboundSa)) ++contextsFound;
            }
            if (entries) FwpmFreeMemory0(reinterpret_cast<void**>(&entries));
            if (status != ERROR_SUCCESS || count == 0) break;
        }
        const DWORD close = IPsecSaContextDestroyEnumHandle0(engine, saEnum);
        if (status == ERROR_SUCCESS) status = close;
    }
    const DWORD close = FwpmEngineClose0(engine);
    if (status == ERROR_SUCCESS) status = close;
    std::printf("Lab residual filters=%u SA contexts=%u; audit=0x%08lx\n",
        filtersFound, contextsFound, status);
    return status == ERROR_SUCCESS && (filtersFound || contextsFound) ?
        ERROR_INVALID_DATA : status;
}

// Recovery tool for the synthetic loopback exercise only. Never matches
// carrier addresses; require a populated SA selector before deleting by ID.
DWORD CleanupLabSaContexts() {
    HANDLE engine = nullptr, enumeration = nullptr;
    DWORD status = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr,
                                   nullptr, &engine);
    std::vector<UINT64> ids;
    if (status == ERROR_SUCCESS)
        status = IPsecSaContextCreateEnumHandle0(engine, nullptr, &enumeration);
    if (status == ERROR_SUCCESS) {
        for (;;) {
            IPSEC_SA_CONTEXT1** entries = nullptr;
            UINT32 count = 0;
            status = IPsecSaContextEnum1(engine, enumeration, 100,
                                         &entries, &count);
            if (status == ERROR_SUCCESS) {
                for (UINT32 i = 0; i < count; ++i)
                    if (IsLabSa(entries[i]->inboundSa) ||
                        IsLabSa(entries[i]->outboundSa))
                        ids.push_back(entries[i]->saContextId);
            }
            if (entries) FwpmFreeMemory0(reinterpret_cast<void**>(&entries));
            if (status != ERROR_SUCCESS || count == 0) break;
        }
    }
    if (enumeration) {
        const DWORD close = IPsecSaContextDestroyEnumHandle0(engine,
                                                              enumeration);
        if (status == ERROR_SUCCESS) status = close;
    }
    if (status == ERROR_SUCCESS) {
        for (const UINT64 id : ids) {
            const DWORD deleted = IPsecSaContextDeleteById0(engine, id);
            std::printf("Loopback SA context %llu cleanup: 0x%08lx\n",
                        id, deleted);
            if (deleted != ERROR_SUCCESS && status == ERROR_SUCCESS)
                status = deleted;
        }
    }
    if (engine) {
        const DWORD close = FwpmEngineClose0(engine);
        if (status == ERROR_SUCCESS) status = close;
    }
    const DWORD audit = AuditResidualObjects();
    return status == ERROR_SUCCESS ? audit : status;
}

int AbandonActivatedLabContext() {
    UINT8 local[16]{}, remote[16]{};
    local[15] = 1;
    remote[15] = 2;
    void* reservation = nullptr;
    UINT32 clientSpi = 0, serverSpi = 0;
    DWORD status = VoWinImsReserveV6(local, remote, 1, 60000, 60001,
        &reservation, &clientSpi, &serverSpi);
    if (status == ERROR_SUCCESS) {
        const UINT8 authKey[20]{};
        const UINT8 cipherKey[16]{};
        status = VoWinImsActivateV6(&reservation, 0xABCDEF01u, 0xABCDEF02u,
                                     1, 3, authKey, 20, cipherKey, 16);
    }
    std::printf("Activated loopback before forced process exit: 0x%08lx\n",
                status);
    if (status == ERROR_SUCCESS) {
        // AuditResidualObjects returns ERROR_INVALID_DATA for live lab objects;
        // the printed counts establish that the post-exit matcher is sensitive.
        AuditResidualObjects();
    }
    std::fflush(stdout);
    if (status != ERROR_SUCCESS) {
        if (reservation) VoWinImsRelease(&reservation);
        return 2;
    }
    std::_Exit(0); // No VoWinImsRelease: simulate abrupt owner termination.
}

// Probe whether short-lived filters can live in a dynamic session while the
// manually managed SA context uses a separate, non-dynamic session. The
// public backend does not use this arrangement until the probe is proven.
int RunDynamicFilterProbe(bool manualSpi = false) {
    FWP_BYTE_ARRAY16 local{}, remote{};
    local.byteArray16[15] = 1;
    remote.byteArray16[15] = 2;
    NET_LUID luid{};
    DWORD status = ConvertInterfaceIndexToLuid(1, &luid);
    if (status != ERROR_SUCCESS) return 2;

    FWPM_SESSION0 session{};
    session.flags = FWPM_SESSION_FLAG_DYNAMIC;
    HANDLE dynamicEngine = nullptr, staticEngine = nullptr;
    UINT64 inboundFilter = 0, outboundFilter = 0, context = 0;
    status = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, &session,
                             &dynamicEngine);
    if (status == ERROR_SUCCESS)
        status = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, nullptr,
                                 &staticEngine);
    auto addFilter = [&](bool inbound, UINT64* id) -> DWORD {
        FWPM_FILTER_CONDITION0 conditions[6]{};
        conditions[0].fieldKey = FWPM_CONDITION_IP_LOCAL_ADDRESS;
        conditions[0].matchType = FWP_MATCH_EQUAL;
        conditions[0].conditionValue.type = FWP_BYTE_ARRAY16_TYPE;
        conditions[0].conditionValue.byteArray16 = &local;
        conditions[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
        conditions[1].matchType = FWP_MATCH_EQUAL;
        conditions[1].conditionValue.type = FWP_BYTE_ARRAY16_TYPE;
        conditions[1].conditionValue.byteArray16 = &remote;
        conditions[2].fieldKey = FWPM_CONDITION_IP_PROTOCOL;
        conditions[2].matchType = FWP_MATCH_EQUAL;
        conditions[2].conditionValue.type = FWP_UINT8;
        conditions[2].conditionValue.uint8 = IPPROTO_UDP;
        conditions[3].fieldKey = FWPM_CONDITION_IP_LOCAL_PORT;
        conditions[3].matchType = FWP_MATCH_EQUAL;
        conditions[3].conditionValue.type = FWP_UINT16;
        conditions[3].conditionValue.uint16 = 60000;
        conditions[4].fieldKey = FWPM_CONDITION_IP_LOCAL_INTERFACE;
        conditions[4].matchType = FWP_MATCH_EQUAL;
        conditions[4].conditionValue.type = FWP_UINT64;
        conditions[4].conditionValue.uint64 = &luid.Value;
        conditions[5].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT;
        conditions[5].matchType = FWP_MATCH_EQUAL;
        conditions[5].conditionValue.type = FWP_UINT16;
        conditions[5].conditionValue.uint16 = 60010;
        FWPM_FILTER0 filter{};
        filter.displayData.name = const_cast<PWSTR>(kFilterName);
        filter.layerKey = inbound ? FWPM_LAYER_INBOUND_TRANSPORT_V6 :
                                    FWPM_LAYER_OUTBOUND_TRANSPORT_V6;
        filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
        filter.action.calloutKey = inbound ?
            FWPM_CALLOUT_IPSEC_INBOUND_TRANSPORT_V6 :
            FWPM_CALLOUT_IPSEC_OUTBOUND_TRANSPORT_V6;
        filter.numFilterConditions = manualSpi ? ARRAYSIZE(conditions) : 5;
        filter.filterCondition = conditions;
        return FwpmFilterAdd0(dynamicEngine, &filter, nullptr, id);
    };
    if (status == ERROR_SUCCESS) status = addFilter(true, &inboundFilter);
    if (status == ERROR_SUCCESS) status = addFilter(false, &outboundFilter);
    if (status == ERROR_SUCCESS) {
        IPSEC_TRAFFIC1 traffic{};
        traffic.ipVersion = FWP_IP_VERSION_V6;
        std::memcpy(traffic.localV6Address, local.byteArray16, 16);
        std::memcpy(traffic.remoteV6Address, remote.byteArray16, 16);
        traffic.trafficType = IPSEC_TRAFFIC_TYPE_TRANSPORT;
        traffic.ipsecFilterId = outboundFilter;
        traffic.localPort = 60000;
        if (manualSpi) traffic.remotePort = 60010;
        traffic.ipProtocol = IPPROTO_UDP;
        traffic.localIfLuid = luid.Value;
        status = IPsecSaContextCreate1(staticEngine, &traffic, nullptr,
                                       nullptr, &context);
        std::printf("Cross-session SA context create: 0x%08lx\n", status);
        if (status == ERROR_SUCCESS && !manualSpi) {
            IPSEC_GETSPI1 getSpi{};
            getSpi.inboundIpsecTraffic = traffic;
            getSpi.inboundIpsecTraffic.ipsecFilterId = inboundFilter;
            getSpi.ipVersion = FWP_IP_VERSION_V6;
            IPSEC_SA_SPI spi = 0;
            status = IPsecSaContextGetSpi1(staticEngine, context, &getSpi,
                                           &spi);
            std::printf("Cross-session inbound SPI: 0x%08lx; SPI=%lu\n",
                        status, spi);
        }
        if (status == ERROR_SUCCESS && manualSpi) {
            UINT8 authKey[20]{};
            IPSEC_SA_AUTH_INFORMATION0 auth{};
            auth.authTransform.authTransformId = IPSEC_AUTH_TRANSFORM_ID_HMAC_SHA_1_96;
            auth.authKey.size = sizeof(authKey);
            auth.authKey.data = authKey;
            IPSEC_SA0 sa{};
            sa.spi = 0x12345678u; // Lab-only selected inbound SPI, no GetSpi call.
            sa.saTransformType = IPSEC_TRANSFORM_ESP_AUTH;
            sa.espAuthInformation = &auth;
            IPSEC_SA_BUNDLE1 bundle{};
            bundle.numSAs = 1;
            bundle.saList = &sa;
            bundle.ipVersion = FWP_IP_VERSION_V6;
            bundle.lifetime.lifetimeSeconds = 3600;
            status = IPsecSaContextAddInbound1(staticEngine, context, &bundle);
            std::printf("Manual SPI inbound install without GetSpi: 0x%08lx\n", status);
            if (status == ERROR_SUCCESS) {
                sa.spi = 0xABCDEF01u; // Synthetic peer-owned outbound SPI.
                status = IPsecSaContextAddOutbound1(staticEngine, context, &bundle);
                std::printf("Manual SPI outbound install: 0x%08lx\n", status);
            }
        }
    }
    const DWORD probe = status;
    if (context) {
        const DWORD cleanup = IPsecSaContextDeleteById0(staticEngine, context);
        std::printf("Cross-session SA cleanup: 0x%08lx\n", cleanup);
        if (cleanup != ERROR_SUCCESS) status = cleanup;
    }
    if (staticEngine) {
        const DWORD close = FwpmEngineClose0(staticEngine);
        if (status == ERROR_SUCCESS) status = close;
    }
    if (dynamicEngine) {
        const DWORD close = FwpmEngineClose0(dynamicEngine);
        std::printf("Dynamic-session close: 0x%08lx\n", close);
        if (status == ERROR_SUCCESS) status = close;
    }
    const DWORD audit = AuditResidualObjects();
    std::printf("%s probe result: 0x%08lx; overall=0x%08lx\n",
                manualSpi ? "Manual-SPI remote-port" : "Dynamic-filter",
                probe, status);
    return status == ERROR_SUCCESS && audit == ERROR_SUCCESS ? 0 : 2;
}

int RunScenario(const char* label, bool activate, UINT32 authAlgorithm,
                UINT32 cipherAlgorithm, UINT32 authLength, UINT32 cipherLength) {
    // The local machine owns ::1 but not ::2. No carrier or modem IP is used.
    UINT8 local[16]{};
    UINT8 remote[16]{};
    local[15] = 1;
    remote[15] = 2;
    void* reservation = nullptr;
    UINT32 clientSpi = 0, serverSpi = 0;
    const DWORD reserve = VoWinImsReserveV6(local, remote, 1, 60000, 60001,
        &reservation, &clientSpi, &serverSpi);
    std::printf("%s reservation: 0x%08lx; handle=%s; SPIs=%lu,%lu\n",
        label, reserve, reservation ? "present" : "null", clientSpi, serverSpi);

    DWORD activation = ERROR_SUCCESS;
    if (reserve == ERROR_SUCCESS && activate) {
        const UINT8 authKey[20]{};
        const UINT8 cipherKey[24]{};
        activation = VoWinImsActivateV6(&reservation, 0xABCDEF01u,
            0xABCDEF02u, authAlgorithm, cipherAlgorithm,
            authKey, authLength, cipherLength ? cipherKey : nullptr,
            cipherLength);
        std::printf("%s SA activation: 0x%08lx\n", label, activation);
    }

    DWORD release = ERROR_SUCCESS;
    for (int attempt = 0; reservation && attempt < 3; ++attempt) {
        release = VoWinImsRelease(&reservation);
        std::printf("%s cleanup attempt %d: 0x%08lx; handle=%s\n", label,
                    attempt + 1, release, reservation ? "present" : "null");
    }
    if (reservation) {
        std::puts("WARNING: WFP cleanup incomplete; inspect the loopback-only filters.");
        return 3;
    }
    const DWORD audit = AuditResidualObjects();
    return reserve == ERROR_SUCCESS && activation == ERROR_SUCCESS &&
        release == ERROR_SUCCESS && audit == ERROR_SUCCESS ? 0 : 2;
}

// Requires a temporary fd7a:6f57:494d:5300::2/128 address on Windows
// loopback. Two independent
// manual-SA reservations model UE and P-CSCF on the same Windows host. A
// mismatched peer key must drop the datagram; matching keys must deliver it.
// This is deliberately separate from the default lab matrix and carrier PPP.
int RunPacketRoundTrip(bool wrongPeerKey) {
    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 2;
    UINT8 ue[16]{}, peer[16]{};
    ue[15] = 1;
    std::memcpy(peer, kPacketPeer, 16);
    void* ueReservation = nullptr;
    void* peerReservation = nullptr;
    SOCKET sender = INVALID_SOCKET, receiver = INVALID_SOCKET;
    UINT32 ueClientSpi = 0, ueServerSpi = 0;
    UINT32 peerClientSpi = 0, peerServerSpi = 0;
    DWORD status = VoWinImsReserveV6(ue, peer, 1, 60000, 60001,
        &ueReservation, &ueClientSpi, &ueServerSpi);
    if (status == ERROR_SUCCESS)
        status = VoWinImsReserveV6(peer, ue, 1, 60010, 60011,
            &peerReservation, &peerClientSpi, &peerServerSpi);
    UINT8 ueAuth[20]{};
    UINT8 peerAuth[20]{};
    UINT8 cipher[16]{};
    if (wrongPeerKey) peerAuth[0] = 1;
    if (status == ERROR_SUCCESS)
        status = VoWinImsActivateV6(&ueReservation, peerServerSpi,
            peerClientSpi, 1, 3, ueAuth, 20, cipher, 16);
    if (status == ERROR_SUCCESS)
        status = VoWinImsActivateV6(&peerReservation, ueServerSpi,
            ueClientSpi, 1, 3, peerAuth, 20, cipher, 16);
    if (status == ERROR_SUCCESS) {
        sender = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
        receiver = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
        if (sender == INVALID_SOCKET || receiver == INVALID_SOCKET)
            status = ERROR_OPEN_FAILED;
    }
    sockaddr_in6 source{}, destination{};
    source.sin6_family = AF_INET6;
    source.sin6_port = htons(60000);
    source.sin6_addr.u.Byte[15] = 1;
    destination.sin6_family = AF_INET6;
    destination.sin6_port = htons(60010);
    std::memcpy(destination.sin6_addr.u.Byte, peer, 16);
    if (status == ERROR_SUCCESS &&
        (bind(sender, reinterpret_cast<sockaddr*>(&source), sizeof(source)) == SOCKET_ERROR ||
         bind(receiver, reinterpret_cast<sockaddr*>(&destination), sizeof(destination)) == SOCKET_ERROR))
        status = ERROR_ADDRESS_NOT_ASSOCIATED;
    DWORD timeoutMs = 1500;
    if (status == ERROR_SUCCESS && setsockopt(receiver, SOL_SOCKET, SO_RCVTIMEO,
        reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs)) == SOCKET_ERROR)
        status = ERROR_INVALID_PARAMETER;
    constexpr char payload[] = "vowin-native-ipsec-loopback";
    if (status == ERROR_SUCCESS && sendto(sender, payload, sizeof(payload), 0,
        reinterpret_cast<sockaddr*>(&destination), sizeof(destination)) == SOCKET_ERROR)
        status = ERROR_WRITE_FAULT;
    char received[sizeof(payload)]{};
    int count = SOCKET_ERROR;
    int socketError = 0;
    if (status == ERROR_SUCCESS) {
        count = recvfrom(receiver, received, sizeof(received), 0, nullptr, nullptr);
        if (count == SOCKET_ERROR) socketError = WSAGetLastError();
        const bool delivered = count == sizeof(payload) &&
            std::memcmp(received, payload, sizeof(payload)) == 0;
        const bool expected = wrongPeerKey ?
            count == SOCKET_ERROR && socketError == WSAETIMEDOUT : delivered;
        std::printf("Windows loopback ESP packet: peer-key=%s; received=%d; socket-error=%d; expected=%s\n",
            wrongPeerKey ? "wrong" : "matching", count, socketError,
            expected ? "yes" : "no");
        if (!expected) status = ERROR_INVALID_DATA;
    }
    if (sender != INVALID_SOCKET) closesocket(sender);
    if (receiver != INVALID_SOCKET) closesocket(receiver);
    if (peerReservation) {
        const DWORD cleanup = VoWinImsRelease(&peerReservation);
        if (status == ERROR_SUCCESS) status = cleanup;
    }
    if (ueReservation) {
        const DWORD cleanup = VoWinImsRelease(&ueReservation);
        if (status == ERROR_SUCCESS) status = cleanup;
    }
    WSACleanup();
    const DWORD audit = AuditResidualObjects();
    if (status == ERROR_SUCCESS) status = audit;
    return status == ERROR_SUCCESS ? 0 : 2;
}

// Capability check for a Windows-native userspace ESP alternative to WFP.
// No real SA, SIM material, modem, or carrier endpoint is involved. A raw
// socket may receive its own loopback datagram; that does not prove an IMS
// peer or a non-loopback bearer works.
int RunRawEspSocketProbe() {
    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 2;
    SOCKET esp = socket(AF_INET6, SOCK_RAW, IPPROTO_ESP);
    if (esp == INVALID_SOCKET) {
        std::printf("IPv6 raw ESP socket creation failed: WSA=%d\n", WSAGetLastError());
        WSACleanup();
        return 2;
    }
    sockaddr_in6 loopback{};
    loopback.sin6_family = AF_INET6;
    loopback.sin6_addr.u.Byte[15] = 1;
    DWORD timeoutMs = 1500;
    int socketError = 0;
    if (setsockopt(esp, SOL_SOCKET, SO_RCVTIMEO,
            reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs)) == SOCKET_ERROR ||
        bind(esp, reinterpret_cast<sockaddr*>(&loopback), sizeof(loopback)) == SOCKET_ERROR)
        socketError = WSAGetLastError();
    // Synthetic SPI + sequence + 16 bytes. It is intentionally not valid ESP
    // for a negotiated association; this only tests raw protocol-50 I/O.
    UINT8 packet[24]{};
    packet[0] = 0x12; packet[1] = 0x34; packet[2] = 0x56; packet[3] = 0x78;
    packet[7] = 1;
    for (int i = 8; i < 24; ++i) packet[i] = static_cast<UINT8>(i);
    if (socketError == 0 && sendto(esp, reinterpret_cast<const char*>(packet),
            sizeof(packet), 0, reinterpret_cast<sockaddr*>(&loopback),
            sizeof(loopback)) == SOCKET_ERROR)
        socketError = WSAGetLastError();
    UINT8 received[128]{};
    int count = SOCKET_ERROR;
    if (socketError == 0) {
        count = recvfrom(esp, reinterpret_cast<char*>(received),
                         sizeof(received), 0, nullptr, nullptr);
        if (count == SOCKET_ERROR) socketError = WSAGetLastError();
    }
    const bool matched = count == sizeof(packet) &&
        std::memcmp(received, packet, sizeof(packet)) == 0;
    std::printf("Windows IPv6 raw ESP loopback: received=%d; WSA=%d; payload-match=%s\n",
        count, socketError, matched ? "yes" : "no");
    closesocket(esp);
    WSACleanup();
    return matched ? 0 : 2;
}
} // namespace

int main(int argc, char** argv) {
    const bool activate = argc == 2 && std::strcmp(argv[1], "--activate") == 0;
    const bool auditOnly = argc == 2 && std::strcmp(argv[1], "--audit-only") == 0;
    const bool matrix = argc == 2 && std::strcmp(argv[1], "--matrix") == 0;
    const bool dynamicProbe = argc == 2 &&
        std::strcmp(argv[1], "--dynamic-filter-probe") == 0;
    const bool manualSpiProbe = argc == 2 &&
        std::strcmp(argv[1], "--manual-spi-probe") == 0;
    const bool abandon = argc == 2 &&
        std::strcmp(argv[1], "--abandon-activated") == 0;
    const bool cleanup = argc == 2 &&
        std::strcmp(argv[1], "--cleanup-loopback") == 0;
    const bool packetVerify = argc == 2 &&
        std::strcmp(argv[1], "--packet-verify") == 0;
    const bool rawEspProbe = argc == 2 &&
        std::strcmp(argv[1], "--raw-esp-probe") == 0;
    if (argc > 2 || (argc == 2 && !activate && !auditOnly && !matrix &&
                                  !dynamicProbe && !manualSpiProbe &&
                                  !abandon && !cleanup &&
                                  !packetVerify && !rawEspProbe)) {
        std::puts("Usage: VoWinHostImsWfpLab.exe [--activate|--matrix|--audit-only|--dynamic-filter-probe|--manual-spi-probe|--abandon-activated|--cleanup-loopback|--packet-verify|--raw-esp-probe]");
        return 1;
    }
    if (auditOnly) return AuditResidualObjects() == ERROR_SUCCESS ? 0 : 4;
    if (dynamicProbe) return RunDynamicFilterProbe();
    if (manualSpiProbe) return RunDynamicFilterProbe(true);
    if (rawEspProbe) return RunRawEspSocketProbe();
    if (cleanup) return CleanupLabSaContexts() == ERROR_SUCCESS ? 0 : 5;
    if (abandon) return AbandonActivatedLabContext();
    if (packetVerify) {
        const int rejectedWrongKey = RunPacketRoundTrip(true);
        const int acceptedMatchingKey = RunPacketRoundTrip(false);
        if (rejectedWrongKey || acceptedMatchingKey)
            std::puts("Packet verification FAILED: Windows loopback may bypass ESP; a matching-key delivery alone is not proof of encryption.");
        return rejectedWrongKey || acceptedMatchingKey ? 2 : 0;
    }
    if (!matrix) return RunScenario("Loopback", activate, 1, 3, 20, 16);
    if (RunScenario("SHA1/AES", true, 1, 3, 20, 16)) return 2;
    if (RunScenario("SHA1/NULL", true, 1, 0, 20, 0)) return 2;
    if (RunScenario("MD5/NULL", true, 0, 0, 16, 0)) return 2;
    return RunScenario("SHA1/3DES", true, 1, 2, 20, 24);
}
