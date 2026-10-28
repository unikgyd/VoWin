# VoWin SIP Gateway（可选 SIP / Asterisk 对接）

## 部署 v2 导入与独立权限

Windows 支持下述**私密导入文件**；它不是 `deployment_v2.py` 的公开秘密引用模型。
新版[双平台生成器](../../publish/asterisk-render-v2-20261024/README.md)已接通；
[Asterisk v2 管理页](../../packaging/asterisk/admin/README.md)支持私密草稿及默认关闭的受控应用／恢复。
旧共享凭据输出不会自动变成独立角色凭据，管理页应用不等于 Windows 自动导入或线路启用。
导入只保存关闭草稿，不会开监听、连接 PBX、启用模组或产生费用。软件证据与尚未验收的
真实 WPF／Asterisk／运营商边界见[本阶段记录](../../publish/windows-deployment-v2-20261024/README.md)。

```json
{
  "Version": 2,
  "Settings": {
    "CredentialVersion": 2,
    "BindAddress": "127.0.0.1", "Port": 5062, "Realm": "gateway.example",
    "LocalRegistrarEnabled": true, "Extension": "desk",
    "UseTrunk": true, "ServerAddress": "127.0.0.2", "ServerPort": 5060,
    "TrunkUsername": "pbx-to-gateway", "IncomingTarget": "600",
    "PeerInboundAlgorithm": "SHA-256",
    "PeerOutboundUsername": "gateway-to-pbx", "PeerExpectedRealm": "pbx.example",
    "PeerAllowedAlgorithms": ["SHA-256"],
    "AllowOutboundCalls": false, "AllowOutgoingSms": false,
    "PeerAllowOutboundCalls": false, "PeerAllowOutboundMessages": false,
    "PeerAllowIncomingCalls": false, "PeerAllowIncomingMessages": false,
    "PeerOutgoingLines": [], "PeerSmsSendLines": [],
    "PrimaryPolicy": {
      "AllowCalls": false, "AllowSms": false,
      "AllowInternalCalls": false, "AllowMessages": false,
      "OutgoingLines": [], "SmsSendLines": []
    },
    "LineRoutes": {}
  },
  "Credentials": {
    "MainPassword": "REPLACE-LOCAL-SECRET", "Extensions": {},
    "PeerInboundPassword": "REPLACE-INBOUND-SECRET",
    "PeerOutboundPassword": "REPLACE-OUTBOUND-SECRET"
  }
}
```

示例全部业务授权关闭，地址仅为回环占位，必须换成自己的明确私网／VPN 端点、Realm 和强密码。
`Credentials.Extensions` 的键必须与附加本地账号精确对应，不能夹带主账号或未知账号。
关闭某个角色后不能携带其未使用的非空密码。账号字符串或密码值可以相同，但角色必须分别提供，
不会相互推断。原文件含明文凭据，应私密保存；持久启动凭据由当前 Windows 用户 DPAPI 保护。

最大输入 64 KiB，严格 UTF-8，拒绝递归未知字段、重复／转义后重复／大小写别名、错误类型和
不完整认证。失败不改当前设置；成功清除文件的启用标记、旧迁移路径及旧接收代际。
旧无 Version 公开文件仍可导入为关闭草稿，但不能通过被 JsonIgnore 的属性偷偷填写新 peer 密码。

权限独立叠加：全局总开关、模组开关、实际已认证主体、最终线路名单全部通过才允许收费操作。
主账号的 `PrimaryPolicy` 与附加账号 `Policy` 都使用 `AllowCalls`／`AllowSms`（收费）、
`AllowInternalCalls`／`AllowMessages`（内部，双方均须许可）、`OutgoingLines`／`SmsSendLines`。
peer 单独使用上述 `Peer…` 设置，不借用同名本地账号权限或默认线路。

每条 `LineRoutes` 可设 `LocalIncomingExtensions`、`LocalSmsRecipients` 两份独立本地名单，
以及 `ForwardCallsToPeer`、`ForwardSmsToPeer`。明确开启 peer 转发须有 peer 和该线路明确的
`IncomingTarget`／`SmsTarget`；还受 peer 对应入站开关限制。短信发送权限不是收件授权。
名单 `null` 保留旧规则，`[]` 表示无人／无线路，不把未知项丢掉或自动换卡；新列表均深拷贝。
旧无主策略时保留全局收费权限语义，显式禁短信策略不会在保存时消失。

