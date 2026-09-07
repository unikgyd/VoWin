# VoSharp

> **基于 .NET 10 / C# 的 Windows VoWiFi、IMS 电话、双通道 SMS 与 eUICC 实验性核心工程**
> **当前仍在安全与协议一致性修复阶段；部分路径为占位或仅通过离线测试，不能直接作为生产通信核心。**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-0078D6?logo=windows)](https://microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Tests-160%20passed-success)](#51-编译与测试)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## 📖 目录

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
- **3GPP TS 23.003 ePDG 域名解析**：根据 SIM 卡 IMSI（MCC/MNC）自动计算标准 FQDN（如 `epdg.epc.mnc000.mcc460.pub.3gppnetwork.org`）并查询 DNS。
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
- **3GPP TS 24.229 SIP 注册**：
  - 401 Unauthorized 挑战自动响应；
  - **Digest AKAv1-MD5 认证**：自动调用硬件 USIM 计算 RES 并生成响应摘要；
  - **3GPP Security Agreement (sec-agree)**：支持 `ipsec-3gpp` 安全联合协商与端口映射切换；
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

# 2. 运行所有单元测试；当前基线为 160 通过、0 失败
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

本项目基于 [MIT 许可证](LICENSE) 开源。
