#pragma once
#include <windows.h>

#if defined(VOWIN_IMS_WFP_BUILD)
#define VOWIN_IMS_WFP_API __declspec(dllexport)
#else
#define VOWIN_IMS_WFP_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// On failure, *reservation may be non-null if cleanup failed. The caller must
// retry VoWinImsRelease rather than discard that handle.
VOWIN_IMS_WFP_API DWORD __cdecl VoWinImsReserveV6(
    const UINT8 localAddress[16], const UINT8 pcscfAddress[16],
    UINT32 interfaceIndex, UINT16 localClientPort, UINT16 localServerPort,
    void** reservation, UINT32* clientInboundSpi, UINT32* serverInboundSpi);

// Installs both inbound and outbound ESP SAs for each protected local port.
// Algorithms match VoSharp.Ipsec: auth 0=HMAC-MD5-96, 1=HMAC-SHA1-96;
// cipher 0=NULL, 2=3DES-CBC, 3=AES-128-CBC. Key lengths must be 16/20
// (auth) and 0/24/16 (cipher). Keys are only borrowed for the call.
// Remote SPIs are receiver-owned: pcscfServerSpi pairs with the UE client
// port, pcscfClientSpi with the UE server port. On WFP failure this function
// attempts to destroy the reservation and sets *reservation to null if clean;
// otherwise the caller must retry VoWinImsRelease.
VOWIN_IMS_WFP_API DWORD __cdecl VoWinImsActivateV6(
    void** reservation, UINT32 pcscfClientSpi, UINT32 pcscfServerSpi,
    UINT32 authAlgorithm, UINT32 cipherAlgorithm,
    const UINT8* authKey, UINT32 authKeyLength,
    const UINT8* cipherKey, UINT32 cipherKeyLength);

// Pure validation/preflight; does not open WFP or retain key bytes.
VOWIN_IMS_WFP_API DWORD __cdecl VoWinImsValidateKeys(
    UINT32 authAlgorithm, UINT32 cipherAlgorithm,
    const UINT8* authKey, UINT32 authKeyLength,
    const UINT8* cipherKey, UINT32 cipherKeyLength);

// Clears *reservation only after all context/filter deletion succeeds.
VOWIN_IMS_WFP_API DWORD __cdecl VoWinImsRelease(void** reservation);

#ifdef __cplusplus
}
#endif
