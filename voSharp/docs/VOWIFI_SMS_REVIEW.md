# VoWiFi 始终收不到 SMS：voSharp / VoCat 对照审阅

审阅日期：2026-09-03。范围：当前工作区 voSharp，以及 references/VoCat；Windows 客户端事件接入补查 VoWin。

用户确认的现象是“一直收不到”，不是连接一段时间后失效。本次没有实网接收抓包，因此区分“已复现的代码缺陷”和“该运营商实际命中的原因”。

> 修复状态（2026-09-04）：本文识别的短头解析、请求来源回复路由、独立 RP 报告、multipart 字节保真、IMS sec-agree 用户态数据面、注册续期与 SMS capability 证据均已实现，并加入端到端回归测试。本文其余章节保留最初审阅时的证据和设计依据。

## 结论

最应先修的是 **SIP 短头名解析**：当前实现会把符合协议的下行短信判为不支持的媒体类型，返回 415，完全不进入短信解码和收件箱。已使用相同 SMS-DELIVER 内容、本地调用真实接收处理器与 Kernel 收件箱复现；仅把完整头改成合法短头，就从正常收件变成零收件。

另外确认三个实际缺陷：回复不保留请求来源而固定发往注册目标；短信 RP-ACK/RP-ERROR 被错误地放进 SIP 响应；multipart 解析会修改二进制短信。IMS 安全协商和受保护的下行端口也尚未接入运行路径，影响需要此机制的运营商。

**不能据此宣称短头就是当前 SIM 的唯一根因**：要最终确认，需看到该 SIM 下行 MESSAGE 的头部，或 ESP 解密后没有进入 SIP 的报文元数据。完整头、普通 UDP 的本地短信能够入箱，也证明“错误 RP-ACK 导致所有首条短信都无法在本地显示”并不成立。

## 1. [P1] SIP 短头没有归一化，合法短信被返回 415

位置：

- `src/VoSharp.Sip/SipMessage.cs:155-166`：按原样存储头名。
- `src/VoSharp.Telephony/VoWifi/ImsSmsHandler.cs:147`：只查询 `Content-Type`。
- `src/VoSharp.Telephony/VoWifi/VoWifiManager.cs:820-825`：媒体类型无法提取即回复 415 并返回。

`c: application/vnd.3gpp.sms` 和 `Content-Type: application/vnd.3gpp.sms` 语义相同，但当前 `GetHeader("Content-Type")` 不会命中 `c`。类似地，`v/f/t/i/l` 不会映射到 Via/From/To/Call-ID/Content-Length；生成的拒绝响应还会缺少 Via，From/To/Call-ID 也不正确。拒绝发生在第 828 行接收日志之前，所以没有“Received incoming SIP MESSAGE”日志不一定代表没有下行包。

VoCat 在 `internal/vowifi/ims/message.go:189-196` 显式转换这些短头；同文件 167-175 行还处理折叠头。

本地实验调用真实的 `SipMessage.Parse(byte[])` → `VoWifiManager.HandleIncomingSipMessageAsync` → `VoKernel.GetInbox()`：

```text
headers=full:    status=200, inbox=1, responseBody=020541020000, responseViaCount=1
headers=compact: status=415, inbox=0, responseBody=,             responseViaCount=0
```

修复方案：在 SIP 解析入口统一展开短头，至少覆盖 `v/f/t/i/l/c/m`，并支持完整名和短名混用时合并到同一列表。将展开逻辑统一应用于 Get/Set/AddHeader，避免手动构造消息绕过规则。解析折叠头只处理头部，不触碰二进制正文。按 Content-Length 截取、校验正文。

示意映射：

```csharp
static string CanonicalHeaderName(string name) => name.ToLowerInvariant() switch
{
    "v" => "Via", "f" => "From", "t" => "To", "i" => "Call-ID",
    "l" => "Content-Length", "c" => "Content-Type", "m" => "Contact",
    _ => name
};
```

