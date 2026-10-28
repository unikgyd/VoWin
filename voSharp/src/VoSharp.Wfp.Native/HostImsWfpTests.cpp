// Validation-only smoke tests. Every input fails before WFP is opened.
#include "HostImsWfp.h"

#include <cassert>

int main() {
    const UINT8 local[16] = {0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0,
                             0, 0, 0, 0, 0, 0, 0, 1};
    const UINT8 remote[16] = {0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0,
                              0, 0, 0, 0, 0, 0, 0, 2};
    void* reservation = nullptr;
    UINT32 clientSpi = 0, serverSpi = 0;
    assert(VoWinImsReserveV6(local, remote, 0, 6000, 6001,
                             &reservation, &clientSpi, &serverSpi) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsReserveV6(local, remote, 1, 6000, 6000,
                             &reservation, &clientSpi, &serverSpi) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsReserveV6(local, remote, 1, 1024, 6001,
                             &reservation, &clientSpi, &serverSpi) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsReserveV6(local, remote, 1, 5060, 6001,
                             &reservation, &clientSpi, &serverSpi) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsReserveV6(local, local, 1, 6000, 6001,
                             &reservation, &clientSpi, &serverSpi) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsReserveV6(local, remote, 1, 6000, 6001,
                             nullptr, &clientSpi, &serverSpi) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsActivateV6(&reservation, 2001, 2002, 1, 0,
                              local, 20, nullptr, 0) == ERROR_INVALID_PARAMETER);
    const UINT8 sha1Key[20]{};
    const UINT8 md5Key[16]{};
    const UINT8 aesKey[16]{};
    const UINT8 tripleDesKey[24]{};
    assert(VoWinImsValidateKeys(1, 0, sha1Key, 20, nullptr, 0) == ERROR_SUCCESS);
    assert(VoWinImsValidateKeys(0, 0, md5Key, 16, nullptr, 0) == ERROR_SUCCESS);
    assert(VoWinImsValidateKeys(1, 3, sha1Key, 20, aesKey, 16) == ERROR_SUCCESS);
    assert(VoWinImsValidateKeys(0, 2, md5Key, 16, tripleDesKey, 24) == ERROR_SUCCESS);
    assert(VoWinImsValidateKeys(1, 3, sha1Key, 16, aesKey, 16) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsValidateKeys(1, 3, sha1Key, 20, aesKey, 24) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsValidateKeys(1, 0, sha1Key, 20, aesKey, 16) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsValidateKeys(2, 0, sha1Key, 20, nullptr, 0) == ERROR_INVALID_PARAMETER);
    assert(VoWinImsRelease(nullptr) == ERROR_INVALID_PARAMETER);
    assert(reservation == nullptr && clientSpi == 0 && serverSpi == 0);
    return 0;
}
