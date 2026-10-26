# VoWin: Windows Multi-Line Communications Gateway

**English** | [简体中文](README.zh-CN.md)

[![Build and release VoWin](https://github.com/unikgyd/VoWin/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/unikgyd/VoWin/actions/workflows/release.yml)

<p align="center"><img src="VoWin/Assets/vowin.png" alt="VoWin logo" width="128" /></p>

VoWin is a Windows desktop client for SIM/eSIM management, modem voice, Host IMS,
VoWiFi, and SIP gateway services. The Linux client lives in the separate
[VoCat-sip](https://github.com/unikgyd/VoCat-sip) repository (Go + React).
Linux development follows the mature VoWin behavior while retaining VoCat's
existing device, networking, scheduled-task, and notification capabilities.

## Cross-platform progress

| Area | Current development status |
| --- | --- |
| Windows | VoWin/VoSharp (C#) remains the Windows client. The current Linux parity work does not change Windows runtime code. |
| Linux calls and media | Web phone, physically paired USB PCM, SIP extension and optional Asterisk peer bridges, and RFC 4733 DTMF are wired. |
| Multiple modems and recovery | Per-modem control, Host IMS, per-SIM ePDG/retry history, and separate call/media ownership are present. |
| Independent cellular/Wi-Fi access | Per-ICCID connection preferences, default routing, SIM-change recovery, and management UI are being integrated. The latest source has not had final combined validation. |
| Remaining capabilities | Hold/resume, auto-answer and message playback, SIP INFO compatibility, complete hardware lifecycle, and live carrier acceptance remain pending. |

See the [VoCat feature union checklist](https://github.com/unikgyd/VoCat-sip/blob/master/docs/FEATURE_UNION.md)
for the full comparison. Historical passing runs apply to their corresponding
source snapshots; they do not establish acceptance of the current worktree or
complete cross-platform parity. The latest checkpoint is development work,
not a new release, and does not replace installed applications.

## Features

- Independent modem management: SIM/eSIM information, SMS, calls, and cellular/Wi-Fi status.
- USB modem audio, Host IMS, and VoWiFi services, subject to device firmware, SIM, and carrier support.
- Desktop phone, incoming-call windows, SMS conversations, verification-code queries, and on-demand diagnostics.
- Optional SIP server, extension ringing groups, mobile SMS bridging, and Asterisk peer integration.
- eSIM profile management, ePDG discovery and manual configuration, proxy routing, remote control, and notifications.

Successful registration alone does not establish working call audio, confirmed
SMS delivery, or compatibility with every carrier.

## Installation and first use

1. Use Windows 10/11 x64 and install **.NET 10 Desktop Runtime x64**.
2. Install the appropriate vendor-signed modem drivers and confirm AT/QMI/audio interfaces in Device Manager.
3. Download a package from [Releases](https://github.com/unikgyd/VoWin/releases) and run `VoWin.exe`.
4. Add a modem, verify its SIM identity, and configure cellular, Host IMS, VoWiFi, SIP, or remote notifications as needed.

Communications and audio require the corresponding hardware interfaces. eSIM
management requires an eUICC and an available APDU channel. Follow the requirements
and installation instructions for the specific release package.

## Source and repositories

| Path or repository | Purpose |
| --- | --- |
| `VoWin/` | WPF desktop UI, application services, settings, and local storage. |
| `voSharp/` | SIM, modem, IKE, IMS, SIP, SMS, and call protocols. |
| `qmi/` | QMI communications and device support. |
| [VoCat-sip](https://github.com/unikgyd/VoCat-sip) | Separate Linux repository; its default branch is `master`. |

VoWin uses `main`. Local `VoCat-sip/` and `SimAdmin-sip/` directories are independent
repositories, rather than implicit VoWin submodules. SimAdmin remains a reference;
VoCat-sip is the current Linux development target. Build outputs, test results,
runtime databases, logs, local credentials, and device diagnostic archives are
excluded from source commits.

Development requires the .NET 10 SDK. Packaging scripts also require PowerShell 7.

```powershell
dotnet restore VoWin.slnx
dotnet build VoWin.slnx --no-restore
dotnet run --project VoWin/VoWin.csproj
```

The current workflow completes implementation before running the final combined
tests. No tests or builds were run during the README/Git cleanup.

## Community and licensing

- [GitHub Issues](https://github.com/unikgyd/VoWin/issues)
- [Telegram community](https://t.me/VoWindows1)
- [X / Twitter @Unikgyd99](https://x.com/Unikgyd99)

Use and redistribution are governed by this repository's [LICENSE](LICENSE) and
its dependencies' licenses. VoCat-sip has a separate research and evaluation
license, which cross-platform integration does not change. Use the projects for
lawful, authorized communications research and testing. Existing license,
regional-admission, and authorization checks remain in force.

Thanks to VoHive, VoCat, and mdd-sim-gateway for their reference implementations.

## Support the project

| Asset and network | Address |
| --- | --- |
| USDT · TRON (TRC-20) | `TDecoS5mFozuyfJSR2QhSsWbCkGm8nsvpH` |
| USDT · BNB Smart Chain (BEP-20) | `0x0e5e07b7604d8585fd936527f48874a10ad8b884` |
| USDT · Polygon | `0x0e5e07b7604d8585fd936527f48874a10ad8b884` |

![VoWin interface](docs/images/页面.png)
