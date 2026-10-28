// Experimental Windows IMS WFP backend: reserve two IPv6 transport-mode SA
// contexts before Security-Client, then install ESP after AKA. Deliberately
// not wired into HostImsRegistrationClient until recovery and packet-level
// verification are implemented.
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include "HostImsWfp.h"
#include <fwpmu.h>
#include <iphlpapi.h>
#include <rpc.h>

#include <cstring>
#include <new>

#pragma comment(lib, "Fwpuclnt.lib")
#pragma comment(lib, "Iphlpapi.lib")

namespace {
struct ReservedPair {
    UINT64 inboundFilter = 0;
    UINT64 outboundFilter = 0;
    UINT64 context = 0;
    IPSEC_SA_SPI inboundSpi = 0;
};

struct Reservation {
    HANDLE filterEngine = nullptr; // Dynamic: filters vanish if this process exits.
    HANDLE saEngine = nullptr;     // Manual SA APIs reject dynamic sessions.
    FWP_BYTE_ARRAY16 local{};
    FWP_BYTE_ARRAY16 remote{};
    UINT64 interfaceLuid = 0;
    ReservedPair client{};
    ReservedPair server{};
    bool activated = false;
};

struct KeySpec {
    IPSEC_AUTH_TRANSFORM_ID0 authTransform{};
    IPSEC_CIPHER_TRANSFORM_ID0 cipherTransform{};
    const UINT8* authKey = nullptr;
    UINT32 authLength = 0;
    const UINT8* cipherKey = nullptr;
    UINT32 cipherLength = 0;
    bool hasCipher = false;
};

bool IsValidAddress(const UINT8* address) {
    if (!address || address[0] == 0xFF ||
        (address[0] == 0xFE && (address[1] & 0xC0) == 0x80))
        return false; // Multicast/link-local.
    UINT8 aggregate = 0;
    for (int i = 0; i < 16; ++i) aggregate |= address[i];
    return aggregate != 0;
}

DWORD AddFilter(Reservation& owner, ReservedPair& pair, UINT16 localPort,
                bool inbound) {
    FWPM_FILTER_CONDITION0 conditions[5]{};
    conditions[0].fieldKey = FWPM_CONDITION_IP_LOCAL_ADDRESS;
    conditions[0].matchType = FWP_MATCH_EQUAL;
    conditions[0].conditionValue.type = FWP_BYTE_ARRAY16_TYPE;
    conditions[0].conditionValue.byteArray16 = &owner.local;
    conditions[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
    conditions[1].matchType = FWP_MATCH_EQUAL;
    conditions[1].conditionValue.type = FWP_BYTE_ARRAY16_TYPE;
    conditions[1].conditionValue.byteArray16 = &owner.remote;
    conditions[2].fieldKey = FWPM_CONDITION_IP_PROTOCOL;
    conditions[2].matchType = FWP_MATCH_EQUAL;
    conditions[2].conditionValue.type = FWP_UINT8;
    conditions[2].conditionValue.uint8 = IPPROTO_UDP;
    conditions[3].fieldKey = FWPM_CONDITION_IP_LOCAL_PORT;
    conditions[3].matchType = FWP_MATCH_EQUAL;
    conditions[3].conditionValue.type = FWP_UINT16;
    conditions[3].conditionValue.uint16 = localPort;
    conditions[4].fieldKey = FWPM_CONDITION_IP_LOCAL_INTERFACE;
    conditions[4].matchType = FWP_MATCH_EQUAL;
    conditions[4].conditionValue.type = FWP_UINT64;
    conditions[4].conditionValue.uint64 = &owner.interfaceLuid;

    FWPM_FILTER0 filter{};
    filter.displayData.name = const_cast<PWSTR>(L"VoWin experimental IMS protected SIP port");
    filter.layerKey = inbound ? FWPM_LAYER_INBOUND_TRANSPORT_V6 :
                                FWPM_LAYER_OUTBOUND_TRANSPORT_V6;
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = inbound ? FWPM_CALLOUT_IPSEC_INBOUND_TRANSPORT_V6 :
                                         FWPM_CALLOUT_IPSEC_OUTBOUND_TRANSPORT_V6;
    filter.numFilterConditions = ARRAYSIZE(conditions);
    filter.filterCondition = conditions;
    return FwpmFilterAdd0(owner.filterEngine, &filter, nullptr,
                          inbound ? &pair.inboundFilter : &pair.outboundFilter);
}

DWORD ReservePair(Reservation& owner, ReservedPair& pair, UINT16 localPort) {
    DWORD status = AddFilter(owner, pair, localPort, true);
    if (status != ERROR_SUCCESS) return status;
    status = AddFilter(owner, pair, localPort, false);
    if (status != ERROR_SUCCESS) return status;

    IPSEC_TRAFFIC1 outbound{};
    outbound.ipVersion = FWP_IP_VERSION_V6;
    std::memcpy(outbound.localV6Address, owner.local.byteArray16, 16);
    std::memcpy(outbound.remoteV6Address, owner.remote.byteArray16, 16);
    outbound.trafficType = IPSEC_TRAFFIC_TYPE_TRANSPORT;
    outbound.ipsecFilterId = pair.outboundFilter;
    outbound.localPort = localPort;
    outbound.ipProtocol = IPPROTO_UDP;
    outbound.localIfLuid = owner.interfaceLuid;
    status = IPsecSaContextCreate1(owner.saEngine, &outbound, nullptr, nullptr,
                                   &pair.context);
    if (status != ERROR_SUCCESS) return status;

    IPSEC_GETSPI1 getSpi{};
    getSpi.inboundIpsecTraffic = outbound;
    getSpi.inboundIpsecTraffic.ipsecFilterId = pair.inboundFilter;
    getSpi.ipVersion = FWP_IP_VERSION_V6;
    status = IPsecSaContextGetSpi1(owner.saEngine, pair.context, &getSpi,
                                   &pair.inboundSpi);
    if (status != ERROR_SUCCESS) return status;
    return pair.inboundSpi >= 256 ? ERROR_SUCCESS : ERROR_INVALID_DATA;
}

DWORD ReleasePair(Reservation& owner, ReservedPair& pair) {
    DWORD first = ERROR_SUCCESS;
    if (pair.context) {
        const DWORD result = IPsecSaContextDeleteById0(owner.saEngine, pair.context);
        if (result == ERROR_SUCCESS || result == FWP_E_NOT_FOUND) pair.context = 0;
        else first = result;
    }
    if (pair.outboundFilter) {
        const DWORD result = FwpmFilterDeleteById0(owner.filterEngine, pair.outboundFilter);
        if (result == ERROR_SUCCESS || result == FWP_E_FILTER_NOT_FOUND)
            pair.outboundFilter = 0;
        else if (first == ERROR_SUCCESS) first = result;
    }
    if (pair.inboundFilter) {
        const DWORD result = FwpmFilterDeleteById0(owner.filterEngine, pair.inboundFilter);
        if (result == ERROR_SUCCESS || result == FWP_E_FILTER_NOT_FOUND)
            pair.inboundFilter = 0;
        else if (first == ERROR_SUCCESS) first = result;
    }
    return first;
}

DWORD Release(Reservation& owner) {
    DWORD first = ReleasePair(owner, owner.server);
    const DWORD client = ReleasePair(owner, owner.client);
    if (first == ERROR_SUCCESS) first = client;
    return first;
}

bool ParseKeySpec(UINT32 authAlgorithm, UINT32 cipherAlgorithm,
                  const UINT8* authKey, UINT32 authKeyLength,
                  const UINT8* cipherKey, UINT32 cipherKeyLength,
                  KeySpec& spec) {
    if (!authKey ||
        (authAlgorithm == 0 && authKeyLength != 16) ||
        (authAlgorithm == 1 && authKeyLength != 20) ||
        authAlgorithm > 1) return false;
    spec.authTransform = authAlgorithm == 0 ?
        IPSEC_AUTH_TRANSFORM_ID_HMAC_MD5_96 :
        IPSEC_AUTH_TRANSFORM_ID_HMAC_SHA_1_96;
    spec.authKey = authKey;
    spec.authLength = authKeyLength;
    switch (cipherAlgorithm) {
    case 0: // ealg=null: ESP authentication without an encryption transform.
        if (cipherKeyLength != 0) return false;
        break;
    case 2: // des-ede3-cbc
        if (!cipherKey || cipherKeyLength != 24) return false;
        spec.cipherTransform = IPSEC_CIPHER_TRANSFORM_ID_CBC_3DES;
        spec.hasCipher = true;
        break;
    case 3: // aes-cbc
        if (!cipherKey || cipherKeyLength != 16) return false;
        spec.cipherTransform = IPSEC_CIPHER_TRANSFORM_ID_AES_128;
        spec.hasCipher = true;
        break;
    default:
        return false;
    }
    spec.cipherKey = cipherKey;
    spec.cipherLength = cipherKeyLength;
    return true;
}

DWORD InstallSa(HANDLE engine, UINT64 context, IPSEC_SA_SPI spi,
                const KeySpec& keys, bool inbound) {
    IPSEC_SA_AUTH_INFORMATION0 auth{};
    auth.authTransform.authTransformId = keys.authTransform;
    auth.authKey.size = keys.authLength;
    auth.authKey.data = const_cast<UINT8*>(keys.authKey);

    IPSEC_SA_AUTH_AND_CIPHER_INFORMATION0 encrypted{};
    encrypted.saAuthInformation = auth;
    encrypted.saCipherInformation.cipherTransform.cipherTransformId =
        keys.cipherTransform;
    encrypted.saCipherInformation.cipherKey.size = keys.cipherLength;
    encrypted.saCipherInformation.cipherKey.data =
        const_cast<UINT8*>(keys.cipherKey);

    IPSEC_SA0 sa{};
    sa.spi = spi;
    if (keys.hasCipher) {
        sa.saTransformType = IPSEC_TRANSFORM_ESP_AUTH_AND_CIPHER;
        sa.espAuthAndCipherInformation = &encrypted;
    } else {
        sa.saTransformType = IPSEC_TRANSFORM_ESP_AUTH;
        sa.espAuthInformation = &auth;
    }
    IPSEC_SA_BUNDLE1 bundle{};
    bundle.numSAs = 1;
    bundle.saList = &sa;
    bundle.ipVersion = FWP_IP_VERSION_V6;
    // WFP bounds this field; the 3GPP "keep until SIP teardown" model needs
    // explicit refresh/rekey rather than UINT32_MAX in the Windows kernel.
    bundle.lifetime.lifetimeSeconds = 3600;
    return inbound ? IPsecSaContextAddInbound1(engine, context, &bundle) :
                     IPsecSaContextAddOutbound1(engine, context, &bundle);
}
} // namespace

extern "C" VOWIN_IMS_WFP_API DWORD __cdecl VoWinImsReserveV6(
    const UINT8 localAddress[16], const UINT8 pcscfAddress[16],
    UINT32 interfaceIndex, UINT16 localClientPort, UINT16 localServerPort,
    void** reservation, UINT32* clientInboundSpi, UINT32* serverInboundSpi) {
    if (!reservation || !clientInboundSpi || !serverInboundSpi ||
        !IsValidAddress(localAddress) || !IsValidAddress(pcscfAddress) ||
        std::memcmp(localAddress, pcscfAddress, 16) == 0 ||
        interfaceIndex == 0 || localClientPort <= 1024 || localServerPort <= 1024 ||
        localClientPort == 5060 || localClientPort == 5061 ||
        localServerPort == 5060 || localServerPort == 5061 ||
        localClientPort == localServerPort) return ERROR_INVALID_PARAMETER;
    *reservation = nullptr;
    *clientInboundSpi = 0;
    *serverInboundSpi = 0;

    NET_LUID luid{};
    DWORD status = ConvertInterfaceIndexToLuid(interfaceIndex, &luid);
    if (status != ERROR_SUCCESS) return status;
    Reservation* owner = new (std::nothrow) Reservation;
    if (!owner) return ERROR_NOT_ENOUGH_MEMORY;
    owner->interfaceLuid = luid.Value;
    std::memcpy(owner->local.byteArray16, localAddress, 16);
    std::memcpy(owner->remote.byteArray16, pcscfAddress, 16);
    FWPM_SESSION0 filterSession{};
    filterSession.flags = FWPM_SESSION_FLAG_DYNAMIC;
    status = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr,
                             &filterSession, &owner->filterEngine);
    if (status == ERROR_SUCCESS)
        status = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, nullptr,
                                 &owner->saEngine);
    if (status == ERROR_SUCCESS)
        status = ReservePair(*owner, owner->client, localClientPort);
    if (status == ERROR_SUCCESS)
        status = ReservePair(*owner, owner->server, localServerPort);
    if (status == ERROR_SUCCESS &&
        owner->client.inboundSpi == owner->server.inboundSpi)
        status = ERROR_INVALID_DATA;
    if (status != ERROR_SUCCESS) {
        if (owner->filterEngine) {
            const DWORD cleanup = Release(*owner);
            if (cleanup != ERROR_SUCCESS) {
                *reservation = owner; // Caller can retry release with the saved IDs.
                return cleanup;
            }
        }
        if (owner->saEngine) {
            const DWORD close = FwpmEngineClose0(owner->saEngine);
            if (close != ERROR_SUCCESS) {
                *reservation = owner;
                return close;
            }
            owner->saEngine = nullptr;
        }
        if (owner->filterEngine) {
            const DWORD close = FwpmEngineClose0(owner->filterEngine);
            if (close != ERROR_SUCCESS) {
                *reservation = owner;
                return close;
            }
            owner->filterEngine = nullptr;
        }
        delete owner;
        return status;
    }
    *clientInboundSpi = owner->client.inboundSpi;
    *serverInboundSpi = owner->server.inboundSpi;
    *reservation = owner;
    return ERROR_SUCCESS;
}