设置中的高级区可编辑这些权限。付费线路文本 `*` 表示不限制、空白禁止；本地收件名单的
`*` 表示沿用旧单目标／全部规则。peer 三态灰色保留旧设置。改变规则前先停止 SIP。
公共 options 的地址只写字符串诊断投影，不是可导入的凭据快照，也不替代 `DeepClone()`。

## Linux Go／Rust 参考与 C# 对照

当前 Linux 功能整合主线是 Go 的 VoCat-sip，Windows 继续使用 C#；SimAdmin-sip 的
Rust 协议保留参考。两端通过协议契约与样例对齐，而非要求同一语言或 UI，也不以
.NET 作为 Linux 运行依赖。Go 的逐线模式与 Host IMS 业务已接入，真实联合验收仍待
完成；固定 Asterisk 中继的[第一阶段已实现](../../VoCat-sip/docs/ASTERISK_RELAY_PLAN.md)，
不能把本地分机注册成功当成中继可用。另见
[Go 对齐状态](../../VoCat-sip/docs/VOWIN_PARITY.md)与
[Rust 参考说明](../../SimAdmin-sip/protocol/README.md)。
`VoSharp.GatewayHost` 的私有 NDJSON 组件保留为此前比较原型，不再是 Linux 发布依赖。

G.711 会话不初始化 AMR。Linux AMR 解析分别使用系统 AMR-NB／WB 库，不提取 Windows DLL；
缺少 WB 不再影响可用的 NB。AMR 编解码可用本身不等于 Linux 运营商承载或音频绑定已经完成。

## 独立分机与 SIP MESSAGE

直连 registrar 不再仅向运营商转发呼叫。已配置分机目标在内部优先解析，多联系人同时振铃，
第一条 200 分支获得通话，其他分支 CANCEL，迟到 200 则 ACK / BYE。两条独立 SIP / RTP
腿在网关转发 PCM，不使用 SIM、Windows 麦克风或模组媒体。BYE / CANCEL / ACK 超时
清理对应分机腿，不挂断其他模组；未注册的本地分机返回 480，不转运营商。

`RequireConfiguredLines=true` 可以在没有模组规则时启动纯分机服务器，但运营商呼叫 /
短信仍不能通过缺失的线路。主账号兼容原配置，附加账号独立密码、权限与默认线路；
REGISTER 支持查询当前联系人、通配注销和联系人数量限制，OPTIONS 返回能力信息。

`MessagesEnabled`、`ForwardIncomingSms`、`AllowOutgoingSms` 默认关闭。模组规则增加
独立的 `ForwardIncomingSms` / `AllowOutgoingSms` / `SmsTarget`；固定中继另检查
`AllowOutboundMessages`，附加分机检查 `SipExtensionPolicy.AllowSms`。内网文本不经过 SIM。
`ISipGatewayMessageController` 复用内核现有 Host IMS / VoWiFi / AT 短信接口，绑定原线路，
不因 UI 切卡或一条线路不可用而换用另一张卡。

可选的 `ISipGatewayIdempotentMessageController` 接收由认证 SIP 事务生成的稳定操作 ID；
Rust 原生实现使用相同的 `contracts/sip-sms-operation-v1.json` 校验身份，
Linux Rust 在提交前保存保护记录，重建组件或恢复新版备份后也不重放未知提交。
Windows 桌面现已把实际 SQLite 准入存储接到原控制器：发送前提交 Unknown，占位跨
监听器／进程重建保留，未知结果不自动重放；缺存储时核心拒绝收费发送。
这是 24 小时有界保护，不是永久恰好一次或手机送达，详见
[Windows SIP 短信持久防重](SIP_SMS_DURABILITY.md)。

