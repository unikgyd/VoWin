// Read-only check for the Windows WFP pieces needed by IMS manual IPsec SAs.
// This does not create filters, SA contexts, SPIs, or network traffic.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <fwpmu.h>
#include <rpc.h>

#include <cstdio>

#pragma comment(lib, "Fwpuclnt.lib")

namespace {
struct Probe {
    const char* name;
    const GUID* key;
    bool callout;
};
}

int wmain() {
    HANDLE engine = nullptr;
    const DWORD open = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr,
                                       nullptr, &engine);
    if (open != ERROR_SUCCESS) {
        std::printf("WFP engine open failed: 0x%08lx\n", open);
        return 1;
    }

    const Probe probes[] = {
        {"IPv6 inbound transport layer", &FWPM_LAYER_INBOUND_TRANSPORT_V6, false},
        {"IPv6 outbound transport layer", &FWPM_LAYER_OUTBOUND_TRANSPORT_V6, false},
        {"IPv6 inbound IPsec transport callout", &FWPM_CALLOUT_IPSEC_INBOUND_TRANSPORT_V6, true},
        {"IPv6 outbound IPsec transport callout", &FWPM_CALLOUT_IPSEC_OUTBOUND_TRANSPORT_V6, true},
    };
    bool ready = true;
    for (const Probe& probe : probes) {
        DWORD status;
        if (probe.callout) {
            FWPM_CALLOUT0* object = nullptr;
            status = FwpmCalloutGetByKey0(engine, probe.key, &object);
            if (object) FwpmFreeMemory0(reinterpret_cast<void**>(&object));
        } else {
            FWPM_LAYER0* object = nullptr;
            status = FwpmLayerGetByKey0(engine, probe.key, &object);
            if (object) FwpmFreeMemory0(reinterpret_cast<void**>(&object));
        }
        std::printf("%s: %s (0x%08lx)\n", probe.name,
                    status == ERROR_SUCCESS ? "available" : "unavailable", status);
        ready = ready && status == ERROR_SUCCESS;
    }
    const DWORD close = FwpmEngineClose0(engine);
    if (close != ERROR_SUCCESS) {
        std::printf("WFP engine close failed: 0x%08lx\n", close);
        return 1;
    }
    std::puts("Read-only check only: SA write permission and ESP data plane are untested.");
    return ready ? 0 : 2;
}