extern "C" VOWIN_IMS_WFP_API DWORD __cdecl VoWinImsActivateV6(
    void** reservation, UINT32 pcscfClientSpi, UINT32 pcscfServerSpi,
    UINT32 authAlgorithm, UINT32 cipherAlgorithm,
    const UINT8* authKey, UINT32 authKeyLength,
    const UINT8* cipherKey, UINT32 cipherKeyLength) {
    if (!reservation || !*reservation) return ERROR_INVALID_PARAMETER;
    Reservation* owner = static_cast<Reservation*>(*reservation);
    if (owner->activated) return ERROR_ALREADY_EXISTS;
    if (pcscfClientSpi < 256 || pcscfServerSpi < 256 ||
        pcscfClientSpi == pcscfServerSpi ||
        pcscfClientSpi == owner->client.inboundSpi ||
        pcscfClientSpi == owner->server.inboundSpi ||
        pcscfServerSpi == owner->client.inboundSpi ||
        pcscfServerSpi == owner->server.inboundSpi)
        return ERROR_INVALID_PARAMETER;
    KeySpec keys{};
    if (!ParseKeySpec(authAlgorithm, cipherAlgorithm, authKey, authKeyLength,
                      cipherKey, cipherKeyLength, keys))
        return ERROR_INVALID_PARAMETER;

    DWORD status = InstallSa(owner->saEngine, owner->client.context,
                             owner->client.inboundSpi, keys, true);
    if (status == ERROR_SUCCESS)
        status = InstallSa(owner->saEngine, owner->server.context,
                           owner->server.inboundSpi, keys, true);
    if (status == ERROR_SUCCESS)
        status = InstallSa(owner->saEngine, owner->client.context,
                           pcscfServerSpi, keys, false);
    if (status == ERROR_SUCCESS)
        status = InstallSa(owner->saEngine, owner->server.context,
                           pcscfClientSpi, keys, false);
    if (status == ERROR_SUCCESS) {
        owner->activated = true;
        return ERROR_SUCCESS;
    }
    const DWORD cleanup = VoWinImsRelease(reservation);
    return cleanup == ERROR_SUCCESS ? status : cleanup;
}