该修复适用于 Windows，无需驱动或系统网络设置。协议依据：[RFC 3261 §7.3.3](https://www.rfc-editor.org/rfc/inline-errata/rfc3261.html#s-7.3.3)。

## 2. [P1] 接收端丢失来源地址，SIP 响应固定发往 P-CSCF:5060

位置：

- `src/VoSharp.Telephony/VoWifi/VoWifiManager.cs:481-487`：取出端口后只把正文放进 `Channel<byte[]>`，源 IP/端口、目标端口和协议丢失。
- 同文件 `499-505`：自定义 sender 把所有报文固定封装为本地 5060 → P-CSCF 5060。
- 同文件 `779`：回复复用这个 sender。
- `src/VoSharp.Sip/SipTransport.cs:74-77,153`：普通 UDP 路径也丢弃 `RemoteEndPoint`，发送总用 `_remoteEp`。

因此，当下行请求从另外的源端口到达，并通过 Via/rport 要求回原端口时，响应会发错目的地。VoCat 的 `sms_runtime.go:189-207` 保留 remote，并通过该次接收的 respond 回调回复。

本地回环 UDP 验证：注册目标和请求发送方使用两个不同端口。下行请求的 Via 明确带源端口和 rport；voSharp 的回复到达注册目标，请求发送方没有收到回复。

```text
reply route: registeredProxyReceived=True, actualRequestSourcePendingBytes=0
```

修复方案：引入接收上下文，例如 `SipReceivedPacket(Message, LocalEndPoint, RemoteEndPoint, Transport, ReplyAsync)`。上层回复使用这个上下文；主动发出的 REGISTER / RP-ACK MESSAGE 使用注册后的路由和安全关联。UDP 响应依 Via/received/rport 计算目的地址，rport 场景回源地址和源端口，并从相应本地端口发出。不要仅全局修改远端端口，否则会破坏主动请求的路由。

该问题影响网络是否确认投递，**自身不必然阻止第一条已解码短信在本地显示**。协议依据：[RFC 3581](https://www.rfc-editor.org/rfc/rfc3581.html)。

## 3. [P1] RP 确认必须另发 MESSAGE，不能塞进 SIP 200

位置：`src/VoSharp.Telephony/VoWifi/VoWifiManager.cs:835-838,874-879`。

当前收到 RP-DATA 后，在解码前就回复携带 RP-ACK 的 SIP 200；如果后续解码失败，又对同一请求回复携带 RP-ERROR 的第二个 SIP 200。这既缺少独立的 RP 层交付报告，也过早确认了尚未被成功接收的内容。

VoCat 的对应流程：

- `sms_runtime.go:341-372`：先回复无正文的 SIP 200。
- `sms_runtime.go:527-542`：处理/保存短信成功后才确认；失败发送 RP-ERROR。
- `sms_runtime.go:636-655`：另发 SIP MESSAGE 到原请求的 P-Asserted-Identity，缺失时兼容回退 From；设置 In-Reply-To。
- `sms_runtime.go:808-902`：独立 Call-ID/CSeq、已注册公共身份、Service-Route 和适用的安全协商头。

修复后的交互：

```text
IP-SM-GW → UE : MESSAGE [RP-DATA + SMS-DELIVER]
UE → IP-SM-GW : SIP 200，Content-Length: 0
UE            : 校验、解码并可靠接收短信/分片
UE → IP-SM-GW : 新 MESSAGE [RP-ACK，或失败时 RP-ERROR]
IP-SM-GW → UE : SIP 200/202
```

新 MESSAGE 的 Request-URI/To 指向 IP-SM-GW，From 使用已注册公共身份（不能用原始 Contact 整段字符串），In-Reply-To 指向原下行 Call-ID。保留 RP reference；长短信每个接收分片分别做 RP 确认，不等待全部重组。事务需有超时、UDP 重传和重复接收去重。底层事务键应包含 CSeq 数字；当前 `SipTransport.GetTxKey` 只有 Call-ID 和 method，在复用 Call-ID 的后续事务中可能误匹配。

这项缺陷会造成网络端无法正常完成短信交付，值得修复，但本地 full-header 实验仍然入箱，不能用它单独解释“第一条也一直看不到”。协议依据：[3GPP TS 24.341 §5.3.2.3–5.3.2.4](https://www.etsi.org/deliver/etsi_ts/124300_124399/124341/12.07.00_60/ts_124341v120700p.pdf)。

## 4. [P1，运营商相关] sec-agree 未接入，数据面只支持固定端口的普通 IPv4/UDP

位置：

- `src/VoSharp.Sip/SipRegisterSession.cs:108-116`：取得 AKA 的 RES/CK/IK 后只用 RES 生成摘要，未处理 Security-Server、安装 IMS SA 或切换受保护端口。
- `src/VoSharp.Sip/SecurityAgreement.cs`：有构造/解析/密钥辅助函数，但生产代码没有调用 `SecurityAgreementBuilder`。
- `src/VoSharp.Telephony/VoWifi/VoWifiManager.cs:447,481-487,502`：端口固定 5060，解密后直接按 IPv4/UDP 偏移处理，未检查 IP 协议、分片，也未解析内层 transport-mode ESP。

VoCat 的 `security.go:822-900` 会激活 IMS IPsec，`contactAddress():904-909` 使用 UE server port，`sms_runtime.go:80-104` 同时读取相应受保护 UDP/TCP 入站通道。注释明确记录部分运营商即使注册用 TCP，也通过受保护 UDP server port 下发短信。

这里存在两层不同的安全机制：UE↔ePDG 的外层隧道与 UE↔P-CSCF 的 IMS sec-agree。前者建立成功不能证明后者已实现。不过有些网络允许仅使用外层隧道；**没有该运营商协商报文时，不能断言所有 VoWiFi 都必须再建内层 SA**。严格要求 sec-agree 的网络也可能直接拒绝当前注册，因此单看“注册成功”的描述不能确定此分支已经命中。

适合当前 Windows 架构的方案：

1. 继续沿用现有 C# 用户态 ePDG ESP/SOCKS5 数据面，在注册会话内接入 Security-Client/Server/Verify 的实际生命周期。
2. 为要求 IMS IPsec 的配置建立四个单向 SA（两个双向通信对），用协商的 CK/IK 派生密钥；按协商算法处理完整性与加密，保留每个方向的 SPI/序号/重放窗口。
3. 增加专门的 IMS transport-mode ESP 编解码器。现有 `EspTunnel` 为外层 tunnel mode，不能直接当成 transport mode 使用。
4. 外层 ESP 解密后，先解析 IP 版本、协议、长度和分片；按协议处理普通 UDP、内层 ESP，以及未来需要的 TCP。内层 ESP 解密后再以协商后的 UE client/server 端口分发 SIP，并保留回复上下文。
5. 普通网络继续使用普通 SIP 分支；明确需要 sec-agree 的配置若协商或 SA 安装失败，应报告原因，而不是显示 SMS ready。

如果需要支持 TCP，可引入 Windows 虚拟接口与 IP/TCP 栈等完整方案，再连接现有外层 ESP；不能把 TCP 包直接跳过 8 字节当 UDP。Linux 的 `ip xfrm` / network namespace 不能直接复制到 Windows。另一条 Windows WFP 路径需要完成虚拟地址、路由和 SA 的端到端接入；当前 `HasNativeDataPlane()` 返回 false，不是可直接切换使用的修复。

## 5. [P2] multipart 会改写二进制载荷

位置：`src/VoSharp.Telephony/VoWifi/ImsSmsHandler.cs:167-184`。

对整个 MIME part 做 `Replace("\r\n", "\n")` 会修改正文中的任意 `0D 0A`；`TrimEnd('-', '\r', '\n')` 又会吞掉真实正文末尾的 `2D/0D/0A`。Latin1 本身能够保持字节，问题在后续的字符串替换和裁剪。

独立的 MIME 字节保真实验（只验证提取器，不作为完整 SMS-DELIVER 样本）：

```text
original=01050000020D0A → extracted=0105000002
original=01050000012D   → extracted=0105000001
```

VoCat `sms_runtime.go:545-606` 用 MIME 字节流解析器提取原始 part，并按 Content-Transfer-Encoding 解码。

修复方案：从 RawBody 的字节流解析 MIME 边界，只移除 MIME 自身的分隔 CRLF；二进制载荷逐字节保留。精确解析媒体类型，支持 binary/8bit/base64/quoted-printable；编码错误明确拒绝。`ParseRpdu` 对非法 TPDU 长度应报错，不能通过截短长度掩盖载荷已经损坏。

## 其他发现与排除项

- **事件链已接通**：`VoWifiManager:859` 发布 SmsIncoming，`VoKernel:233-268` 加入 inbox 并触发 SmsReceived，`VoWin/Services/VoKernelService.cs:276-283` 显示短信。本地实验已验证到 Kernel 入箱，未启动 WPF 做 UI 交互验证。
- **不是缺少 smsip 声明**：`ImsRegisterBuilder.cs:66` 已在 Contact 添加 `+g.3gpp.smsip`。但注册结果只取第一个 Contact，没验证它对应本会话且被确认支持 SMS。VoCat `provider.go:1147-1198,1356-1378` 有对应检查。应把“IMS 已注册”与“SMS 接收能力已确认”分开报告。
- **注册续期缺失**：管理器只执行一次 RegisterAsync，未保存会话并安排刷新；VoCat `provider.go:1203-1274` 有续期和失败状态转换。这能解释久置后失效，不能解释用户反馈的从一开始就失败。修复时按服务器授权有效期安排提前刷新，并更新失效状态。
- **NAT-T 保活存在**：`IkeTransport.cs` 已有 20 秒 keepalive，不能把“没做 NAT 保活”作为当前结论；保活也不能代替 IMS REGISTER 续期。

## 建议实施顺序与验收

先修短头归一化并增加拒收原因日志，这是修改最小、已复现、最能直接解释零收件的路径。随后同时修复回复上下文与独立 RP 报告，再处理 MIME。根据真实协商情况实施 sec-agree 数据面，最后补注册续期与 readiness。

用于确定当前 SIM 命中哪一项的日志点：

1. 外层解密后：IP version/protocol、源/目标 IP 与端口、分片标志、长度、必要时内层 SPI；对未分发数据说明原因。
2. SIP 解析后、媒体校验前：method、Call-ID、CSeq、归一化 Content-Type、Content-Length、实际正文长度、传输端点。
3. RP/TPDU：RP 类型/reference、解码成功或失败原因、分片接收状态；无需记录短信正文或 AKA 密钥。
4. REGISTER 最终响应：本会话 Contact、授权 expires、smsip 确认情况、Security-Server 与实际激活结果。

如果有 `c:` 的 MESSAGE 被回 415，第 1 项就是实际阻断点。如果根本没有 MESSAGE，而外层解密后出现内层 ESP/其他目标端口，则应处理第 4 项。如果有 full-header MESSAGE 且解码失败，检查 MIME/RP/TPDU。如果能入箱但网络反复重投，检查第 2、3 项。

必要回归用例：完整头与短头混用；真正的 SMS-DELIVER 单段和多段到 Kernel 入箱；请求源端口不同于注册目标；每次下行的空正文 SIP 200 和独立 RP-ACK/RP-ERROR；二进制 MIME 字节保真；协商端口/内层 ESP 数据分发；短有效期注册续期。真实网络验收需在 Windows 下观察至少一条下行短信及其独立 RP 报告成功完成。

## 已执行验证

```powershell
dotnet test tests/VoSharp.Tests/VoSharp.Tests.csproj --no-restore --filter "FullyQualifiedName~SmsComprehensiveTests|FullyQualifiedName~SipImsTests|FullyQualifiedName~TelephonyTests" --verbosity minimal
```

最初结果：42 项通过，0 项失败。修复后原有误导性 EndToEnd 用例已替换为真实 SMS-DELIVER 序列化用例，并新增短头到 Kernel 入箱、独立 RP-ACK/RP-ERROR、重传去重、响应回源、MIME 字节保真、IMS ESP 固定向量/协商/重放保护和注册续期测试。最终全量结果为 196 项通过，0 项失败。

另建隔离本地复现程序，直接引用当前 Kernel/SIP/Telephony 项目，结果如上。程序路径：`C:/Users/19747/AppData/Local/Temp/vosharp-sms-review-20260903/Program.cs`；运行命令：

```powershell
dotnet run --project C:/Users/19747/AppData/Local/Temp/vosharp-sms-review-20260903/SmsReview.csproj --verbosity quiet
```

复现程序只注入合成报文和使用本地回环 UDP，不访问模组、不发送实际短信。临时目录可能被系统清理；本文保留了观察结果与修复验收条件。
