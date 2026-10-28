// One-shot Windows RAS probe for the modem's IMS PDP (CID 5). The optional
// --no-ccp mode creates a temporary phonebook and removes it after the probe.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ras.h>
#include <raserror.h>

#include <atomic>
#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>

namespace {
std::atomic<bool> finished{false};
std::atomic<bool> connected{false};
std::atomic<DWORD> dialError{0};

void CALLBACK OnDialState(HRASCONN, UINT, RASCONNSTATE state, DWORD error,
                          DWORD extendedError) {
    std::printf("RAS state=%lu error=%lu extended=%lu\n",
                static_cast<unsigned long>(state),
                static_cast<unsigned long>(error),
                static_cast<unsigned long>(extendedError));
    std::fflush(stdout);
    if (error != 0) {
        dialError.store(error);
        finished.store(true);
    } else if (state == RASCS_Connected) {
        connected.store(true);
        finished.store(true);
    } else if (state == RASCS_Disconnected) {
        finished.store(true);
    }
}

void PrintRasError(DWORD error) {
    WCHAR message[512]{};
    if (RasGetErrorStringW(error, message, ARRAYSIZE(message)) == ERROR_SUCCESS) {
        std::wprintf(L"RAS error %lu: %ls\n", error, message);
    } else {
        std::printf("RAS error %lu\n", static_cast<unsigned long>(error));
    }
}

bool FindSoleQuectelModem(RASDEVINFOW& modem) {
    DWORD bytes = 0;
    DWORD count = 0;
    if (RasEnumDevicesW(nullptr, &bytes, &count) != ERROR_BUFFER_TOO_SMALL ||
        bytes < sizeof(RASDEVINFOW)) {
        std::puts("Could not enumerate Windows RAS devices.");
        return false;
    }
    std::vector<BYTE> buffer(bytes);
    auto* devices = reinterpret_cast<RASDEVINFOW*>(buffer.data());
    devices[0].dwSize = sizeof(RASDEVINFOW);
    const DWORD result = RasEnumDevicesW(devices, &bytes, &count);
    if (result != ERROR_SUCCESS) {
        PrintRasError(result);
        return false;
    }
    unsigned int matches = 0;
    for (DWORD i = 0; i < count; ++i) {
        if (wcscmp(devices[i].szDeviceType, L"modem") == 0 &&
            wcsstr(devices[i].szDeviceName, L"Quectel USB Modem Port") != nullptr) {
            modem = devices[i];
            ++matches;
        }
    }
    if (matches != 1) {
        std::printf("Expected exactly one Quectel modem; found %u. No dial attempted.\n", matches);
        return false;
    }
    return true;
}

bool PrepareNoCcpPhonebook(const RASDEVINFOW& modem, WCHAR* phonebook) {
    WCHAR tempDir[MAX_PATH]{};
    if (GetTempPathW(ARRAYSIZE(tempDir), tempDir) == 0 ||
        GetTempFileNameW(tempDir, L"VIM", 0, phonebook) == 0) {
        std::puts("Could not create a temporary phonebook path.");
        return false;
    }
    // GetTempFileName creates an empty file; RAS creates the phonebook itself.
    DeleteFileW(phonebook);
    RASENTRYW entry{};
    entry.dwSize = sizeof(entry);
    entry.dwFramingProtocol = RASFP_Ppp;
    entry.dwType = RASET_Phone;
    entry.dwfNetProtocols = RASNP_Ip | RASNP_Ipv6;
    entry.dwfOptions = 0;  // In particular, do not set RASEO_SwCompression.
    // Windows still installed a PPP IPv6 default route even without
    // RASEO2_IPv6RemoteDefaultGateway. A high interface metric should prefer
    // existing connections, but it is not destination-specific isolation.
    entry.dwfOptions2 = RASEO2_IPv6ExplicitMetric;
    entry.dwIPv6InterfaceMetric = 9999;
    wcscpy_s(entry.szLocalPhoneNumber, L"*99***5#");
    wcscpy_s(entry.szDeviceType, modem.szDeviceType);
    wcscpy_s(entry.szDeviceName, modem.szDeviceName);
    const DWORD result = RasSetEntryPropertiesW(phonebook, L"VoWin IMS PPP Probe",
                                                &entry, sizeof(entry), nullptr, 0);
    if (result != ERROR_SUCCESS) {
        PrintRasError(result);
        DeleteFileW(phonebook);
        phonebook[0] = L'\0';
        return false;
    }
    std::puts("Temporary RAS entry created with CCP disabled.");
    return true;
}

DWORD WaitForLeaseStop(HRASCONN connection, unsigned int maxSeconds) {
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    std::string command;
    ULONGLONG renewedAt = GetTickCount64();
    while (GetTickCount64() - renewedAt < static_cast<ULONGLONG>(maxSeconds) * 1000) {
        RASCONNSTATUSW ras{};
        ras.dwSize = sizeof(ras);
        const DWORD rasStatus = RasGetConnectStatusW(connection, &ras);
        if (rasStatus != ERROR_SUCCESS || ras.rasconnstate != RASCS_Connected) {
            std::printf("IMS PPP lease lost its RAS connection: %lu state=%lu\n",
                        rasStatus, static_cast<unsigned long>(ras.rasconnstate));
            return ERROR_CONNECTION_ABORTED;
        }
        DWORD available = 0;
        if (!PeekNamedPipe(input, nullptr, 0, nullptr, &available, nullptr)) {
            const DWORD error = GetLastError();
            if (error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED ||
                error == ERROR_NO_DATA) {
                // A closed parent stdin pipe is equivalent to an explicit stop.
                std::puts("IMS PPP lease owner closed stdin; hanging up.");
                return ERROR_SUCCESS;
            }
            std::printf("IMS PPP lease control pipe failed: %lu; hanging up.\n", error);
            return error;
        }
        if (available) {
            char bytes[128]{};
            DWORD read = 0;
            if (!ReadFile(input, bytes, min(available, DWORD(sizeof(bytes))),
                          &read, nullptr)) return GetLastError();
            for (DWORD i = 0; i < read; ++i) {
                if (bytes[i] == '\n') {
                    if (!command.empty() && command.back() == '\r')
                        command.pop_back();
                    if (command == "STOP") return ERROR_SUCCESS;
                    if (command != "RENEW") return ERROR_INVALID_DATA;
                    renewedAt = GetTickCount64();
                    command.clear();
                    continue;
                }
                if (command.size() >= 16) return ERROR_INVALID_DATA;
                command.push_back(bytes[i]);
            }
        }
        Sleep(500);
    }
    std::puts("IMS PPP lease heartbeat expired; hanging up.");
    return WAIT_TIMEOUT;
}
}  // namespace