extern "C" VOWIN_IMS_WFP_API DWORD __cdecl VoWinImsValidateKeys(
    UINT32 authAlgorithm, UINT32 cipherAlgorithm,
    const UINT8* authKey, UINT32 authKeyLength,
    const UINT8* cipherKey, UINT32 cipherKeyLength) {
    KeySpec keys{};
    return ParseKeySpec(authAlgorithm, cipherAlgorithm, authKey, authKeyLength,
                        cipherKey, cipherKeyLength, keys) ?
        ERROR_SUCCESS : ERROR_INVALID_PARAMETER;
}

extern "C" VOWIN_IMS_WFP_API DWORD __cdecl VoWinImsRelease(void** reservation) {
    if (!reservation || !*reservation) return ERROR_INVALID_PARAMETER;
    Reservation* owner = static_cast<Reservation*>(*reservation);
    const DWORD status = owner->filterEngine ? Release(*owner) : ERROR_SUCCESS;
    if (status != ERROR_SUCCESS) return status; // Preserve IDs for retry.
    if (owner->saEngine) {
        const DWORD close = FwpmEngineClose0(owner->saEngine);
        if (close != ERROR_SUCCESS) return close;
        owner->saEngine = nullptr;
    }
    if (owner->filterEngine) {
        const DWORD close = FwpmEngineClose0(owner->filterEngine);
        if (close != ERROR_SUCCESS) return close;
        owner->filterEngine = nullptr;
    }
    delete owner;
    *reservation = nullptr;
    return ERROR_SUCCESS;
}
