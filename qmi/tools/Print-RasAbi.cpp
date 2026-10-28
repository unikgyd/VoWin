// Developer-only ABI check for the managed RAS interop. Never packaged or dialed.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ras.h>
#include <cstdio>
#include <cstddef>
int main() {
    std::printf("RASDEVINFOW=%zu RASCONNW=%zu RASCONNSTATUSW=%zu RASDIALPARAMSW=%zu RASENTRYW=%zu\n",
        sizeof(RASDEVINFOW), sizeof(RASCONNW), sizeof(RASCONNSTATUSW), sizeof(RASDIALPARAMSW), sizeof(RASENTRYW));
    std::printf("entry net=%zu frame=%zu deviceType=%zu deviceName=%zu type=%zu options2=%zu ipv6metric=%zu\n",
        offsetof(RASENTRYW, dwfNetProtocols), offsetof(RASENTRYW, dwFramingProtocol), offsetof(RASENTRYW, szDeviceType),
        offsetof(RASENTRYW, szDeviceName), offsetof(RASENTRYW, dwType), offsetof(RASENTRYW, dwfOptions2), offsetof(RASENTRYW, dwIPv6InterfaceMetric));
}
