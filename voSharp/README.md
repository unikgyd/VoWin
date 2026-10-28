# VoSharp

> **基于 .NET 10 / C# 的 Windows VoWiFi、IMS 电话、双通道 SMS 与 eUICC 实验性核心工程**
> **当前仍在安全与协议一致性修复阶段；部分路径为占位或仅通过离线测试，不能直接作为生产通信核心。**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-0078D6?logo=windows)](https://microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Kernel_tests-1684%20passed-success)](../publish/asterisk-render-v2-20261024/README.md)
[![License](https://img.shields.io/badge/License-See_repository-lightgrey)](../LICENSE)

---

2026-10-26：[Windows 桌面与 CLI 的 IKE 持久记录](../publish/windows-ike-owners-20261026/README.md)
已共用当前用户的 `%LOCALAPPDATA%/VoWin/preferences.db` 和同一 SQLite 适配。
独立 CLI 默认启用此 owner，`--client` 使用已有服务；没有新增清空／改库命令。
核心 1,798／桌面 685／IKE 274、关键三轮、真实独立进程和构建／发布核对通过，原跳过保留。
仅依赖系统 .NET；x64 CLI 验证文件夹约 6.76 MiB，不代表 VoWin 安装包体积或实卡验收。

2026-10-24：[双平台部署 v2 生成器与新配置能力](../publish/asterisk-render-v2-20261024/README.md)
接入来源 CIDR、本地 SHA-256、独立身份域及逐线路选线／国家号码，导入保持关闭。
Windows 全量核心 1684／桌面 651、关键集三轮及 Release 构建通过；Linux 实际 Config 消费与
Asterisk 隔离加载通过。未替换现用程序、操作实卡；新版管理网页和联合实网验收仍待完成。

2026-10-24：[Windows 部署 v2 与独立 SIP 权限](../publish/windows-deployment-v2-20261024/README.md)
已连接配置导入、保存和运行时：本地／peer 分离、逐线路付费名单、独立来电／短信接收，
并修复原卡回复号码与分机同名时的路由绕过。核心 1,574／桌面 626 通过，原 5／17 实机
跳过，169／224 项关键各三轮，Release 零警告／错误。尚未替换现用程序或做新实网验收；
生成器和管理网页的 v2 接线仍待完成。设置与私密导入格式见 [SIP 配置](docs/SIP_GATEWAY.md)。

## 📖 目录

2026-10-21 双向鉴权对齐：从 Linux Rust UIM 实现补入单层 `80` 响应封装和 `6Cxx` Le 修正，
同时向 Rust 回补严格字段长度、总长度兼容与 `9Fxx` 续读。两端共用 42 条合成响应，拒绝
截断、错误状态和异常 Kc 尾部；格式回退只重解析已有响应，不触发新的 AKA 请求。Linux 协议保持纯 Rust，
Windows 保持 C#，完整迁移范围见[双端对齐记录](../docs/CROSS_PLATFORM_PARITY.md)。

2026-10-21 Asterisk 互通对齐：真实 Asterisk 测试发现 Rust 未接受没有用户名的 Contact，
已修复其误挂断。VoSharp 的现有解析支持该形式，固定中继回归新增同类 401／407、完整
ACK／BYE 目标和双向 G.711 场景，24 项通过；本轮未改变 Windows 运行配置或生产中继。

固定中继进一步对齐：与 Rust 共用 21 条摘要挑战样例，严格拒绝重复／损坏字段，优先 SHA-256。
正常和迟到会话的 BYE 支持一次鉴权重试，仍共用原有 4 秒清理预算。ACK 超时及运营商已结束
时先回收本地会话和媒体，再做远端清理；阻塞挂断回调的回归验证本地状态不再滞留。
最新 SIP／G.711 相关 97 项通过、1 项真实 LAN 测试显式跳过，未替换桌面程序。

IMS 注册进一步对齐：Rust 新增原生 REGISTER 状态机，两端共用 5 组二进制 RES 摘要和
20 组挑战解析样例。VoSharp 增加 4096 字节／32 字段上限及严格控制字符／参数语法校验，
摘要计算在 finally 清理自己的 RES 副本、HA1 和计算缓冲区，不修改调用方的 RES。
Rust 的生产承载、安全数据面及运营商媒体仍未接入，详见
[Linux 注册核心范围](../SimAdmin-sip/docs/IMS_REGISTRATION.md)；这不是 Windows 会话已新增 rspauth 验证的声明。

ESP 数据面继续双向对齐：Rust 原生传输模式 ESP 与 Linux raw socket 已通过隔离注册链路，
两端共用 32 组独立 Node／OpenSSL 报文向量。VoSharp 在提交重放窗口前检查完整 UDP 长度、
校验和及外层 IP，拒绝损坏的已鉴权高序号报文而不影响后续正常包。Linux 的真实承载、
IKE／ePDG 和运营商媒体仍待接入，范围见[IMS ESP 说明](../SimAdmin-sip/docs/IMS_ESP.md)。

承载前置继续双向对齐：Linux 新增显式选卡的 ModemManager IMS 租约与不重叠清理保护，
两端共用 31 组地址和 CGCONTRDP 样例。VoSharp 不再把 `/64` 当成 IPv4，带前导零字节
按十进制解析，并拒绝损坏 CSV；AKA 续传限制为 8 次／4096 字节，畸形 6C 不重试。
相关 Windows 回归 439 项通过、0 跳过；真实 Linux 线路与后台集成仍待完成，见
[IMS 承载说明](../SimAdmin-sip/docs/IMS_BEARER.md)。

网络接入继续对齐：Rust 新增原生地址、独立路由表、按源地址和绑定接口的策略，以及
网络失效时关闭 ESP 的生命周期连接。VoSharp 的 QMI IMS 租约及正式 Windows 路由核验
同步拒绝环回、多播、link-local、mapped、自身等无效 P-CSCF；诊断候选仍可供注入的回环
协议测试使用，不作为真实路由授权。详见[Linux IMS 网络范围](../SimAdmin-sip/docs/IMS_NETWORK.md)。

选卡鉴权继续对齐：Rust 新增原生 QMI 端点与卡身份绑定、ISIM 优先和 USIM 明确回退。
VoSharp 修复 EF.ICCID 中间 F 被跳过、导致损坏身份被折叠为正常号码的边界；AKA 解析先
完成全部长度检查，再分配 RES／CK／IK，失败兼容分支不残留部分密钥数组。两端共用
12 条身份解码样例，相关 Windows 回归 493 项通过。Linux 后台注册与媒体仍待接入，
见[原生 AKA 范围](../SimAdmin-sip/docs/IMS_AKA.md)；本轮不替换现用 Windows 程序。

逐模组生命周期继续对齐：Linux 把承载、网络、卡鉴权、ESP 注册与续期接入独立任务，
并提供明确标注的实验注册界面。VoSharp 增加 `SipRegisterSession.CurrentResult`，在授权
过期或传输关闭后不返回在线；同时使用 UTC 与单调计时限制寿命，不让阻塞续期或墙钟
回拨延长状态。`LastResult` 保持历史语义，Windows Host IMS 改查实时值。相关回归 512 项
通过，范围见[线路管理说明](../SimAdmin-sip/docs/IMS_HOST_LIFECYCLE.md)，没有部署或替换桌面程序。

2026-10-19 ePDG 发现配置：新增 `EpdgUserConfiguration`，支持优先地址、补充发现 PLMN
与仅手动候选。VoWin 按 SIM 保存，并在下次连接应用；核心仍区分发现地址与认证身份。
同一 ICCID 的 AT / EF.IMSI 不同读数保留为候选，成功历史优先，不把一个 IMSI 的 EF_AD
MNC 长度套给另一个。详见 [ePDG 发现与多 IMSI 回退](docs/EPDG_DISCOVERY.md)。

2026-10-16 ePDG DNS 恢复更新：Windows 默认使用配置的系统 DNS，独立并行查询 A / AAAA、
每类限时 6 秒，以新查询避开本机解析缓存；一类超时 / 无记录不丢弃另一类的有效地址。
不自动更换 DNS 服务器、固定运营商 IP 或使用过期缓存。`EpdgDnsQueryResult` 增加解析器
名称与各记录类型的结果诊断；自定义 `IEpdgDnsResolver` 保持原有注入行为。
依据 [DnsQueryEx](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsqueryex)
与 [查询选项](https://learn.microsoft.com/en-us/windows/win32/dns/dns-constants)，异步原生
请求可取消，原生回调结束前保留请求 / 取消句柄与记录内存，不派生 PowerShell 子进程。
网络地址变化仅唤醒离线且仍启用的自动恢复，不改变已注册的其他实例。

离线全方案 1197 通过、22 显式跳过、0 失败；原生 DNS 实网查询单项通过。
COM10 EC20 / DITO 单模组实测注入首次 DNS 失败后，由同一生产恢复任务自动重新查询，
真实 IKE / IMS 注册成功并保持 DPD，测试清理获远端注销；不打开 COM6、不拨号或发短信。
这不等于自然隔夜故障已复测或其他运营商已通过。报告见
`publish/vowifi-dns-recovery-20261016/tests/ec20-dns-recovery-live.trx`。

- [1. 项目定位与核心理念](#1-项目定位与核心理念)
- [2. 系统整体架构](#2-系统整体架构)
- [3. 核心功能特性](#3-核心功能特性)
  - [3.1 VoWiFi / IKEv2 / 用户态 ESP 通信栈（零 WFP 依赖）](#31-vowifi--ikev2--用户态-esp-通信栈零-wfp-依赖)
  - [3.2 IMS / SIP / 语音呼叫与音频](#32-ims--sip--语音呼叫与音频)
  - [3.3 蜂窝与 IMS 双通道 SMS 短信](#33-蜂窝与-ims-双通道-sms-短信)
  - [3.4 多 SIM 卡槽与模组硬件池 (Modem Pool)](#34-多-sim-卡槽与模组硬件池-modem-pool)
  - [3.5 GSMA SGP.22 eSIM / eUICC 配置文件管理](#35-gsma-sgp22-esim--euicc-配置文件管理)
  - [3.6 强类型事件与 GUI / MVVM 接入设计](#36-强类型事件与-gui--mvvm-接入设计)
- [4. 工程项目结构与模块划分](#4-工程项目结构与模块划分)
- [5. 快速上手与运行指南](#5-快速上手与运行指南)
  - [5.1 编译与测试](#51-编译与测试)
  - [5.2 启动 CLI 控制台](#52-启动-cli-控制台)
  - [5.3 在 GUI (WPF/Avalonia/WinUI) 中作为核心集成](#53-在-gui-wpfavaloniawinui-中作为核心集成)
- [6. CLI 交互命令参考手册](#6-cli-交互命令参考手册)
- [7. 遵循的国际通信规范 (3GPP / RFC / GSMA)](#7-遵循的国际通信规范-3gpp--rfc--gsma)

---

## 1. 项目定位与核心理念

`VoSharp` 旨在为 Windows 提供以 C# 为主的自主通信核心；硬件和系统集成仍依赖串口、PC/SC、winmm 等 Windows 接口。

### 核心架构决策
1. **C# 控制面全自研**：IKEv2、EAP-AKA、SIP、SDP、RTP、SMS (RPDU/TPDU)、GSMA SGP.22 (ASN.1 BER-TLV) 全部使用现代 C# (.NET 10) 编写，控制流断点直打、内存安全、无黑盒。
2. **硬件 SIM 负责 AKA 鉴权运算**：物理 SIM 卡通过 4G 模组（Quectel EC25 / DJI 4G 等 AT 接口）或 PC/SC 智能卡读卡器进行 3GPP TS 31.102 鉴权（`AT+CCHO`/`+CGLA`、`AT+CSIM`、`winscard.dll`），零泄露根密钥 $K$。
3. **纯 C# 用户态 ESP 数据面（完全摆脱 WFP 内核与驱动依赖）**：
   - 彻底移除对 Windows Filtering Platform (WFP) 内核驱动与 IP Helper 路由安装的依赖；
   - 由自研的 `EspTunnel` 在用户态完成 RFC 4303 ESP 封装、AES-CBC 加解密、HMAC-SHA 完整性校验、Sequence Number 递增管理与 IPv4/UDP 协议编解码；
   - 与 `IkeTransport`（UDP 4500 / NAT-T）直接接驳，**原生支持每张 SIM 卡绑定独立的 SOCKS5 代理**，实现多卡槽多代理并发路由，完全规避内核 IPsec 栈对代理流量的劫持与冲突。
4. **GUI 就绪架构 (GUI-Ready Core)**：提供统一门面接口 `IVoKernel` 与细粒度强类型事件（`EventHandler<TEventArgs>`），GUI 前端（WPF、WinUI 3、Avalonia、MAUI、Blazor Desktop）可直接完成 MVVM 数据绑定与异步调用。

---

## 2. 系统整体架构

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│                      GUI 呈现层 (WPF / WinUI 3 / Avalonia / MAUI)                 │
│                                  或 VoSharp.Cli 交互式终端                        │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │ 订阅强类型 C# 事件 / 调用 IVoKernel 方法
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│                             VoSharp.Kernel (顶层门面)                             │
│       IVoKernel · VoKernel · ModemPool · ModemSlot · TelephonyStateMachine       │
├──────────────────────────────────────────────────────────────────────────────────┤
│                           VoSharp.Telephony (业务编排层)                          │
│        VoWifiManager  ·  ImsCallManager  ·  SmsService  ·  EmergencyService      │
│        └── 用户态 EspTunnel 数据面 (RFC 4303 纯 C# ESP 加解密 / IPv4/UDP 封包)    │
├──────────────────────────────────────────────────────────────────────────────────┤
│                             VoSharp.Sip (IMS / 会话层)                            │
│     SipRegisterSession · DigestAkav1Md5 · SecurityAgreement · SmsOverIms · Rtp   │
├──────────────────────────────────────────────────────────────────────────────────┤
│                        VoSharp.Ike (IKEv2 / EAP-AKA 协议栈)                      │
│     IkeSession · Dual-Mode EapAkaClient(Type 23/50) · IkeCrypto · IkeTransport   │
│     └── IkeTransport (UDP 4500 NAT-T 泵 · 原生支持独立 SOCKS5 代理链路)          │
├──────────────────────┬───────────────────────────┬───────────────────────────────┤
│   VoSharp.Euicc      │       VoSharp.Sim         │        VoSharp.Modem          │
│ GSMA SGP.22 eSIM 管理 │ 3GPP USIM / PC/SC 智能卡   │ Quectel EC25 / AT 会话 / URC  │
├──────────────────────┴───────────────────────────┴───────────────────────────────┤
│                           VoSharp.Crypto / VoSharp.Common                        │
│     Milenage · AES-CBC/CTR/GCM · HMAC-SHA256 · DH ModpGroup · AsyncEventBus      │
└──────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. 核心功能特性

### 3.1 VoWiFi / IKEv2 / 用户态 ESP 通信栈（零 WFP 依赖）
- **3GPP TS 23.003 ePDG 域名解析**：优先采用 USIM EF_AD 的 MNC 长度；未知时尝试合法的两/三位候选。保留 FQDN/IP/归属 PLMN 对应关系，端点切换时同步 IKE 与 SIP 身份域；按 SIM、出口配置与鉴权域隔离短期失败记录。支持静态/漫游静态、PLMN、PCO、位置候选、可注入 DNS 解析器及 IPv4/IPv6 选择策略，并逐项记录 DNS 结果/耗时。默认系统 A/AAAA 解析不随 SOCKS5h 自动转为代理 DNS；完整 NAPTR/SRV 漫游选择与桌面 CarrierConfig 自动获取尚未实现，不能据此宣称全运营商兼容。
- **纯 C# IKEv2 状态机（实验性）**：已有 `IKE_SA_INIT`、`IKE_AUTH` 与 CHILD_SA 代码，但 responder 身份验证、生命周期和真网互操作仍待完成。
- **双模式 EAP-AKA 认证**：
  - **RFC 4187 (EAP-AKA Type 23)**：默认用于现网 3GPP ePDG 接入；
  - **RFC 5448 (EAP-AKA' Type 50)**：支持 `AT_KDF` 协商与 SHA-256 KDF 密钥派生。
- **安全与密码学套件**：
  - 加密算法：AES-CBC-128 / 256、AES-GCM-128 / 256；
  - 完整性算法：HMAC-SHA256-128、HMAC-SHA1-96；
  - Diffie-Hellman：Group 14 (MODP-2048)、Group 19 (ECP-256)、Group 2 (MODP-1024)；
  - PRF / PRF+ 密钥派生引擎与 SKEYSEED 计算。
- **纯用户态 ESP 隧道 (`EspTunnel.cs`)**：
  - **无需 Windows 内核驱动，零 WFP 依赖**；
  - 在用户空间直接封装/解封装 RFC 4303 ESP 载荷，实施 AES 加解密与 HMAC 鉴权校验；
  - 自动构建与解析虚拟内部 IPv4/UDP 协议头，直接交由 `SipTransport` 与 `RtpSession`；
  - **原生 SOCKS5 代理多路复用**：流量走标准 UDP/TCP Socket 传输，每张卡可直连或经由不同 SOCKS5 节点独立转发，无任何系统内核路由冲突。
- **NAT 穿越与保活**：支持 UDP 500 到 4500 自动漂移、Non-ESP Marker 识别、NAT 探测哈希与 20 秒 Keepalive 保活。

### 3.2 IMS / SIP / 语音呼叫与音频
- **WireGuard 私网 SIP Gateway**：内置 Digest registrar 与 B2BUA，将 VoWiFi IMS 或 AT + Quectel USB Audio 基带通话桥接为 PCMA/PCMU SIP/RTP；支持多终端注册、来电分叉抢接、外拨和 DTMF，且默认拒绝公网/通配地址监听。配置与边界见 [SIP_GATEWAY.md](docs/SIP_GATEWAY.md)。
- **双路径媒体策略**：VoWiFi 可直接桥接 Windows 侧 IMS RTP；无 IMS 注册时回退到 `ATD`/`ATA` 与 USB Audio PCM。基带音频由独立 helper 经命名管道直连 SIP RTP，不经过电脑麦克风/扬声器。
- **严格限制 ADB 边界**：标准 Quectel UAC 模组不探测、不调用 ADB；只有 QDC507 固件实际启用缺失的 D4 路由时才使用 ADB 兼容层，PCM 始终通过 Windows USB Audio 传输。
- **蜂窝 Host IMS 前置探针与 Windows IMS 通话**：`host-ims probe` 只读检查 IMS APN、P-CSCF 与 Windows 接口；可选的受控 PPP 租约将已验证的物理模组 IMS 承载交给 Windows，结束后恢复内部 IMS。数据面为 C# 用户态 ESP 与受信任的 WinDivert 原始 I/O 组件；已有中国卡单线路 CLI 双向语音实证，双模组 WPF 同时注册也已验证。真实两线路经 Asterisk 媒体仍未验收，不能推广为全模组 / 全运营商可用。无 SIM 时探针只做硬件预检。设计见 [HOST_IMS.md](docs/HOST_IMS.md)。
- **3GPP TS 24.229 SIP 注册**：
  - 401 Unauthorized 挑战自动响应；
  - **Digest AKAv1-MD5 认证**：自动调用硬件 USIM 计算 RES 并生成响应摘要；
  - **3GPP Security Agreement (sec-agree)**：支持 `ipsec-3gpp` 安全联合协商与端口映射切换；
  - **VoWiFi 安全续期**：提出新的 SPI / 客户端端口、保持 Contact 服务端口；临时关联确认前保留旧事务路径，重传与响应绑定加密代次，限时销毁旧密钥。DITO 三次同会话续期、加速本地调度续期及注销 200 OK 已实测；新挑战时的关联轮换由 IPv4 / IPv6 报文测试覆盖，不声称实网重新鉴权或自然一小时续期已验收。Windows Host IMS 现有端点预约后端未因此自动获得完整新参数轮换能力；
  - **Service-Route 与 P-Associated-URI 自动注入**。
- **呼叫会话控制（部分实现）**：已有 `INVITE`、振铃、接听、拒接和挂断骨架；事务重传、ACK 激活、CANCEL/dialog 一致性仍在修复。
- **音频采集与编解码**：
  - 纯 C# RTP 会话调度与抖动缓冲；
  - G.711 PCMA (A-law) / PCMU (μ-law) 编解码；
  - DTMF 按键信号收发（RFC 4733）；
  - Windows 原生音频输出 (`winmm.dll`) 与通话 WAV 自动落盘录音。

### 3.3 蜂窝与 IMS 双通道 SMS 短信
- **双通道无缝支持**：
  - **IMS 短信通道 (SMS over IMS)**：基于 3GPP TS 24.341 / TS 24.011，通过 SIP `MESSAGE` 传输二进制 RPDU (RP-DATA / RP-ACK / RP-ERROR)；
  - **基带短信通道 (Cellular AT SMS)**：通过模组 AT 接口（`AT+CMGS`、`AT+CMGL`、`AT+CMGR`）收发 3GPP PDU 格式短信。
- **长短信自动拼接与分片**：内置 `SmsReassembler`，自动解析 8 位/16 位 Concatenation UDH，无缝拼接多片长短信。
- **状态报告与送达回执（部分实现）**：可解析 SMS-STATUS-REPORT；跨卡槽和 TP-MR 回绕下的可靠 Outbox 关联仍待修复。

### 3.4 多 SIM 卡槽与模组硬件池 (Modem Pool)
- **多模组卡槽抽象 (`ModemSlot`)**：每个卡槽封装独立的串口通信驱动、SIM 身份、USIM AKA 实例、VoWiFi 隧道与 IMS 呼叫管理器。
- **硬件池管理 (`ModemPool`)**：
  - 自动扫描与热插拔探测 Windows COM 串口；
  - 支持多卡槽枚举与主活动卡槽切换；独占租约、移除等待和公平并发仍待实现；
  - **独立 SOCKS5 代理路由**：允许为每个卡槽配置专属代理（`socks5://ip:port`），使 IKEv2/VoWiFi 隧道经由海外专用出口连接运营商 ePDG。

### 3.5 GSMA SGP.22 eSIM / eUICC 配置文件管理
- **SGP.22 eUICC 管理与下载**：ES10 本地档案管理，以及通过隔离 LPA Worker 完成 ES9+、SM-DP+ 证书认证、BPP 下载/安装和结果通知；VoWin 支持粘贴 `LPA:1` 激活链接或读取二维码图片，并在完成后重新读取 ICCID 核验：
  - 支持通过 4G 模组逻辑通道（`AT+CCHO`/`+CGLA`、`AT+CSIM`）管理板载 eUICC 芯片；
  - 支持通过 PC/SC 读卡器（`winscard.dll`）管理物理 eSIM 智能卡。
- **ASN.1 BER-TLV 编解码器**：高容错解析 `BF2D` (GetProfilesInfo)、`BF21` (ProfileInfo) 等复杂 ASN.1 数据结构。
- **Profile 生命周期控制**：查询 EID、获取 Profile 列表、激活/切换 Profile（Enable）、禁用 Profile（Disable）、删除 Profile（Delete）、修改 Profile 昵称（SetNickname）。

### 3.6 强类型事件与 GUI / MVVM 接入设计
为了方便后续扩展 GUI 应用（如 WPF / WinUI 3 / Avalonia / MAUI），内核提供了顶层抽象接口 **`IVoKernel`**，并在全链路暴露了标准的 C# 强类型事件（`event EventHandler<TEventArgs>`）：

| 事件分类 | 核心事件 | 说明 |
| :--- | :--- | :--- |
| **通话事件** | `IncomingCall`, `CallStateChanged`, `CallConnected`, `CallEnded`, `DtmfReceived` | 弹出来电窗口、状态流转、接通/挂断时长、DTMF 显示 |
| **短信事件** | `SmsReceived`, `SmsSent`, `SmsStatusReportReceived` | 新消息追加、发送结果反馈、运营商回执状态点亮 |
| **VoWiFi 状态** | `VoWifiStateChanged` | VoWiFi 连接中/已连接/失败状态指示灯 |
| **基带与信号** | `SignalQualityChanged`, `NetworkRegistrationChanged`, `SimStateChanged`, `FlightModeChanged` | 信号格数(0~5)、RSSI dBm、4G/5G制式、SIM就绪检测 |
| **多卡池事件** | `SlotAdded`, `SlotRemoved`, `ActiveSlotChanged`, `SlotStateChanged`, `SlotListChanged` | 多卡切换器更新、卡槽状态变化 |
| **eSIM 事件** | `EuiccProfilesUpdated`, `EuiccOperationCompleted` | eSIM Profile 列表刷新、切换/重命名完成通知 |
| **系统诊断** | `TelephonyStateChanged`, `LogEmitted`, `SystemErrorOccurred` | 全局状态机流转监控、日志与错误捕获 |

---

## 4. 工程项目结构与模块划分

解决方案包含 13 个源码项目与 2 个单元测试项目（共 15 个项目）：

```
voSharp/
├── voSharp.slnx                      # 统一解决方案定义文件
├── src/
│   ├── VoSharp.Common/               # 基础公共模块 (AsyncEventBus, 日志, 事件参数基类)
│   ├── VoSharp.Crypto/               # 密码学核心 (Milenage, AES, HMAC, SHA, DH, PRF/PRF+)
│   ├── VoSharp.Sim/                  # SIM/USIM 身份解析, PLMN, PC/SC winscard 智能卡驱动
│   ├── VoSharp.Modem/                # 模组串口通信驱动 (AT 会话引擎, URC 异步监听, CSIM/CGLA 硬件 AKA)
│   ├── VoSharp.Ike/                  # 纯 C# 全栈 IKEv2 / 双模 EAP-AKA (Type 23/50) 状态机与协议栈
│   ├── VoSharp.Sip/                  # IMS / SIP 协议栈, AKAv1-MD5, TS 24.229 sec-agree, SDP/RTP
│   ├── VoSharp.Telephony/            # 电话核心业务层 (VoWifiManager, EspTunnel 用户态数据面, ImsCallManager, SmsService, 音频)
│   ├── VoSharp.Euicc/                # GSMA SGP.22 eSIM 管理器, ASN.1 BER-TLV 编解码器, ISD-R 客户端
│   ├── VoSharp.StateMachine/         # 全局电话生命周期状态机与审计
│   ├── VoSharp.Kernel/               # 顶层核心引擎, IVoKernel 门面, ModemPool/ModemSlot 多卡硬件池
│   ├── VoSharp.Ipsec/                # 跨平台 SA 与密钥抽象契约
│   ├── VoSharp.Native/               # Windows 本地辅助 (strongSwan VICI 客户端, Wintun 驱动加载)
│   └── VoSharp.Cli/                  # 交互式命令行应用程序
└── tests/
    ├── VoSharp.Ike.Tests/            # IKEv2 编解码, DH, EAP-AKA 向量测试
    └── VoSharp.Tests/                # 综合单元测试 (AKA, SIP, SMS, 状态机, 多卡池, 强类型事件)
```

---

## 5. 快速上手与运行指南

### 5.1 编译与测试
- **开发环境要求**：
  - Windows 10 / 11 (x64 / ARM64)
  - .NET 10 SDK
  - Visual Studio 2026 或 VS Code (带 C# Dev Kit)

```powershell
# 1. 还原并编译整个解决方案
dotnet build voSharp.slnx

# 2. 运行所有单元测试；当前基线为 315 通过、0 失败
dotnet test voSharp.slnx
```

### 5.2 启动 CLI 控制台

```powershell
# 运行 CLI
dotnet run --project src/VoSharp.Cli
```

进入交互终端后，可使用 `help` 查看所有命令，例如快速挂载模组并建立 VoWiFi 会话：

```text
voSharp> attach COM3          # 连接 COM3 上的 4G 模组
voSharp> status               # 查看 SIM 卡 ICCID/IMSI 与网络信号
voSharp> vowifi start         # 发起 ePDG DNS 解析、EAP-AKA 鉴权并建立用户态 ESP 隧道与 IMS 注册
voSharp> call dial 10086      # 通过 VoWiFi 呼叫 10086
voSharp> call hangup          # 挂断当前通话并自动保存 WAV 录音
voSharp> sms list             # 查看收件箱短信
```

### 5.3 在 GUI (WPF/Avalonia/WinUI) 中作为核心集成

在 GUI 项目中引入 `VoSharp.Kernel`，注册并监听 `IVoKernel`：

```csharp
using VoSharp.Kernel;

public class MainWindowViewModel
{
    private readonly IVoKernel _kernel;

    public MainWindowViewModel()
    {
        // 初始化内核（亦可通过 Microsoft.Extensions.DependencyInjection 注入）
        _kernel = new VoKernel();

        // 1. 订阅来电事件 -> 触发 UI 弹窗
        _kernel.IncomingCall += (sender, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                ShowIncomingCallNotification(e.CallerNumber, e.DisplayName, e.IsVoWifi);
            });
        };

        // 2. 订阅呼叫状态变化
        _kernel.CallStateChanged += (sender, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                CurrentCallStatusText = $"通话状态: {e.NewState}, 编码: {e.Codec}";
            });
        };

        // 3. 订阅新短信到达
        _kernel.SmsReceived += (sender, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                MessagesList.Add(new ChatMessage(e.Message.SenderOrRecipient, e.Message.Text));
            });
        };

        // 4. 订阅信号质量与卡槽切换
        _kernel.SignalQualityChanged += (sender, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                SignalBars = e.Signal.Bars;
                SignalDbm = $"{e.Signal.RssiDbm} dBm ({e.Signal.Rat})";
            });
        };
    }

    public async Task StartCallAsync(string targetNumber)
    {
        await _kernel.ExecuteCommandAsync($"call dial {targetNumber}");
    }

    public async Task HangupCallAsync()
    {
        await _kernel.ExecuteCommandAsync("call hangup");
    }

    public async Task SendSmsAsync(string recipient, string content)
    {
        await _kernel.ExecuteCommandAsync($"sms send {recipient} {content}");
    }
}
```

---

## 6. CLI 交互命令参考手册

| 命令分类 | 命令格式 | 说明 |
| :--- | :--- | :--- |
| **基础与状态** | `status` | 输出当前全局状态机、SIM 卡信息、信号格数与 VoWiFi 隧道详情 |
| | `history` | 打印状态机迁移审计历史与每次状态切换原因 |
| | `mmi <code\>` | 执行 MMI 指令查询（如 `mmi *#06#` 查询 IMEI） |
| **基带与模组** | `attach <port\> [baud]` | 打开指定 COM 串口（如 `attach COM3`）并握手探测设备 |
| | `detach` | 断开当前模组串口连接 |
| | `at <command\>` | 向基带模组发送原始 AT 指令（如 `at AT+CSQ`） |
| | `aka [rand] [autn]` | 使用真实 USIM 卡或输入测试向量执行 3GPP AKA 鉴权运算 |
| **VoWiFi 与隧道** | `vowifi start [epdg]` | 启动 IKEv2 / EAP-AKA 鉴权，建立用户态 ESP 隧道并完成 IMS 注册 |
| | `vowifi stop` | 拆除 ESP 隧道并注销 IMS 会话 |
| | `vowifi diag` | 查看 VoWiFi 详细诊断（IKE SPI、EAP-AKA 向量、P-CSCF IP、密钥套件） |
| **电话与呼叫** | `call dial <number\>` | 发起 VoWiFi SIP 呼叫（如 `call dial 10086`） |
| | `call answer` | 接听当前正在振铃的来电 |
| | `call reject` | 拒接当前正在振铃的来电 |
| | `call hangup` | 挂断当前通话并将语音自动保存为 WAV 录音文件 |
| | `dtmf <digit\>` | 在活动通话中发送 DTMF 双音多频按键（如 `dtmf 1`） |
| **短信收发** | `sms send <to\> <text\>` | 发送短信（自动选择 IMS 优先或基带 PDU 模式，支持长短信分片） |
| | `sms list [unread|all]` | 列出收件箱/发件箱短信及运营商送达回执状态 |
| | `sms read <index\>` | 读取并解码指定索引的完整短信内容 |
| | `sms delete <index\>` | 删除 SIM 卡或模组存储中的短信 |
| **eSIM 管理** | `euicc list` | 列出 eUICC 芯片中的所有 eSIM Profile（ICCID、运营商名、状态） |
| | `euicc switch <iccid\>` | 切换/激活指定的 eSIM Profile |
| | `euicc disable <iccid\>` | 禁用指定的 eSIM Profile |
| | `euicc delete <iccid\>` | 删除指定的 eSIM Profile |
| | `euicc rename <iccid\> <name\>` | 重命名 eSIM Profile 昵称 |
| | `euicc info` | 获取 eUICC 芯片元数据与 EID |
| **多卡槽硬件池** | `pool list` | 列出多模组硬件池中所有卡槽及状态 |
| | `pool add <port\>` | 向硬件池中添加新的串口模组 |
| | `pool select <id\>` | 切换当前活动的默认主卡槽 |
| | `pool proxy <id\> <url\>` | 为指定卡槽绑定专属 SOCKS5 代理（如 `socks5://127.0.0.1:1080`） |

---

## 7. 遵循的国际通信规范 (3GPP / RFC / GSMA)

| 领域 / 协议 | 标准与 RFC 规范 | 在 VoSharp 中的实现说明 |
| :--- | :--- | :--- |
| **3GPP VoWiFi 接入** | **3GPP TS 23.402 / TS 24.302** | 非 3GPP 不受信任 Wi-Fi 接入 ePDG S2b 接口规格与 IKEv2 配置载荷 |
| **IKEv2 协议** | **RFC 7296** | 完整 IKEv2 协议与载荷编解码、密钥协商与 NAT-T (RFC 3948) |
| **ESP 协议** | **RFC 4303** | 纯 C# 用户态 ESP 封包/解包、AES-CBC 加解密与 HMAC-SHA 鉴权 |
| **EAP-AKA 鉴权** | **RFC 4187 / RFC 5448** | EAP-AKA (Type 23) 与 EAP-AKA' (Type 50 + SHA-256 KDF) 客户端 |
| **USIM 智能卡 APDU** | **3GPP TS 31.102 / ETSI TS 102 221** | `AUTHENTICATE` (GSM/3G Context)、`SELECT`、ISD-R 逻辑通道管理 |
| **IMS / SIP 注册** | **3GPP TS 24.229 / RFC 3261** | SIP REGISTER、401 挑战、`Digest AKAv1-MD5` (RFC 2617) 与 `sec-agree` (RFC 3329) |
| **IMS 语音呼叫** | **3GPP TS 24.229 / RFC 3550** | SIP INVITE 会话控制、SDP 媒体协商、RTP 实时传输与 G.711 编解码 |
| **SMS over IMS** | **3GPP TS 24.341 / TS 24.011** | SIP MESSAGE 承载 RPDU (RP-DATA/RP-ACK) 与 TPDU 分片重组 |
| **蜂窝 SMS PDU** | **3GPP TS 23.040 / TS 23.038** | 7-bit / 8-bit / UCS2 字符编码、UDH 长短信拼接与状态报告解析 |
| **eSIM / eUICC** | **GSMA SGP.22** | ASN.1 BER-TLV 解析、ISD-R 逻辑通道 APDU 交互与 Profile 生命周期管理 |

---

## 📄 许可证

本目录目前没有独立的 LICENSE；旧 README 的 MIT 标注没有随附文件支撑。
发布前需核对[仓库许可证文本](../LICENSE)与各第三方随附许可；此说明不更改或重新授予任何许可证。
