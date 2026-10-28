# Experimental Host IMS WFP reservation

`HostImsWfp.cpp` is an experimental native stage of a Windows `ipsec-3gpp` backend.
It reserves two IPv6 transport-mode SA contexts and inbound SPIs on a verified
interface, matching the UE client and server SIP ports separately. After AKA,
`VoWinImsActivateV6` can install the four inbound/outbound ESP bundles from
the selected P-CSCF SPIs and already-expanded CK/IK keys. Its C ABI also
exports `VoWinImsReserveV6` and `VoWinImsRelease`. An opt-in managed
[`IHostImsSecurityBackend`](../VoSharp.Telephony/WindowsHostImsSecurityBackend.cs)
adapter now handles SPI/port binding, CK/IK expansion and cleanup retries.
The product does **not** instantiate it by default or distribute this DLL yet.
Build with Visual Studio's x64 Developer Command Prompt:

```bat
cmake -S voSharp\src\VoSharp.Wfp.Native -B build\wfp-ims -G "NMake Makefiles"
cmake --build build\wfp-ims
```

The implementation follows Microsoft's [manual SA keying sample](https://learn.microsoft.com/en-us/windows/win32/fwp/manual-sa-keying) and
[manual SA flow](https://learn.microsoft.com/en-us/windows/win32/fwp/manual-ipsec-sas).
The automated tests only exercise validation paths that do not open WFP.
`VoWinHostImsWfpLab` is an explicitly built, fixed-address `::1` / `::2`
loopback exercise; it must never be mistaken for carrier or modem validation.
Its `--matrix` mode installs and removes SHA1/AES, SHA1/NULL, MD5/NULL and
SHA1/3DES SA pairs in sequence, then read-only audits matching WFP filters and
SA contexts. All four combinations passed on this machine with zero residual
objects. `--audit-only` can be rerun without changing WFP state.
`--dynamic-filter-probe` verified that two filters in a dynamic WFP session
can be referenced by a manual SA context in a separate normal session:
context creation and inbound SPI reservation succeeded, and closing the
dynamic session removed both filters. The backend now uses this dual-session
arrangement; the four-algorithm matrix passed again with zero residual objects.
`--abandon-activated` deliberately exits without calling `VoWinImsRelease`
after a fixed loopback activation. The pre-exit audit observed four filters
and two SA contexts; the next process observed zero of each. A scoped
`--cleanup-loopback` command is available for this synthetic test only. This
is evidence about process-exit cleanup on this Windows machine, not a general
guarantee for every failure mode or a carrier-bearer test.
The reservation export has now been invoked on the live, Windows-owned IMS PPP
interface: two locally bound UDP ports obtained distinct inbound SPIs, then
the reservation was released without SA activation or sending packets. The
modem was restored to internal IMS afterward. No encrypted carrier packet has
been validated.
The managed adapter has also completed a separate elevated loopback
reservation/activation/disposal through the actual DLL; `--audit-only`
reported zero matching filters and SA contexts afterward.
The Windows-native `--packet-verify` lab adds a negative control: a peer SA
with a deliberately wrong authentication key must reject the UDP packet, while
matching keys must deliver it. It uses only `::1` and a temporary, active-store
`fd7a:6f57:494d:5300::2/128` alias on Windows loopback. On this machine the
wrong-key packet **was delivered**. Thus loopback delivery bypassed (or did not
exercise) ESP and is not packet-level validation of this backend. The alias
was removed with `netsh interface ipv6 delete address interface=1
address=fd7a:6f57:494d:5300::2 store=active`; subsequent address inspection
showed only `::1`. Both attempts left zero matching filters and SA contexts.
Do not treat a matching-key receive in this lab as a pass without the
wrong-key negative control; use a genuinely non-loopback Windows test peer for
future ESP validation.
`VoWinWintunImsLab --wfp-packet <absolute-wintun.dll>` now supplies an isolated
non-loopback **Windows-native** outbound test using a temporary `VoWin IMS Lab`
adapter and only `fd7a:6f57:494d:5301::1/128` to `::2/128`. The DLL can be
obtained from the [official Wintun site](https://www.wintun.net/); it is not
checked into this repository. The lab installs a 60-second local address and
peer-only `/128` route, binds UDP to that interface, captures the baseline
IPv6 next-header 17 packet, then activates SHA1/AES WFP SAs and captures
next-header 50 ESP. On this machine, the ESP wire SPI was `0xabcdef02` as
configured and the test plaintext marker did not appear in the captured packet.
It releases the WFP reservation, route, address, session and adapter on exit;
an elevated `VoWinHostImsWfpLab --audit-only` plus independent adapter/address/
route checks found zero residual objects. This proves outbound encryption on
the synthetic non-loopback path, **not** carrier PPP compatibility, IMS
registration or voice. No WSL, ADB, modem or carrier network was used.
The `--wfp-bidirectional` extension reinjects that encrypted packet with the
peer IPv6 address and the reserved inbound SPI. It recomputes HMAC-SHA1 using
the synthetic lab key, leaving the AES ciphertext untouched; the equal UDP
ports make the reversed IPv6 pseudo-header checksum valid. Windows delivered
the correctly authenticated plaintext to the bound socket, rejected a packet
with one corrupted ICV byte, and accepted the intact packet of the same
sequence afterward. **But it also delivered that intact packet a second time
when replayed, after an explicit empty receive-queue timeout.** This lab
deliberately exits nonzero for that security gap.
Manual WFP SAs must not be enabled as the default Host IMS backend until replay
protection is demonstrated or provided by a suitable ingress layer. Both
negative and positive runs cleaned all synthetic WFP/adapter/address/route
state. The [Microsoft manual-SA API](https://learn.microsoft.com/en-us/windows/win32/fwp/manual-ipsec-sas)
documents SA installation, but its
[`IPSEC_SA_BUNDLE1` fields](https://learn.microsoft.com/en-us/windows/win32/api/ipsectypes/ns-ipsectypes-ipsec_sa_bundle1)
do not expose an obvious per-SA replay-window setting; no claim is made that
all Windows IPsec configurations accept replays.
An additional no-packet `--manual-spi-probe` used exact remote UDP port
conditions and attempted to install a caller-chosen inbound SPI directly after
`IPsecSaContextCreate1`. Windows returned `0x8032001B`
(`FWP_E_INCOMPATIBLE_SA_STATE`) at `IPsecSaContextAddInbound1`; the follow-up
audit found zero residual filters/SA contexts. This confirms on this machine
that the SA state machine requires `GetSpi` before inbound installation, as
documented in [Microsoft's WFP error codes](https://learn.microsoft.com/en-us/windows/win32/fwp/wfp-error-codes).
Generating a random SPI before registration and installing it only after the
P-CSCF reveals its ports is **not** a valid shortcut. The existing backend
still lacks exact remote-port enforcement; a two-stage selector design must
retain the reserved SPI while safely narrowing the effective traffic policy.
As a separate all-Windows alternative, `--raw-esp-probe` tests an elevated
Winsock `AF_INET6/SOCK_RAW/IPPROTO_ESP` socket using only `::1`. Socket creation
and synthetic protocol-50 send succeeded, but receive timed out (`WSA 10060`).
This does not rule out reception on a non-loopback interface, but it does not
establish a usable raw-ESP data plane either. See [Microsoft's raw-socket
documentation](https://learn.microsoft.com/en-us/windows/win32/winsock/tcp-ip-raw-sockets-2)
for API behavior and administrator requirements. No carrier traffic was sent.
The separate `VoWinWintunImsLab --raw-esp-inbound` check also injected a
synthetic, deliberately unauthenticated IPv6 protocol-50 packet from the
temporary non-loopback Wintun adapter. A raw ESP socket bound to the local
test IPv6 address timed out (`WSA 10060`); the adapter, address and route were
removed. This rules out the naive raw-socket receive path for that packet on
this machine, not a lower-layer packet capture design or a valid-SA packet on
the real PPP bearer.
An installed Npcap driver provides a lower-layer path on the synthetic Wintun
interface: `--npcap-esp-capture` matched the exact injected inbound IPv6 ESP
packet at DLT_RAW before Winsock, and `--npcap-esp-send` sent an outbound raw
IPv6 ESP packet that Wintun received byte-for-byte. Both labs use only the
temporary adapter; Npcap is dynamically loaded from the installed system
directory and is not bundled. This is a useful native Windows user-space ESP
transport experiment, **not** evidence that Npcap can see the real IMS bearer.
The read-only `VoWinNpcapInterfaceProbe <interface-index>` was then run while
the actual IMS CID 5 was connected through Windows RAS PPP with only P-CSCF
`/128` routes. Npcap did **not** enumerate the exact PPP interface GUID on
this machine. The PPP was hung up, the modem restored to `ims=1,1` and
`usbnet=0`, and independent checks found no PPP interface, probe process or
host routes. The Wintun Npcap success therefore cannot be wired into this
PPP product path. Npcap's [capture API](https://npcap.com/guide/wpcap/pcap_findalldevs.html)
and [send API](https://npcap.com/guide/npcap-tutorial.html) are separate from
packet blocking; preventing replays before the Windows WFP decryptor would
need an interface-visible interception layer, such as a narrowly scoped WFP
callout/WinDivert experiment, or another bearer design.

The official WinDivert `netdump.exe` in SNIFF mode was subsequently checked
against the real, route-isolated IMS RAS PPP. It observed 19 outbound ICMPv6
packets on the exact PPP interface index, sourced from the current IMS IPv6
address and addressed to the runtime P-CSCF. No SIP identity or ESP was sent.
The local `--windivert-sniff` lab then captured an injected 56-byte inbound
IPv6 ESP packet on a temporary Wintun adapter with byte-for-byte equality.
The `--windivert-send` lab used a SEND_ONLY handle to inject a synthetic
outbound ESP packet, and Wintun received the exact 56 bytes. These are native
Windows packet-I/O proofs. Real PPP inbound ESP and end-to-end
registration remain unverified. The temporary adapter, routes, addresses and
WinDivert driver service were removed after the tests. The C# userspace ESP
implementation remains the intended cryptographic layer; WinDivert is only a
candidate raw packet I/O layer.
The managed `WindowsHostImsWinDivertIo` was also exercised against a temporary
Wintun peer using the real WinDivert DLL ABI: the peer received its synthetic
outbound ESP byte-for-byte, and managed code received the peer's inbound ESP
byte-for-byte. The first run found and fixed an overly strict minimum-length
check on the raw packet boundary. The managed path is still explicit-only.
It subsequently sent one synthetic, intentionally invalid ESP packet through
the real, route-isolated IMS PPP; Windows `pktmon` independently captured its
exact marker on the protocol-50 path. This verifies outbound injection, not
carrier acceptance or inbound delivery. A separate local fake-P-CSCF test
also exercises the explicit user-space ESP registration branch: after the
401 challenge, authenticated REGISTER uses only the protected datagram path,
not plain UDP. No carrier REGISTER has been sent.

Do not enable the adapter in the product registration path yet. In particular:

- No SA installation or encrypted packet exchange has been validated on a
  carrier bearer. Loopback-only tests installed and removed each SA set
  successfully without transmitting packets. WFP rejected the
  earlier `UINT32_MAX` lifetime with `FWP_E_OUT_OF_BOUNDS`; the native and
  managed plans now use 3600 seconds, requiring refresh/rekey before expiry.
- The filters are constrained by IPv6 addresses, UDP, local protected port and
  interface LUID, but not P-CSCF remote port, which arrives in `Security-Server`
  after the SPI reservation. The resulting selector must be verified before use.
- WFP SA contexts cannot be created in a dynamic session. The filters now
  auto-expire with the dynamic session. In the isolated activated-loopback
  forced-exit test, the associated SA contexts disappeared too; however,
  ordinary release still deletes both explicitly. Recovery after partial
  creation, BFE failure, and real bearer loss remains unverified; startup
  reconciliation and a reliable cleanup owner are required before enabling it.
- Inbound ESP authentication and decryption now work in the isolated lab, but
  duplicate ESP delivery is an unresolved security failure. ESP on the actual
  IMS PPP bearer, network registration, and media verification are still
  missing. The USB Audio/AT call path remains the working fallback.