int wmain(int argc, wchar_t** argv) {
    bool noCcp = false;
    bool checkModem = false;
    bool checkPhonebook = false;
    bool untilStop = false;
    unsigned int holdSeconds = 60;
    unsigned int maxSeconds = 3600;
    for (int i = 1; i < argc; ++i) {
        if (wcscmp(argv[i], L"--no-ccp") == 0) {
            noCcp = true;
        } else if (wcscmp(argv[i], L"--check-modem") == 0) {
            checkModem = true;
        } else if (wcscmp(argv[i], L"--check-phonebook") == 0) {
            checkPhonebook = true;
        } else if (wcscmp(argv[i], L"--until-stop") == 0) {
            untilStop = true;
        } else if (wcsncmp(argv[i], L"--hold-seconds=", 15) == 0) {
            wchar_t* end = nullptr;
            const unsigned long requested = wcstoul(argv[i] + 15, &end, 10);
            if (end == argv[i] + 15 || *end != L'\0' || requested < 15 || requested > 300) {
                std::puts("Hold time must be 15-300 seconds.");
                return 1;
            }
            holdSeconds = static_cast<unsigned int>(requested);
        } else if (wcsncmp(argv[i], L"--max-seconds=", 14) == 0) {
            wchar_t* end = nullptr;
            const unsigned long requested = wcstoul(argv[i] + 14, &end, 10);
            if (end == argv[i] + 14 || *end != L'\0' || requested < 15 || requested > 3600) {
                std::puts("Lease maximum must be 15-3600 seconds.");
                return 1;
            }
            maxSeconds = static_cast<unsigned int>(requested);
        } else {
            std::puts("Usage: RasImsPppProbe.exe [--no-ccp|--check-modem|--check-phonebook] [--hold-seconds=15..300|--until-stop --max-seconds=15..3600]");
            return 1;
        }
    }
    if ((checkModem || checkPhonebook) &&
        (noCcp || checkModem == checkPhonebook || argc > 2 || untilStop)) {
        std::puts("Preflight checks cannot be combined with dial options.");
        return 1;
    }
    if (untilStop && (!noCcp || GetFileType(GetStdHandle(STD_INPUT_HANDLE)) != FILE_TYPE_PIPE)) {
        std::puts("--until-stop requires --no-ccp and a redirected owner stdin pipe; no dial attempted.");
        return 1;
    }
    RASDEVINFOW modem{};
    if (!FindSoleQuectelModem(modem)) {
        return 1;
    }
    if (checkModem) {
        std::wprintf(L"RAS modem ready: %ls\n", modem.szDeviceName);
        return 0;
    }
    if (checkPhonebook) {
        WCHAR testPhonebook[MAX_PATH]{};
        if (!PrepareNoCcpPhonebook(modem, testPhonebook)) return 1;
        const UINT metric = GetPrivateProfileIntW(L"VoWin IMS PPP Probe",
            L"Ipv6InterfaceMetric", 0, testPhonebook);
        const BOOL deleted = DeleteFileW(testPhonebook);
        std::printf("Temporary phonebook IPv6 metric=%u removed=%d; no dial attempted.\n",
                    metric, deleted);
        return metric == 9999 && deleted ? 0 : 2;
    }
    WCHAR phonebook[MAX_PATH]{};
    if (noCcp && !PrepareNoCcpPhonebook(modem, phonebook)) {
        return 1;
    }
    RASDIALPARAMSW params{};
    params.dwSize = sizeof(params);
    wcscpy_s(params.szPhoneNumber, L"*99***5#");
    if (noCcp) {
        wcscpy_s(params.szEntryName, L"VoWin IMS PPP Probe");
    }
    HRASCONN connection = nullptr;
    DWORD leaseResult = ERROR_SUCCESS;
    std::puts("Dialing IMS CID 5 through the sole installed Windows modem.");
    const DWORD start = RasDialW(nullptr, noCcp ? phonebook : nullptr, &params, 1,
                                 reinterpret_cast<LPVOID>(OnDialState), &connection);
    if (start != ERROR_SUCCESS) {
        PrintRasError(start);
    } else {
        for (int i = 0; i < 180 && !finished.load(); ++i) {
            Sleep(500);
        }
        if (!finished.load()) {
            std::puts("RAS dial timed out after 90 seconds.");
        } else if (dialError.load() != 0) {
            PrintRasError(dialError.load());
        } else if (connected.load()) {
            if (untilStop)
                std::printf("RAS connected; holding until STOP/owner exit (maximum %u seconds).\n", maxSeconds);
            else
                std::printf("RAS connected; holding for %u seconds to inspect IPv6.\n", holdSeconds);
            RAS_PROJECTION_INFO projection{};
            projection.version = RASAPIVERSION_CURRENT;
            DWORD size = sizeof(projection);
            const DWORD result = RasGetProjectionInfoEx(connection, &projection, &size);
            if (result == ERROR_SUCCESS && projection.type == PROJECTION_INFO_TYPE_PPP) {
                std::printf("PPP IPv4 negotiation=%lu IPv6 negotiation=%lu\n",
                            static_cast<unsigned long>(projection.ppp.dwIPv4NegotiationError),
                            static_cast<unsigned long>(projection.ppp.dwIPv6NegotiationError));
            } else {
                std::printf("RAS projection query=%lu type=%lu\n",
                            static_cast<unsigned long>(result),
                            static_cast<unsigned long>(projection.type));
            }
            std::fflush(stdout);
            if (untilStop)
                leaseResult = WaitForLeaseStop(connection, maxSeconds);
            else
                Sleep(holdSeconds * 1000);
        }
    }

    if (connection != nullptr) {
        const DWORD hangup = RasHangUpW(connection);
        std::printf("RAS hangup=%lu\n", static_cast<unsigned long>(hangup));
        // RAS keeps the connection handle live until disconnect completes.
        for (int i = 0; i < 30; ++i) {
            RASCONNSTATUSW status{};
            status.dwSize = sizeof(status);
            if (RasGetConnectStatusW(connection, &status) == ERROR_INVALID_HANDLE) {
                break;
            }
            Sleep(500);
        }
    }
    if (noCcp && phonebook[0] != L'\0') {
        if (!DeleteFileW(phonebook)) {
            std::wprintf(L"Warning: temporary phonebook could not be removed: %ls\n", phonebook);
        }
    }
    return connected.load() && leaseResult == ERROR_SUCCESS ? 0 : 2;
}
