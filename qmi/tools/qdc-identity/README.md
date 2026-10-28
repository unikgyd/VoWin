# QDC507 read-only identity helper

This 3,660-byte ARM EABI executable is embedded in VoWin, including ordinary source
builds. It is not a Linux runtime, driver, SIP stack or audio bridge. It dynamically
uses the modem's existing glibc and QCCI libraries. No firmware libraries are shipped.
Source is under the repository's GPL-3.0 license; the public ABI is referenced, not copied.

Only DMS Get Device Serial Numbers (`0x25`, empty request) is sent. No NAS, WMS,
VOICE, IMS or configuration operation exists in the program. A QMI client is opened
and released for each query, with bounded timeouts and an eight-second process alarm.
Response TLVs must contain one successful result and one exactly 15-digit IMEI.

The supported firmware is Linux 3.18.44 / MDM9207 or MDM9607, root ADB, ARM soft-float,
glibc loader `/lib/ld-linux.so.3`, and DMS IDL accessor version 1/57/6. Other ABIs fail
closed. With no arguments it returns `VOWIN_IMEI=...` for internal pairing only.
`--expect <IMEI>` returns 0 on a match, 71 on mismatch, and never prints the identity.
Other failures return 64–70; timeout is also a failure.

Windows enumerates `host:devices-l`, selects a specific nonzero uint64 transport ID,
verifies hardware/root, uploads only this helper to `/tmp/vowin-call` if necessary,
and compares its IMEI with the selected AT/QMI slot's cached IMEI. Before any route
command, firmware, helper hash and IMEI are checked **in the same remote shell**.
Serial `?`, list order and a previously cached transport ID are never identity proof.
Runtime payload uploads are inert files; executable route changes always require
the on-device guard. A missing or ambiguous match is an error, not a fallback.

Build on Windows with LLVM clang/lld, using read-only copies of the owned target's
`libc.so.6` and `libdl.so.2` in a private link directory:

```powershell
./Build-Identity.ps1 -SysrootDirectory <target-link-directory> -Clang <clang.exe>
```

The checked-in binary allows CI and .NET source builds without an ARM SDK. Rebuilt
bytes must match the length/SHA-256 pinned in `Qdc507AdbIdentity.cs`, or intentionally
update that pin after review and explicit hardware tests. Never ship the link directory.

The optional `Lab=QdcIdentity` desktop test requires
`VOWIN_TEST_QDC_IDENTITY_PORT=<selected AT port>`. It can deploy the tiny helper to
temporary storage, reads the selected Windows QMI IMEI, and checks both match and
mismatch guards without changing routes/radio configuration or calling/sending SMS.
Current evidence is one QDC507; two physical anonymous modems remain unverified.

Protocol references: [AOSP transport-ID selection](https://android.googlesource.com/platform/packages/modules/adb/+/ec7028fb5c2cde5c18522dd08f7c34c5e668fc83/client/adb_client.cpp),
[Qualcomm public QCCI ABI](https://github.com/qualcomm/qmi-framework/blob/main/include/qmi_cci.h),
[Osmocom DMS identity experiment](https://github.com/osmocom/quectel-experiments/blob/master/qmi/qmi_test.c).