运营商 IMS 入站 CANCEL 也已收紧原事务身份：区分大小写的 Call-ID、原 URI／From／To、
CSeq 与 Via 分支／sent-by；无效请求不会结束来电。回复 await 后再次核对原会话，
避免旧取消回调清掉替换来电；先释放旧媒体再通知观察者。这是定向生命周期修复，
不是所有呼叫状态机均已串行化或实卡验收的声明，见
[专项失败与验证记录](../../publish/vowin-ims-cancel-20261024/README.md)。

取消未接通的呼叫时，共享核心先清除本地对话，再发布 487 并清理原载波腿；慢挂断不再
阻塞本地空闲状态。载波迟到成功仍由同一清理标记处理，不会挂断另一条线路。

仅接收 UTF-8 / ASCII `text/plain`，正文上限 1024 字节；不解析运营商二进制短信 PDU、
CPIM、附件或压缩内容。按认证账号、Call-ID、CSeq 及内容摘要去重，重用事务但改变内容
时拒绝。逐模组串行提交、逐账号限速；部分成功不自动重试。外发文本按 Unicode 字符边界
拆分，UDP MESSAGE 最大 1300 字节，同目标消息串行，符合
[RFC 3428 §8](https://www.rfc-editor.org/rfc/rfc3428.html#section-8) 的尺寸及拥塞约束。

收到的短信只在用户开启转发后发送给已配置目标，原收件箱及桌面通知不变。离线不排队；
转发失败只记录类别，不记录正文 / 号码。SIP 202 是提交或中继接收确认，不能当作手机送达。
WPF 会保存实际短信提交结果，Asterisk 先返回自己的接收确认，最终状态应查 VoWin 历史。
凭据仍用 DPAPI；新增默认空配置字段省略序列化，以兼容原偏好指纹和旧版加密凭据。

响应除匹配来源、分支、Call-ID 和 From 标签外，还拒绝重复 To 标签；已有对话（例如
BYE）的响应必须保留请求里的 To 标签，按大小写敏感比较。初始事务可建立新 To 标签，
不能把这一例外套用到已建立对话。本项与 Linux SIP 内线开发互相对照后独立修复。

数字发送方的转发消息保留原号码显示，SIP From URI 使用
`sms-<原模组 ID 的稳定摘要>-<号码>` 回复地址。同一发送方发到不同 SIM 时，直接回复
仍绑定原线路；先认证，再检查原线路及收发权限。线路移除、关闭或显式选线冲突时拒绝，
不退回默认卡。非数字发送方不转换为可外发短信的号码。Asterisk 保留该 URI 原样路由；
客户端必须保留回复地址，不能只提取显示号码重新提交。

`SipDialRule` 提供可配置前缀、长度、删除前缀、补号和固定模组；显式选线优先于规则。
部署与故障排查见 [可配置教程](../../packaging/asterisk/README.md)。本轮自动化验证使用
合成运营商控制器与隔离 Asterisk，实际 SIM 短信及手机客户端消息展示单独验收。

## 2026-10-16：外呼取消与建立耗时

SIP 分机取消不再直接丢弃运营商 INVITE 的读取任务：本地通话状态及时结束，独立发送
并重传 CANCEL，最多保留 32 秒取消收尾，期间的迟到 200 执行 ACK / BYE；失败最终响应
及重复响应也发送 ACK。异常路径清空 ActiveCall 前必须发出终态和 CallEnded，防止内核 /
桌面残留一个已经无法挂断的拨号。这里的收尾期限不是 UI 等待期限。

网关立即向已取消的分机 INVITE 返回 487，不等待载波 Dial 返回；尚未接通时，桌面挂断
同样结束原 SIP INVITE，不能向未建立的对话发 BYE。呼叫在 Dialing 时即绑定原运营商
Call-ID / 模组，接受与取消争用一次最终响应；另一模组的通话不参与清理。
协议依据：[RFC 3261 §9](https://www.rfc-editor.org/rfc/rfc3261.html#section-9)、
[§13](https://www.rfc-editor.org/rfc/rfc3261.html#section-13)。

Host IMS 新外呼前读取可选 PANI 最多等 1 秒，超时明确省略，不复用旧小区，也不允许迟到
读取覆盖新的元数据；注册读取策略不变。日志新增网关接收、运营商 INVITE 准备、临时 /
最终响应计时，不记录号码或 SIP 原始头。11:37 的十秒振铃主诉尚不能完整归因，需新包
实网复测；回归、原程序备份与安装哈希见 `publish/sip-outbound-cancel-20261016/verification.json`。

## 2026-10-16：Asterisk 群振与真实入呼反馈

已部署的目标 `600` 现将两张卡的来电都转给分机 1001、1002 的所有可用注册联系人，
不再按 com6 / com10 分别指定单一分机。Asterisk 用一个 `Dial` 管理全部分支，首个
应答后取消其他终端；81 / 82 的显式模组选线保持不变。离线终端没有推送唤醒，全部
离线时 PBX 的 SIP 分支失败，新版 WPF 本机仍能接听，不自动换卡。配置、隔离信令验证与实机边界见
[部署说明](../../packaging/asterisk/README.md)。

09:54 的中国卡 Host IMS → Asterisk → 软电话入呼已由用户确认弹窗和双向声音，
约 12 秒后由软电话正常挂断；此前秒挂来自 Asterisk 目标分机不可达。后续短时炸音
用户反馈已恢复，未确定原因，未将其宣称为已修复的编解码问题。

`SipGatewayOptions.LocalIncomingEnabled` 默认关闭，WPF 启用时明确打开。本机与 SIP
按原 Call-ID / 模组抢接，`TryClaimLocalIncomingCall` 成功后走正常本机应答，并取消
SIP 分支；SIP 赢得应答后通过 `SipIncomingCallClaimed` 通知界面停铃 / 停止自动应答。
迟到的 SIP 200 只 ACK + BYE，不得挂断本机已接听的运营商腿。全部 SIP 分支失败时
仍保留本机来电；关闭 SIP 只撤销远端分支，不结束本机来电或本机已接听的通话。
本机「拒接」结束整通来电，取消所有远端振铃；本机自动应答按既有设置参与抢接。

10:48 的共同响铃、10:49 的本机抢接 / 分机撤铃已由用户实测确认，后者保持运营商
主叫接通且双向有声，挂断后媒体路由归零。新版分机先接及本机整通拒接仍待实测。
10:42 曾有一次未进入响铃的短来电，其原因尚未定位，不能因下一通成功而宣称修复。

后续来显修复候选会从可信 IMS 入站优先选择 `P-Asserted-Identity` 的电话 URI，再回退
`From`；不使用客户端自报的 `P-Preferred-Identity`，不向非可信 SIP 端套用运营商身份。
`Privacy: id/user` 时本机及 SIP 腿统一保持匿名，日志只记录来源 / 隐私状态，不记录号码
或原始 SIP 头。实现依据 [RFC 3325 §8](https://www.rfc-editor.org/rfc/rfc3325.html#section-8)
与 [RFC 3323](https://www.rfc-editor.org/rfc/rfc3323.html)。此修改的实网来显尚待验收。

## 当前软件范围：按模组的内部 VoIP 路由

同一窗口中每个模组拥有独立控制 / VoWiFi / IMS / 媒体实例。内部 VoIP 沿用该模组
选择的默认接入，Host IMS、VoWiFi 或桥接不可用时明确失败，不暗中换接入或换 SIM。
实卡通话暂不进行，也不作为当前软件功能交付的前置条件。

“设置 → SIP 对接 → 内部 VoIP”逐模组配置：接入、入站、出站、来电目标。默认出站
要求 `X-VoWin-Line`，可另选固定默认模组；不再依赖 UI 活动卡槽。规则在 SIP 启用时
快照，运行中锁定编辑；保存或导入不启用。关闭模组的 VoIP 接入不关闭它的注册。
状态面板和桌面来电接听判断同样按模组，未接入 VoIP 的模组不被其他模组的 SIP 模式
抢占接听。修复 VoWiFi-only / 蜂窝停止状态的 SIP 路由，保持 Host IMS 的显式选择。

库接口 `SipGatewayOptions.LineRoutes` / `RequireConfiguredLines` / `DefaultOutboundLineId`
支持上述规则；空规则的旧 CLI / 库配置仍保留原自动选线行为。WPF 启用路径始终使用
显式配置规则。来电目标留空沿用中继目标；直连模式指定目标时只选该已注册分机。
填写新目标不自动改 Asterisk 拨号计划；当前已部署的默认 `600` 及 81 / 82 仍可使用。

软件回归与验收索引见 `publish/module-voip-20261014/evidence.json`。真实 SIM / 手机
音频、真实双 USB 音频并发和长时稳定性保持单独未验收项，不与软件功能验收混淆。

## 2026-10-14 双模组与真实 Asterisk 进展

已在用户的 `10.0.0.2` Docker 部署 Asterisk，并执行真实私网互通：1001 / 1002 鉴权注册、
Echo、按 `X-VoWin-Line` 分别转接两台模组、双向 G.711 / PCM、BYE、81 / 82 显式选线
和 `+E.164` 格式均通过。最新显式模组规则复测为 `publish/dual-module-20261014/tests/module-voip-asterisk-lan.trx`。
这些使用真实 Asterisk 和合成呼叫控制器，没有调用真实运营商或实际手机麦克风；
真实 SIM 的 Asterisk 媒体验收由用户安排稍后配合。见 [部署与操作说明](../../packaging/asterisk/README.md)。

SIP 设置新增显式导入部署配置与本机出口地址选择。导入前验证固定私网对端，导入 / 保存
不启动监听，密码不进入非敏感偏好；不会暗中开放服务器经 SIM 外拨。相同 Windows IP
下的中继与软电话按源端口区分，模组离线时不把指定线路改成当前选中的另一台。

## 可选功能与 Asterisk 固定中继

**默认关闭，完全不依赖 Asterisk。** Host IMS、模组 USB 语音桥接、VoWiFi 仍独立使用。
首次须在“设置 → SIP 对接 · 可选”明确启用才创建 SIP/RTP socket；导入或保存本身不会启用。
勾选“记住配置，并在启动时恢复上次 SIP 开关状态”后，重启自动加载配置，并仅在上次
手动启用时恢复监听；手动关闭后下次仍关闭，程序正常退出不视为手动关闭。
导入部署配置会勾选记住功能，用户可取消并保存以清除记住的密码。非敏感参数保存在
`%LOCALAPPDATA%\VoWin\sip-gateway.json`；记住的密码单独使用 Windows 当前用户 DPAPI
加密，普通 JSON 不含密码。不记住时密码仍只保留本次运行。配置与凭据绑定校验、解密、
私网地址或监听失败均不绕过校验自动启用。仅恢复 SIP，不会暗中切换模组或开通 SIM 外拨。
原导入文件可能仍含明文密码，应妥善保管；一次性旧版迁移只读取已指定且认证端点匹配
的本地文件，成功后转为 DPAPI 保存、不再依赖原文件，拒绝网络共享路径。

本地分机与固定中继可独立启用，也可共存：

- **软电话直连**：开启“本地分机服务”，软电话向 VoWin 注册；沿用原 registrar/B2BUA。配置中继不会关闭本地 Registrar，分机互拨／消息仍先按本地账号解析，不会给分机号补国家码。
- **固定 SIP 中继（不注册）**：开启该模式，VoWin 将 SIM 来电直接 INVITE 到指定服务器上的目标，例如 `600`；Asterisk 再呼叫多个软电话。只支持私网 IP、UDP 和 G.711 PCMA/PCMU；不是通用的 SIP REGISTER 客户端，暂不支持域名、TCP、TLS/SRTP、STUN/TURN 或公网 NAT 穿透。

部署时参数对应如下（地址只是示例，应使用你实际的 LAN / VPN IP）：

| VoWin 设置 | 示例 | Asterisk 对应 |
| --- | --- | --- |
| 本机监听 IP / UDP 端口 | `192.168.1.20` / `5062` | 静态 AOR `contact=sip:gateway@192.168.1.20:5062`，identify 匹配 Windows IP |
| SIP 服务器 IP / UDP 端口 | `192.168.1.10` / `5060` | UDP transport 的实际监听 / 发包地址；VoWin 同时校验 IP 与源端口 |
| 入站账号 / 独立密码（服务器 → VoWin） | `pbx-to-vowin` / 自己的强密码 | Asterisk 的 `outbound_auth`，与本地分机及另一个方向独立 |
| 出站账号 / 独立密码（VoWin → 服务器） | `vowin-to-pbx` / 另一份强密码 | Asterisk 的入站 `auth` |
| 服务器预期 Realm | `pbx.example` | 精确对应 Asterisk 入站 auth 的 `realm`；没有自动学习、通配符或固定默认值 |
| 来电转接目标 | `600` | `from-vowin` context 下的 `600` 振铃组 |
| VoWin Realm | `vowin.local` | Asterisk 的 `outbound_auth.realm` 精确填写该值；不是上方服务器 Realm |
| 允许服务器通过 SIM 外拨 | 默认关闭 | 初次只验收入呼；不要创建通用付费外拨路由 |

使用 [PJSIP 示例](asterisk/hostims-pjsip.conf.example) 与 [拨号计划示例](asterisk/hostims-extensions.conf.example) 时，请先改地址与密码，再合并进现有配置；不要覆盖已部署的配置，也不要与其他 starter kit 的同名中继重复配置。已有 `transport-udp` 的名字不同就修改示例中的 transport 引用。Asterisk 的 `auth` / `outbound_auth` 是不同方向，Realm 不宜共用同一个 auth 对象，参见 [官方认证说明](https://docs.asterisk.org/Configuration/Channel-Drivers/SIP/Configuring-res_pjsip/PJSIP-Authentication/)。

连接流程：SIM 来电 → VoWin INVITE `sip:600@服务器:端口` → Asterisk 401/407 → VoWin ACK 并以新 Via/CSeq 进行一次 Digest 重试 → 手机接听、Asterisk 200 → VoWin ACK → 接听运营商来电，等待运营商 ACK 后接入双向 PCM。
固定 peer 双向默认 SHA-256；旧服务器确需 MD5 时，需分别显式选择入站 MD5／允许出站 MD5。入站要求 `qop=auth`；出站也兼容显式允许算法的不带 qop Digest。固定 peer 只接受一个明确的 401／407 challenge；多 challenge、未知 Realm、重复／畸形字段或非固定源地址都不会获得出站凭据。普通本地分机保留原 MD5/qop=auth 兼容性。

2026-10-24 配置迁移：旧中继文件仍可导入为关闭的草稿，原共享密码不会自动复制为本地分机或双向 peer 密码。补齐两份独立凭据、预期 Realm 和算法后才可启用或恢复；新密码只存当前用户 DPAPI，普通 JSON 不导入／导出新 peer 密码。修改远端地址、账号或对应 Realm 后需重新填密码。旧直连配置保持原角色，不会被隐式升为中继。V2 加密凭据绑定完整公开配置，校验失败不降级到旧 hash。

共存时，每条线路的 `IncomingTarget`／`SmsTarget` 选择中继目标，`LocalIncomingTarget`／`LocalSmsTarget` 独立选择本地分机（空表示全部在线本地分机）。无 peer 时，空 Local 字段兼容沿用旧目标。当前不允许固定 peer 任意代理到本地账号；peer → SIM 与 local → SIM 的认证和权限分别检查。启用短信转发前请核对两个目标，因为短信可能包含验证码。

INVITE 的原 30 秒、MESSAGE 的原 8 秒是整个逻辑操作的总预算（含排队、分块和一次认证重试），不是每次挑战重置。不同迟到 To-tag 分别 ACK／有界清理，同 tag 只申请一次 BYE；未知结果不会新起一轮 BYE。短信防重仍保留旧 operation ID，新增认证角色／权限身份绑定在内容指纹中；旧指纹重放会拒绝，不会因升级获得新 ID 而再次付费发送。

新版 WPF 的本机也参与响铃 / 抢接；库与 CLI 默认仍为 SIP 独占模式。只有实际某一端应答后才接通运营商来电。运营商取消会取消相应卡槽/Call-ID 的 SIP 分支，迟到的 200 会 ACK 后 BYE。关闭 SIP 只清理它拥有的远端通话和媒体，不结束本机已接听的通话，不停用 IMS 注册，也不改 APN / QMI / VoWiFi 配置。

状态里的“已启用”只说明本机监听成功，**不是 Asterisk 注册或连通证明**。固定中继不会 REGISTER；用 Asterisk `pjsip show endpoint vowin` / `pjsip show aor vowin-aor` / `dialplan show 600@from-vowin` 核对配置。`qualify_frequency` 的 OPTIONS 只能验证信令可达，不能验证认证、铃声或双向音频。
首次验收应在 LAN/VPN 中完成：先软电话互拨 / Echo，再启用 VoWin，拨 SIM 号码，确认多个终端振铃、只在手机接听后接通、双向声音、双方挂断及再次来电。Asterisk 的 RTP 范围按你已有 `rtp.conf`（例如 `10000–10199`），Windows 侧 RTP/RTCP 使用绑定私网地址的临时 UDP 端口；防火墙应仅允许可信 VPN / LAN 对端，不自动开放公网。

当前验证边界：本机 UDP 合成测试覆盖 401/407 认证、CSeq/ACK、接听顺序、PCMA/PCMU 双向桥接、取消与迟到 200 清理；真实 Asterisk 与合成 SIP 终端历史互通见本文顶部。新增安全配置与迁移还需部署后验收；本轮未操作现用服务、真实 SIM 或手机。SIP MESSAGE 可选且默认关闭，不等于运营商送达确认或 iOS 后台推送。

本次媒体回归还修复了原 A-law 编码把 13-bit 分段阈值用于完整 PCM16、导致幅度错误的问题，以及 μ-law 对 `short.MinValue` 取负溢出的问题。新增固定向量与全部 65,536 个 PCM16 值的量化误差测试；分段范围对照 [Asterisk A-law 实现](https://github.com/asterisk/asterisk/blob/master/main/alaw.c)，未引入外部二进制或依赖。

## 目标与边界

SIP Gateway 把指定模组所选的 Host IMS、VoWiFi 或基带 USB Audio 线路终止在一个 B2BUA，向私网普通 SIP 客户端提供独立 SIP/RTP 会话。已明确选择的线路不可用时拒绝，不自动换 SIM 或退回另一接入：

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
2. 在“系统状态”选择每个模组的默认呼出，并连接所选接入；显式选择的 Host IMS / VoWiFi
   不可用时拒绝，不自动回退到基带。需要基带时明确选择模组桥接。
3. 打开“设置 → SIP 对接 · 可选”，保持服务器中继模式关闭，填写：
   - WireGuard 本机地址：`10.66.66.1`
   - SIP UDP 端口：`5062`（可自定义，需与客户端一致）
   - 分机：`1001`
   - 强密码：桌面按当前 Windows 用户使用 DPAPI 保存；独立 CLI 不替你持久化
4. 点击“启用 SIP 对接”。
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

WPF 多模组配置默认要求 SIP INVITE 携带 `X-VoWin-Line: <slot-id>`；未携带时仅使用
明确配置的默认外拨模组，否则返回 400。禁用、未配置的线路返回 403，已选但不可用
的运营商线路返回 503，不换另一台。旧 CLI 空规则模式才保留号码前缀 / 活动卡槽逻辑。

通话状态按卡槽和真实 Call-ID 跟踪，一张卡结束不会清空另一张卡的会话或桌面音频。
Windows USB UAC 端点按物理 USB ContainerId 绑定，每个模组有自己的 PCM helper / 管道
与预留状态；同一物理设备拒绝重复占用，不同设备不共用媒体上下文。控制隔离与端点
映射已通过模拟回归，真实双 USB 音频并发仍待另行实机验证。

## 安全约束

- 默认拒绝 `0.0.0.0`、`::` 和公网地址绑定；只有显式启用 `AllowPublicBind` 才能绕过公网地址检查。
- SDP 的媒体地址默认不可信，RTP 总是回到 SIP 数据包的实际源 IP，避免把网关变成 UDP 放大器。
- 不要把 UDP 5060 或 RTP 临时端口映射到公网。WireGuard 是公网侧的认证与加密边界。
- 桌面恢复上次开关与完整配置，密码由当前用户的 DPAPI 保护；迁移中继草稿缺少独立
  凭据／预期 Realm 时保持关闭，不猜测配置。独立 CLI 的持久化由宿主负责。
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
