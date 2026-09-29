# VoWin SIP Gateway（WireGuard 私网桥接）

## 目标与边界

SIP Gateway 优先把 VoWin 已建立的 Windows 侧 VoWiFi IMS 会话终止在一个 B2BUA；IMS 不可用时，同一 B2BUA 自动回退到基带呼叫控制和 USB Audio PCM。两种线路都向 WireGuard 私网内的普通 SIP 客户端提供独立 SIP/RTP 会话：

```text
运营商 IMS ← ePDG/IPsec → VoSharp IMS UA ─┐
                                          ├→ VoWin B2BUA / PCM → SIP phone
运营商 VoLTE/GSM ← 基带 + USB UAC PCM ───┘       WireGuard 10.66.66.0/24
```

这不是透明 SIP proxy。运营商侧的 IMS 身份、安全协商、路由、AMR 与会话状态不会泄露给终端；终端侧只看到普通 Digest SIP 和 G.711 RTP。

首版实现范围：

- UDP SIP registrar，支持一个或多个分机账号；
- RFC Digest MD5/qop=auth、nonce 时效和 `nc` 防重放；
- REGISTER、OPTIONS、INVITE、ACK、CANCEL、BYE、INFO；
- 发给软电话的 INVITE 最终 `200 OK` 会在收到匹配的 ACK 前重传；32 秒仍无 ACK 时清理运营商通话，避免悬挂线路；
- SIP 客户端外拨，经号码规则或 `X-VoWin-Line` 选择 VoWiFi 卡槽；
- IMS 来电同时呼叫所有有效注册终端，首个 200 OK 获胜；未接通分支发送 CANCEL，已接通或迟到的 200 OK 分支则 ACK 后 BYE；
- IMS AMR-NB/PCMA/PCMU 与终端 PCMA/PCMU 之间通过 8 kHz PCM 桥接；
- 没有 VoWiFi 注册时，可通过 ATD/ATA + Quectel USB Audio Class 将基带 VoLTE/GSM 的 8 kHz PCM 直接接入同一个 RTP leg；
- RTP、RTCP 与 SIP socket 均只绑定配置的 WireGuard/私网地址；
- SIP INFO `application/dtmf-relay` 转发 DTMF。

当前不包含：TLS/SRTP、WebRTC/PushKit、会议和呼叫转移。标准 Quectel UAC 固件不需要 ADB；QDC507 固件拒绝 `AT+QPCMV` 且自身缺失可用的 D4 路由，因此仍需用 ADB 在模块 Linux 内加载/启动 UAC 路由，但通话 PCM 始终走 Windows USB Audio，不经过 ADB。这个兼容层不是 Host VoLTE。

VoWiFi、SIP 网关、飞行模式和蜂窝数据现在是相互独立的状态：启动 VoWiFi/SIP 网关不会写 `CFUN` 或 `CGATT`，因此飞行模式关闭且蜂窝数据开启时，Windows 的 4G/5G 数据接口可以继续供网。切换 eSIM Profile 必须让基带重新加载 SIM，期间数据连接会短暂中断；新 ICCID 验证完成后才恢复该卡保存的飞行模式、蜂窝数据、漫游及 VoWiFi 偏好。

## 使用方法

1. 在网关主机和手机上配置 WireGuard，例如网关 `10.66.66.1`、手机 `10.66.66.2`。
2. 优先让目标卡槽完成 VoWiFi/IMS 注册；未注册时，网关会回退到模块基带通话及 USB Audio PCM。
3. 打开“设置 → WireGuard SIP 网关”，填写：
   - WireGuard 本机地址：`10.66.66.1`
   - SIP UDP 端口：`5060`
   - 分机：`1001`
   - 强密码：仅保存在当前进程，不写磁盘
4. 点击“启动 SIP 网关”。
5. 在 Linphone、Zoiper 或其他 SIP UA 中配置：
   - Server/Domain：`10.66.66.1`
   - Transport：UDP
   - Username/Auth ID：`1001`
   - Password：VoWin 中输入的密码
   - 不需要 STUN、TURN 或 outbound proxy

CLI 也可启动：

```text
sip-gateway start --bind 10.66.66.1 --user 1001 --password <strong-password> --port 5060
sip-gateway status
sip-gateway stop
```

多卡时，SIP INVITE 可增加 `X-VoWin-Line: <slot-id>`；未指定时沿用 VoWin 的号码前缀选卡和当前活动卡槽规则。

通话状态按卡槽和真实 Call-ID 跟踪，一张卡结束不会清空另一张卡的会话或桌面音频。当前 Windows USB UAC 端点选择与 PCM 桥仍是进程级资源，蜂窝 SIP 桥同一时刻只允许占用一条线路；要并行桥接多个蜂窝模组，仍需实现并验证每个模组独立的音频端点映射。

## 安全约束

- 默认拒绝 `0.0.0.0`、`::` 和公网地址绑定；只有显式启用 `AllowPublicBind` 才能绕过公网地址检查。
- SDP 的媒体地址默认不可信，RTP 总是回到 SIP 数据包的实际源 IP，避免把网关变成 UDP 放大器。
- 不要把 UDP 5060 或 RTP 临时端口映射到公网。WireGuard 是公网侧的认证与加密边界。
- SIP 密码不会由 WPF 设置页持久化。重新启动 VoWin 后必须再次输入并启动网关。
- iOS 可能暂停后台 SIP 应用；产品化需要 APNs/CallKit 唤醒，这不属于首版网关。

## 关键代码

- `VoSharp.Kernel/SipGateway/SipGateway.cs`：registrar、B2BUA、分叉和对话生命周期；
- `SipDigestAuthenticator.cs`：SIP Digest 与防重放；
- `SipGatewaySdp.cs`：安全 SDP 解析和 G.711 offer/answer；
- `ImsCallManager.cs` / `RtpSession.cs`：运营商 RTP 解码后的 PCM 接口与本地 RTP leg。
- `VoWin/Services/CellularAudioBridge.cs`：独立 UAC helper 与双向命名管道 PCM，避免占用 PC 麦克风/扬声器。

## 验证

```powershell
dotnet test voSharp/tests/VoSharp.Tests/VoSharp.Tests.csproj --filter FullyQualifiedName~SipGatewayTests
dotnet build VoWin/VoWin.csproj
```

真实网络验收还应覆盖：外拨双向语音、来电多终端抢接、未接超时、手机主动挂断、运营商主动 BYE、DTMF，以及 WireGuard 重连后重新 REGISTER。
