# Windows Host IMS / Host VoLTE

## 目标

这里的 Host IMS 指蜂窝侧 IMS 真正终止在 Windows，而不是 VoWiFi，也不是通过 ADB 或 USB Audio 转发模组内已经建立的通话：

```text
LTE/5G 网络 ← IMS APN/PDN → Windows 网卡地址 → P-CSCF
                                      ↓
                         VoSharp AKA + SIP + IPsec + RTP
```

Windows 必须先拥有 IMS PDN 的本机地址、P-CSCF 和可用路由。仅能看到模组提供的 Internet/RNDIS NAT 网卡时，即使模组内部已经注册 IMS，Windows 也不能直接收发该 IMS PDN 的 SIP、ESP 和 RTP。

## 可借鉴的开源实现

[PhhIms / ims-for-wisdom](https://github.com/xuanyayi/ims-for-wisdom) 的蜂窝 IMS 路径是目前最直接的参考：Android 先向 Telephony/RIL 请求带 `NET_CAPABILITY_IMS` 的蜂窝网络，随后从该网络的 `LinkProperties` 读取本机地址和 P-CSCF，再进行 AKA、`Security-Client`/`Security-Server`、传输模式 IPsec、SIP 注册及 RTP。VoSharp 已经具备后半段的大部分协议能力，需要补的是 Windows 的 IMS bearer 获取与数据面。

该项目采用 GPL-2.0。VoWin 只参考其协议分层和状态机思路，不直接复制其实现代码，避免许可证污染。

## Windows bearer 优先级

1. **MBIM/WWAN 多 PDP context（首选）**：Windows WWAN 模型原生定义 `WwanContextTypeIms`，并支持多个 PDP context。模组及驱动必须把 IMS context 暴露成 Windows 可用的虚拟接口；某些特殊 PDP API还要求运营商元数据和应用白名单。
2. **QMI WDS 多 PDN**：若 USB 上存在应用可访问的 QMI control/data function，可建立 `ims` profile/data call，并从 WDS runtime settings/PCO 读取 P-CSCF。已有显式持有的独立 WDS CID/handle 租约：运行时 APN、地址或 P-CSCF 不合格即 Stop 回滚，释放时先 Stop 再释放 CID；仍需将 raw-IP/QMAP 数据流可靠映射到 Windows 网卡并验证 P-CSCF 路由，尚未接入自动注册。Windows Quectel 驱动占用 QMI 端点时不能与其并行抢占。
3. **独立 modem data port + PPP**：可作为少数旧模组的实验性后备，通过 `*99***<cid>#` 暴露 IMS PDP；IPv6、PCO、并发 Internet PDN 和驱动稳定性通常弱于 MBIM/QMI。
4. **RNDIS/ECM NAT**：只能看到模组路由后的 Internet 网络时不满足 Host IMS 条件，不能靠增加一条 Windows 静态路由解决。

以上路径均不需要 ADB。ADB/QDC507 音频兼容代码不属于 Host IMS 方案。

## 当前实现

当前已加入完全只读的前置探针：

```text
host-ims probe
```

它读取：

- `AT+CGDCONT?`：是否配置 IMS APN；
- `AT+CGACT?`：IMS context 是否激活；
- `AT+CGCONTRDP`：IMS 本机地址、网关、DNS 和 P-CSCF；
- `AT+QCFG="ims"`：Quectel IMS/MBN 配置状态；
- `AT+QCFG="usbnet"`：当前为 QMI/RmNet、ECM、MBIM 还是 RNDIS；
- Windows 蜂窝/模组网卡地址及只读路由表：IMS 地址是否真的归属于已连接的 Windows 蜂窝接口，且 `GetBestRoute2` 到 P-CSCF 的最佳路由是否仍使用该接口与 IMS 源地址；Wi-Fi、VPN 和其他普通网卡不参与候选判定。
- Windows 移动宽带候选网卡：即使未插卡也显示候选网卡及连接状态；这是硬件预检，不代表它与所选 COM 卡槽一一对应，更不代表 Windows 已取得 IMS PDN。

探针不会设置 APN、激活 context、修改 USB 模式或重启模组。诊断结果分为：

- `SimUnavailable`：当前没有插入/选中 SIM；QMI UIM 可在 AT 口未打开时确认无卡，WDS Profile List/Settings 可只读查询已配置的 `ims` APN；若 AT 口已打开则补充 USB 数据模式，未打开时不声称 AT 可用。跳过无意义的 `CGACT`/`CGCONTRDP` 动态查询，插卡或启用 eSIM Profile 后重试；

- `NotConfigured`：没有 IMS profile；
- `ConfiguredButInactive`：有 profile，但没有运行时 IMS 地址；
- `ModemInternalOnly`：模组内部 IMS PDN 已存在，但 Windows 不拥有该地址；
- `HostRoutable`：Windows 接口拥有 IMS 地址并取得 P-CSCF；只有具体候选的 Windows 路由核验通过时，才允许继续尝试 Host IMS 注册。

WPF 的普通“刷新状态”只查询射频/注册状态，不再隐式执行 `CFUN` 重载。插卡或切卡后可在“模组管理”明确选择“重载 SIM”；重载、飞行模式开启、蜂窝数据关闭及模组重启会先在本地终止旧 Host IMS 会话。

`HostRoutable` 的候选绑定必须来自**同一个活动 IMS context**：该 context 的本机地址确实出现在 Windows 网卡上，且与所选 P-CSCF 使用同一 IP 地址族。探针/UI 会列出 `CID: 本机地址 → P-CSCF` 候选及路由核验结果；未通过核验的候选不能注册。核验只证明 Windows 路由表选中的接口与源地址，并不证明 P-CSCF 真正可达、运营商接受注册，或数据包必定沿该 PDN 出站。

若 `CGACT` 明确报告已配置的 IMS context 未激活，模组拒绝 `CGCONTRDP` 时仍可判为 `ConfiguredButInactive`；若活动状态未知、IMS context 活动中或配置查询失败，则保持 `ProbeFailed`，不把缺失的动态参数当作可用地址。

“导出 IMS 诊断”也会包含这一节。

桌面界面可在 **系统状态 → 蜂窝 Host IMS 数据面** 点击“探测数据面”，直接查看 USB 网络模式、模组 IMS 设置、IMS Context、本机地址、P-CSCF 与 Windows 网卡映射。界面与 CLI 调用同一个只读探针，并会明确区分 `ModemInternalOnly` 和 `HostRoutable`。

`SipTransport` 支持绑定指定的本机 IMS bearer 地址。Host IMS 注册另外对该 UDP socket 设置 Windows `IP_UNICAST_IF` / `IPV6_UNICAST_IF`，并读回核对接口索引；若驱动拒绝或索引不一致则停止注册，不退回 Wi-Fi/VPN/默认 Internet 接口。它只改变当前 socket，不修改系统路由表；VoWiFi 等其他 SIP 传输仍使用原有接口选择。该能力只有在探针确认 Windows 真正拥有 IMS 地址和对应 P-CSCF 路由后才会使用。

通话媒体也需要同样的隔离。`RtpSession` 可为 RTP 和 RTCP 分别绑定明确的 IMS 本机地址，并将两个 Windows UDP socket 固定到同一个已核验接口；传入接口索引却未传入明确的本机地址时会拒绝创建。Host IMS 注册成功后，普通拨号和私网 SIP 网关可复用该注册会话发送 INVITE；同一卡槽的来电可响铃、接听，并通过蜂窝接口发送 RTP。VoWiFi 路径仍使用其原有 ESP 媒体发送器。外拨/来电在本地模拟 P-CSCF 上通过了 SIP 与 RTP 测试，**尚无插卡后的真实运营商通话验收**，不能据此宣称通用 Host VoLTE 已可用。

媒体协商只宣告当前能够双向编码/解码的 AMR-NB、PCMA 和 PCMU；AMR-WB 目前只有解码能力，不能作为双向通话编解码器。对端未提供可用音频地址、端口或编解码器时，来电回 488，外拨在确认 200 OK 后发送 BYE 并清理本地会话。RTCP 按 SDP 的 `a=rtcp` 目标地址/端口发送，不再假定与 RTP 端口相邻。

`HostImsRegistrationClient` 只接受探针给出的 `HostRoutable` 端点候选，把 SIP socket 绑定到该 IMS 本机地址，再复用现有 REGISTER 和自动刷新。`RegisterWithCardAsync` 优先从 ADF.ISIM 读取 EF.IMPI（`6F02`）、EF.DOMAIN（`6F03`）及第一条 EF.IMPU（`6F04`），在 ISIM 上执行 IMS AKA。单卡且 QMI UIM 能明确映射到活动物理槽时，先尝试 QMI 逻辑通道；若身份读取失败，才在 AKA 前改用 AT。选定 QMI 后不会因 AKA 失败而通过 AT 重放同一挑战。首次 SIP 发包前及每次 AKA 前仍经 AT 从当前卡读取 EF.ICCID 核对，防止 QMI/AT 指向不同卡。仅当 `AT+CUAD` 返回可解析的其他 3GPP UICC 应用 AID 且 ISIM 无法打开，才判定 ISIM 缺席；此时必须有可靠的归属 PLMN/MNC 长度、无 PLMN 冲突，并通过活动卡的 EF.ICCID 严格核对身份，才从 IMSI 派生临时 IMPI/IMPU 并在 USIM 上执行 AKA。USIM 分支会在网络挑战前预检 QMI 逻辑通道，成功则用 QMI AKA，否则保留 AT AKA；QMI AKA 失败不会重放到 AT。EF.ICCID 在每次 AKA 前复核；CUAD/ISIM 读取失败、MNC 长度不明或卡身份不符时均拒绝注册。ISIM 文件规则依据 [3GPP TS 31.103](https://www.etsi.org/deliver/etsi_ts/131100_131199/131103/18.01.00_60/ts_131103v180100p.pdf)，无 ISIM 时的身份派生依据 [3GPP TS 23.003](https://www.etsi.org/deliver/etsi_ts/123000_123099/123003/19.07.00_60/ts_123003v190700p.pdf)。

现已通过独立卡槽会话接入 **系统状态 → 蜂窝 Host IMS 数据面 → 实验性 Host IMS SIP 注册**，以及 CLI 的 `host-ims register|status|stop`。注册前会重新运行探针并验证所选端点仍存在；多候选时必须明确选择。用户主动停止且原卡/模组仍有效时，先结束 Host 通话，再发送与原 Contact 匹配的 `Expires: 0` REGISTER；若网络返回 IMS-AKA 401，再用同一张卡回答挑战，最多等待 10 秒。只有 200 OK 才报告远端注销已确认；超时或失败时仍关闭本地 socket，并提示远端绑定可能保留到有效期结束。换卡、eSIM 身份变化、拔模组或进程清理仅本地失效，不用新卡为旧绑定发送 BYE 或注销。此入口不会激活 IMS bearer；P-Access-Network-Info 的蜂窝小区值尚未采集，若运营商要求 `ipsec-3gpp`，当前 Host 数据面会明确失败，而不会报告成功。**REGISTER 成功仍不意味着真实 Host VoLTE 通话或 SMS over IMS 已可用。**

SIP 客户端响应会核对顶层 Via 的 `sent-by` 与原请求一致。Windows Host IMS 的直连 UDP socket 还只接收所选 P-CSCF 地址发来的包（允许源端口变化）；这是单 P-CSCF 模式的保守边界，不是加密认证，也不能代替尚未实现的蜂窝 IMS IPsec。若运营商从其他 P-CSCF 地址回包，当前会丢弃并表现为超时，需要在多地址/切换策略明确后再支持。

若 P-CSCF 在 401 挑战中要求 `sec-agree` 却没有提供 `Security-Server`，或者在首次 200 OK 中要求安全协商而本机尚未建立 SA，注册会明确失败；不会把未受保护的会话报告为已注册。

### 蜂窝 IMS IPsec 数据面状态

已按 [3GPP TS 33.203](https://www.etsi.org/deliver/etsi_ts/133200_133299/133203/18.00.00_60/ts_133203v180000p.pdf) 明确两组传输模式 SA 的方向：UE `port-c` ↔ P-CSCF `port-s` 使用 UE `spi-c` 入站、P-CSCF `spi-s` 出站；UE `port-s` ↔ P-CSCF `port-c` 使用 UE `spi-s` 入站、P-CSCF `spi-c` 出站。`HostImsSaPlan` 将已协商参数和 AKA CK/IK 展开为这两组 SA 的安装请求，使用独立密钥副本并在释放时清零。它只是可测试的映射，**还没有向 Windows 安装任何 SA**。

Windows [WFP 手动 SA 流程](https://learn.microsoft.com/en-us/windows/win32/fwp/manual-ipsec-sas)还需要受限的入/出站过滤器、SA context、入站 SPI 分配以及两组 SA 的安装与清理。IMS 的 UE SPI 必须在初始 REGISTER 中声明，因此 Windows 后端需要在发包前预留两组入站 SPI，再在收到 401 的 `Security-Server` 和 AKA CK/IK 后激活 SA；当前 `IChildSaBackend.InstallChildSaAsync` 的单阶段接口尚不足以完成此握手。完成并实测该后端之前，不会向运营商发送声称支持 `ipsec-3gpp` 的 Host IMS 注册。

直连 SIP 传输现可在同一 IMS 地址、P-CSCF 地址和已固定的 Windows 网卡上，初始 REGISTER 前绑定并保留受保护的 UE client/server 两个 UDP 端口，待 SA 激活后关闭初始 socket 并切换到这两个端口；回环测试覆盖迁移前后事务和从 server 端口回复来电请求。它仍需对应的 WFP SA 才能承载真实双向 IMS 信令。当前默认会话在收到需要 `ipsec-3gpp` 的挑战时提前失败。

Host 注册客户端现支持注入两阶段 `IHostImsSecurityBackend`：初始 REGISTER 前预留 UE SPI/端口，收到 401 并完成 AKA 后激活 SA，再切到双受保护端口，异常和失效时释放预留。模拟后端测试已覆盖此顺序和失败清理。**默认产品路径没有注入真实 WFP 后端**，不会发出 `Security-Client`，也不会把模拟后端测试当成加密数据面验收。

### 无卡 QDC507 实机预检（2026-09-28）

已接入同仓库 `qmi/qmiSharp` 的只读 QMI 优先路径。设置与 AT 口对应的
`VOWIN_QMI_ENDPOINT_COM6` 为真实 raw-QMI Win32 设备路径（`device:` 前缀）或本机
`tcp://127.0.0.1:<port>` QMI 代理后，附着时先核对 QMI/AT 的 IMEI；匹配后 SIM 在位、
IMEI/IMSI/ICCID、NAS 信号与驻网先走 QMI，服务失败、
响应不完整或无选中的蜂窝应用时回退 AT。UIM 卡状态会遍历多卡及每张卡的应用，
只采用 GW primary 索引指定的 SIM/USIM；若未选应用，不从其他卡推断当前卡可用。
Host IMS 探针还会先检查 WDS 已连接 context：
必须确认 APN 为 `ims`，并取到 IPv4 地址和 P-CSCF，才与 Windows 蜂窝网卡和到 P-CSCF
的源地址/路由核对；QMI 无法提供完整候选时继续原有只读 AT 探针。WDS profile index
不是 AT CID，界面会分别标注。此路径不会发起 WDS 数据会话，当前也不支持 QMI IPv6
IMS context。未配置 raw-QMI 端点时维持 AT 路径；把 COM6 当作 QMUX 设备会失败。

这台测试机上，Windows 识别到 `Quectel USB AT Port (COM6)`、`Quectel Wireless Ethernet Adapter`（驱动服务 `qcusbwwan`），并在移动宽带列表中显示物理接口“手机网络”。只读 AT 返回 `OK`、`+QSIMSTAT: 0,0`、`+QCFG: "usbnet",0`，移动宽带网卡当前未连接，IMS context 查询返回 `ERROR`。这证明 COM/驱动枚举可用，但**既不能证明也不能否定**插卡后的多 PDP/IMS 能力。`netsh mbn show interfaces` 的 `Additional PDP Context: No (Physical interface)` 描述当前条目是物理接口，不能单独当作固件“不支持多 PDP”的结论。

Microsoft 的[多 PDP 开发说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/mobilebroadband/developing-apps-using-multiple-pdp-contexts)要求设备固件/驱动能暴露多数据流；通过 Windows `ConnectivityManager.AcquireConnectionAsync` 申请专用 APN 还需要运营商授权的 `cellularDeviceControl` [受限能力](https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.connectivitymanager.acquireconnectionasync)。因此普通 WPF 进程不能把“调用 Windows API 激活 ims”当成已具备的后端。若现有 `qcusbwwan` 驱动不向应用开放额外 context，需另有获授权的 WWAN 路径、厂商 QMI WDS 接口或可独占的数据端口；这些都尚未在本机验证。

## 下一阶段

只有插卡后实机探针返回 USB 模式、IMS context 和接口映射，才能选择正确 bearer 后端。无卡时探针只做硬件/驱动预检，不能验证 IMS 注册能力：

1. 为 MBIM 或 QMI 实现 `IHostImsBearer`，保持 Internet 与 IMS 两个 context 并存；
2. 继续将来电、呼叫保持、短信及媒体安全策略统一到接入无关的 IMS UA；当前 Host 注册与普通 SIP 通话已接入卡槽编排；
3. 在真实 `HostRoutable` bearer 上验证不要求 `ipsec-3gpp` 的网络的 UDP SIP 注册；
4. 对要求 `ipsec-3gpp` 的网络接入 Windows WFP/IPsec SA，或在应用直接拥有 QMI raw-IP 数据面时复用现有用户态 ESP；
5. 实测 REGISTER、外拨、来电、专用承载/QoS、SMS over IMS 及 Internet PDN 并存。

在探针确认 `HostRoutable` 前，不应将 Host VoLTE 标为已实现。
