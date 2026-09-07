# B8 · 数据面移植规格：Linux XFRM → Windows WFP / IPsec API

> 生成时间：2026-09-01
> 依据：
> - `voCore` 源码实证（`internal/vowifi/ike/installer_linux.go`、`userspace_linux.go`、`ims/security_linux.go`、`ims/security.go`）
> - `voSharp` 源码全量扫描
> - Windows SDK **10.0.26100.0**：常量取自 `shared/ipsectypes.h`，**函数签名取自 `um/fwpmu.h`**，
>   **结构体大小/偏移用 `clang-cl` 实测**（`tools/wfplayout/layout.c`）
>
> **2026-09-01 二版修订**：上一版里有两处是凭函数名推的，现已用 SDK 更正，见 §9。
> 结构体布局与函数签名一律以头文件为准，不再有任何推测。

---

## 0. 结论先行（三条，先看这个）

### 0.1 voSharp 目前**没有**任何可安装到数据面的 SA 模型

| 要找的东西 | 结果 |
|---|---|
| IKE_SA / CHILD_SA 模型 | ❌ 只有 `ViciChildSa` / `ViciChildSaConfig` 两个**只读 DTO**（字符串型 SPI、算法名、TS），是 VICI `list-sas` 的解析产物，不是数据面模型 |
| Security Association 模型 | ❌ 无。`EspTunnel` 有字段但**全项目零实例化点**（孤儿类） |
| strongSwan 调用边界 | ✅ `ViciClient`（TCP 127.0.0.1:4502），唯一入口是 `SendCommandAsync(name, msg)` |
| CHILD_SA 建立后的回调 | ⚠️ **不是回调，是轮询**：`VoWifiManager.StartVoWifiNativeAsync` step 5 调 `ListSasAsync()` 找 `State == "INSTALLED"`。VICI 有 `RegisterEventAsync("child-updown")` 但**从未被调用** |
| Linux/Windows 数据面抽象 | ❌ 零接口、零 installer。`VoWifiManager.cs:237-243` 只有一个事件占位 |

**这意味着 B8 不只要写 WFP 的 P/Invoke，还要先建一套中立的 SA 模型。**

### 0.2 voCore 的 XFRM 内核路径在生产中**几乎走不到**

```go
// installer_linux.go:31-36 —— 路由分发
if config.ProxyMode == vowifi.ProxyModeSOCKS5 || config.UDPEncapsulation {
    return linuxUserspaceInstaller{...}.Install(ctx, config)   // ← TUN + 用户态 ESP
}
return linuxXFRMInstaller{...}.Install(ctx, config)            // ← ip xfrm
```

只要协商了 NAT-T（`UDPEncapsulation == natDetected`），就走用户态。而 VoWiFi 场景下 UE 几乎必然在 NAT 后面，**所以 voCore 的生产路径是 `userspace_linux.go`（`/dev/net/tun` + Go 自己做 ESP），不是 `ip xfrm`。**

`installer_linux.go:262-264` 里的 `encap espinudp` 分支是**死代码**（上面已经分流走了）。

> **对 VoSharp 的直接影响**：最该照搬的是 voCore 的**用户态路径**（对应 Windows 上的 Wintun + 用户态 ESP），而内核 XFRM 路径的移植价值低于预期。这与"用 WFP 不用 Wintun"的决策有张力，见 §0.3。

### 0.3 WFP 隧道模式在 Windows 上没有 `ip link add type dummy` 的等价物 —— 这是最大风险

Linux XFRM 隧道路径靠三件事：`type dummy` 网卡承载虚拟 IP + `ip xfrm policy` 把流量吸附进 SA + 无需显式路由。

Windows 上：
- WFP **有**隧道模式（`IPSEC_TRAFFIC_TYPE_TUNNEL` + `IPSEC_TUNNEL_POLICY0`），内核做封装，**不需要 dummy 网卡**
- 但**出站报文的内层源地址仍需是一个本机可用地址**，否则路由阶段就出不去，根本轮不到 IPsec 匹配
- Windows 没有 `ip rule add ... lookup <table>` 这种源策略路由（`userspace_linux.go:279-358` 用了独立路由表 + `ip rule`）

→ **虚拟 IP 怎么落到 Windows 网络栈上，是 B8 必须先用 spike 验证的问题**，见 §6。

---

## 1. 你列的 12 个字段 · 总映射表

| # | 字段 | Linux XFRM（voCore 实测） | Windows WFP / IPsec API | 备注 |
|---|---|---|---|---|
| 1 | **SPI** | `spi 0x%08x`（8 位定宽小写 hex，`0x` 前缀）；UE 生 Inbound、ePDG 给 Outbound | `IPSEC_SA0.spi`（`typedef UINT32 IPSEC_SA_SPI`） | 直接对应。WFP 是裸 `UINT32`，无前缀。<br>入向SPI 可用 `IPsecSaContextGetSpi0` 由系统分配，或 `IPsecSaContextSetSpi0` 自己指定 |
| 2 | **Direction** | `dir in` / `dir out`（policy）；state 靠 **src/dst 对调** 表达 | `IPSEC_SA_DETAILS0.saDirection`（`FWP_DIRECTION_INBOUND` / `FWP_DIRECTION_OUTBOUND`）；`IPsecSaContextAddInbound0` / `AddOutbound0` | WFP 是显式方向字段，比 XFRM 的"靠地址对调"清晰 |
| 3 | **Encryption algorithm** | `enc cbc(aes) <key>`（IKE 侧恒为 `cbc(aes)`，**不读** `Encryption` 字段，靠密钥长度区分 128/256）；IMS 侧 `cbc(aes)`/`cbc(des3_ede)`/`cipher_null` | `IPSEC_CIPHER_TRANSFORM_ID0 { IPSEC_CIPHER_TYPE, IPSEC_CIPHER_CONFIG }` | ⚠️ XFRM 用**字符串**，WFP 用**枚举对**。见 §3.1 |
| 4 | **Integrity algorithm** | `auth-trunc hmac(sha1) <key> 96`（截断长度是**独立参数**）；IMS 侧恒 96 即使 sha1 | `IPSEC_AUTH_TRANSFORM_ID0 { IPSEC_AUTH_TYPE, IPSEC_AUTH_CONFIG }` | ⚠️ **截断长度在 WFP 里内建于 enum**，不再是独立参数。见 §3.2 |
| 5 | **Encryption key** | `0x` + 小写 hex（`encoding/hex`，无分隔符）；长度 128→16B、256→32B | `IPSEC_SA_CIPHER_INFORMATION0.cipherKey`（`FWP_BYTE_BLOB`：**裸字节 + 长度**） | 直接对应，WFP 更简单（不用 hex 编码） |
| 6 | **Integrity key** | `0x` + 小写 hex；SHA1→20B、SHA256→32B；IMS 侧 sha1 需 **IK + 4 字节 0 = 20B** | `IPSEC_SA_AUTH_INFORMATION0.authKey`（`FWP_BYTE_BLOB`） | 同上。密钥派生逻辑（`SecurityAgreementBuilder.ExpandKeys`）已实现 |
| 7 | **Traffic Selector** | `policy add src <prefix> dst <prefix> [proto N] [sport N] [dport N] dir <in\|out>`；地址范围被 `selectorPrefix()` **强制折叠成 CIDR**，非 CIDR 直接报错 | `IPSEC_TRAFFIC1`（local/remote addr + `remotePort` + `localPort` + `ipProtocol`）+ **`FWPM_FILTER0 transportFilter`**（`IPSEC_SA_DETAILS0` 的成员） | ⚠️ XFRM 的 `proto/sport/dport` 是 policy 参数；WFP 拆到了两处。端口范围（start≠end）XFRM 直接报错，WFP 的 filter 也需拆成多条或改用 range 条件 |
| 8 | **Tunnel / Transport mode** | `mode tunnel`（IKE）/ `mode transport`（IMS sec-agree） | `IPSEC_TRAFFIC1.trafficType` = `IPSEC_TRAFFIC_TYPE_TUNNEL` / `IPSEC_TRAFFIC_TYPE_TRANSPORT` | 干净对应 |
| 9 | **Inner / Outer address** | outer = `tmpl src/dst`（= `OuterLocal`/`OuterRemote`）；inner = TS 前缀 + 虚拟 IP（装在 `type dummy` 网卡） | outer = `IPSEC_TUNNEL_POLICY0.tunnelEndpoints`（`IPSEC_TUNNEL_ENDPOINTS0 {localV4, remoteV4}`）；inner = `IPSEC_TRAFFIC1` 的 local/remote + `transportFilter` 条件 | ⚠️ XFRM 用 `tmpl` 表达隧道端点，WFP 用独立的 `IPSEC_TUNNEL_POLICY0`。**inner 地址如何落到 Windows 栈上 = §6 风险点** |
| 10 | **Lifetime** | **完全没设**（voCore 无 `LIMIT` 参数、无软硬过期） | **必填**：`IPSEC_SA_BUNDLE0.lifetime`（`IPSEC_SA_LIFETIME0 {lifetimeSeconds, lifetimeKilobytes, lifetimePackets}`） | ⚠️ **不对称**：Linux 侧是"无限期"，WFP 必须给值。见 §4 |
| 11 | **Rekey** | **完全没实现**（全 `internal/vowifi` 无 `CREATE_CHILD_SA` 代码，grep 零命中） | 需自己实现：新 SA `AddInbound0`/`AddOutbound0` → 切换 filter → `IPsecSaContextExpire0` 或 `DeleteById0` 老 SA | ⚠️ 两侧都缺。WFP 提供了 `IPsecSaContextExpire0` 做优雅过期，XFRM 没有对应物 |
| 12 | **Delete** | 逆序：`policy delete` → `state delete` → `link delete`（`installer_linux.go:281-333`，`errors.Join` 聚合、幂等） | `IPsecSaContextDeleteById0(engine, saContextId)` + 删除 `FWPM_FILTER0` + 清理 IP Helper 路由/地址 | WFP 更简单（一个 context id 管一对 SA），但 filter 和路由要单独清 |

---

## 2. 结构对应关系（一张图）

```
Linux XFRM                                    Windows WFP
─────────────────────────────────────────    ─────────────────────────────────────────
ip xfrm state add                         →  IPSEC_SA0 + IPSEC_SA_BUNDLE0
  src/dst/proto/spi                           IPSEC_SA0.spi
  mode tunnel|transport                       IPSEC_TRAFFIC1.trafficType
  reqid N                                     （见下）
  replay-window 32                            （无显式字段，见 §6）
  auth-trunc hmac(sha1) KEY 96                IPSEC_SA_AUTH_INFORMATION0
  enc cbc(aes) KEY                            IPSEC_SA_CIPHER_INFORMATION0
  encap espinudp 4500 4500 0.0.0.0            IPSEC_V4_UDP_ENCAPSULATION0

ip xfrm policy add                        →  IPSEC_TRAFFIC1 + FWPM_FILTER0
  src/dst prefix                              IPSEC_TRAFFIC1.local/remoteV4Address
  proto/sport/dport                           IPSEC_TRAFFIC1.ipProtocol/localPort/remotePort
                                              + FWPM_FILTER0 条件
  dir in|out                                  IPSEC_SA_DETAILS0.saDirection
  priority 100                                FWPM_FILTER0.weight / IPSEC_TRAFFIC 无 priority
  tmpl src/dst ... mode ... reqid             IPSEC_TUNNEL_POLICY0.tunnelEndpoints
  level required                              FWPM_FILTER0.action.type = FWP_ACTION_PERMIT
                                              + IPSEC 层 filter 关联

ip link add NAME type dummy               →  ❌ 无等价物（Windows 无 dummy 网卡）
ip address add vIP/32 dev NAME            →  CreateUnicastIpAddressEntry（IP Helper API）装到某接口
ip -6 address add vIP6/N dev NAME             CreateUnicastIpAddressEntry (IPv6)

ip rule add priority P from X lookup T    →  ❌ 无等价物（Windows 无源策略路由表）
ip route add table T default dev NAME         CreateIpForwardEntry2 + metric

（两者合一）                                ←  IPSEC_SA_DETAILS0 就是 WFP 侧的
                                              "state + policy" 打包单元：
                                                ipVersion
                                                saDirection
                                                traffic        ← policy 的选择子
                                                saBundle       ← state 的 SA 列表
                                                udpEncapsulation
                                                transportFilter ← policy 的细化条件
```

> **`IPSEC_SA_DETAILS0` 是关键发现**：WFP 把 XFRM 的 state 和 policy 打包进了同一个结构。
> 移植时不要分别做"装 state"和"装 policy"，直接以 `IPSEC_SA_DETAILS0` 为单位，一次搞定。

---

## 3. 算法名映射（常量值均从 `ipsectypes.h` 实测）

### 3.1 加密算法：XFRM 字符串 → WFP 枚举对

| XFRM 名 | 密钥长度 | `IPSEC_CIPHER_TYPE` | `IPSEC_CIPHER_CONFIG` | 值 |
|---|---|---|---|---|
| `cbc(aes)` | 16 B | `IPSEC_CIPHER_TYPE_AES_128` = 3 | `IPSEC_CIPHER_CONFIG_CBC_AES_128` = 3 | ✅ |
| `cbc(aes)` | 32 B | `IPSEC_CIPHER_TYPE_AES_256` = 5 | `IPSEC_CIPHER_CONFIG_CBC_AES_256` = 5 | ✅ |
| `cbc(aes)` | 24 B | `IPSEC_CIPHER_TYPE_AES_192` = 4 | `IPSEC_CIPHER_CONFIG_CBC_AES_192` = 4 | — |
| `cbc(des3_ede)` | 24 B | `IPSEC_CIPHER_TYPE_3DES` = 2 | `IPSEC_CIPHER_CONFIG_CBC_3DES` = 2 | ✅ IMS 用 |
| `cbc(des)` | 8 B | `IPSEC_CIPHER_TYPE_DES` = 1 | `IPSEC_CIPHER_CONFIG_CBC_DES` = 1 | — |
| `cipher_null` + 空串 key | 0 B | — | — | ⚠️ 见下 |

**`ealg=null` 的处理（IMS sec-agree 会用到）**

- Linux：必须传真正的空字符串作 key，传 `"0x"` 会被 XFRM 判 `EINVAL`
  ```go
  // security.go:522-527
  arguments = append(arguments, "enc", "cipher_null", "")
  ```
- Windows：**不要用空 key**。改成把 transform type 切成 pure-auth：
  - `IPSEC_SA0.saTransformType = IPSEC_TRANSFORM_ESP_AUTH`
  - 只填 `espAuthInformation`，`IPSEC_SA_AUTH_INFORMATION0`
  - 枚举：`IPSEC_TRANSFORM_ESP_AUTH = 2`（`IPSEC_TRANSFORM_AH=1, ESP_AUTH=2, ESP_CIPHER=3, ESP_AUTH_AND_CIPHER=4, ESP_AUTH_FW=5`）

⚠️ 注意 voCore 的 XFRM 写法是 `IPSEC_TRANSFORM_ESP_AUTH_AND_CIPHER` + NULL cipher 的另一条路。Windows 上两条都在，但 **pure-auth 更干净**，且避免了"空 FWP_BYTE_BLOB"的边界情况。

### 3.2 完整性算法：XFRM 字符串 + 截断长度 → WFP 枚举对

| XFRM 名 | 截断 | 密钥长度 | `IPSEC_AUTH_TYPE` | `IPSEC_AUTH_CONFIG` | 值 |
|---|---|---|---|---|---|
| `hmac(md5)` | 96 | 16 B | `IPSEC_AUTH_MD5` = 0 | `IPSEC_AUTH_CONFIG_HMAC_MD5_96` = 0 | ✅ IMS 用 |
| `hmac(sha1)` | 96 | 20 B | `IPSEC_AUTH_SHA_1` = 1 | `IPSEC_AUTH_CONFIG_HMAC_SHA_1_96` = 1 | ✅ |
| `hmac(sha256)` | 128 | 32 B | `IPSEC_AUTH_SHA_256` = 2 | `IPSEC_AUTH_CONFIG_HMAC_SHA_256_128` = 2 | ✅ |

> **关键差异**：XFRM 的截断长度（96/128）是 `auth-trunc` 的**第 4 个参数**；
> WFP 把截断**内建进 `IPSEC_AUTH_CONFIG` 枚举**（`_HMAC_SHA_1_96` / `_HMAC_SHA_256_128`），没有独立长度字段。
>
> 这带来一个 voCore 没有暴露的坑：IMS 侧恒用 96 位截断（即使 sha1），而 IKE 侧 sha256 用 128。
> 映射时必须**按完整算法名**选枚举，不能只按哈希族。

### 3.3 密钥格式

| | Linux XFRM | Windows WFP |
|---|---|---|
| 编码 | `"0x" + hex.EncodeToString(key)`（小写 hex，无分隔符） | `FWP_BYTE_BLOB { size, data }` — **裸字节** |
| SPI | `fmt.Sprintf("0x%08x", spi)` | `UINT32`，无前缀 |
| reqid | 十进制字符串，无前缀 | 无对应概念（见 §5） |

---

## 4. Lifetime：Linux 无限期 vs Windows 必填

voCore **完全没有** lifetime：
- `ChildSAConfig` 无 lifetime / replay-window / ESN / priority 字段
- `xfrm state add` 命令里没有 `LIMIT` 参数
- 用户态路径在 ESP 序号耗尽时报 `rekey is required`（`esp.go:209`）但**没有实现 rekey**

Windows 侧 `IPSEC_SA_BUNDLE0.lifetime` 是 `IPSEC_SA_LIFETIME0 { UINT32 lifetimeSeconds; UINT32 lifetimeKilobytes; UINT32 lifetimePackets; }`，**必填**。

**建议取值**（voCore 未验证，需按运营商行为调）：

| 场景 | lifetimeSeconds | lifetimeKilobytes | lifetimePackets |
|---|---|---|---|
| ePDG 隧道 CHILD_SA | 3600（常见 IKEv2 hard lifetime） | 0（不限） | 0（不限） |
| IMS sec-agree SA | 与 REGISTER `expires` 对齐（默认 3600） | 0 | 0 |
| 保守兜底 | 2147483647 | 2147483647 | 2147483647 |

> ⚠️ 填 0 的含义在 WFP 文档里是"不限"，但部分版本行为不一致。**先用 3600 并在真机上观察是否触发提前重协商**，比填 0 安全。
> 另：`IPSEC_SA_BUNDLE0` 还有 `idleTimeoutSeconds` 和 `ndAllowClearTimeoutSeconds`，voCore 没有对应概念，默认填 0。

---

## 5. reqid：XFRM 有，WFP 没有

| | 做法 |
|---|---|
| Linux IKE 侧 | `reqid = InboundSPI`（直接拿 SPI 当十进制 reqid，`installer_linux.go:68`，**无碰撞检测**） |
| Linux IMS 侧 | `reqid = (UESPI ^ PCSCFSPI) & 0x7fffffff`，0 兜底为 1/2，碰撞时 `^= 0x40000000`（`security.go:680-700`） |
| Windows | **无 reqid**。SA 与 policy 的绑定靠 `IPSEC_SA_DETAILS0` 打包 + `IPSEC_TRAFFIC1.ipsecFilterId`（transport）或 `tunnelPolicyId`（tunnel） |

→ 移植时**直接丢弃 reqid**，改用 `IPSEC_SA_DETAILS0` 的打包语义。C# 模型里不要保留这个字段。

---

## 6. 需要 spike 实测的风险点（按优先级）

| # | 风险 | 为什么 | 建议的验证方式 |
|---|---|---|---|
| **R1** | **虚拟 IP 落到哪个 Windows 接口** | XFRM 用 `type dummy` 网卡承载 vIP + policy 吸附，全程无路由。Windows 无 dummy，且出站内层源地址必须是本机可用地址，否则路由阶段就出不去 | 先写一个最小 spike：Wintun 建一个网卡只用来承载 vIP（**不收发数据**），其余交给 WFP 隧道模式。若可行，则"WFP 数据面 + Wintun 仅作地址承载"是一个组合解 |
| **R2** | **replay window** | XFRM 内核路径不设（用默认 32）；用户态路径 64（`esp.go:23`）。WFP `IPSEC_SA0` / `IPSEC_SA_BUNDLE0` **都没有 replay window 字段** | 查 WFP 是否由系统统一固定（很可能是 32 或 64 常量）。**这一项要在真机上抓包确认**，因为它影响抗重放能力 |
| **R3** | **NAT-T / UDP 4500** | XFRM 侧 `encap espinudp` 是死代码（NAT-T 一律走用户态 relay）。WFP 有 `IPSEC_V4_UDP_ENCAPSULATION0 {localUdpEncapPort, remoteUdpEncapPort}` | 需确认 WFP 在 `IPSEC_TRAFFIC_TYPE_TUNNEL` + UDP 封装下能否正确工作，以及是否需要额外放通 UDP/4500 的 WFP filter |
| **R4** | **TS 端口范围** | XFRM 遇 `start != end` 直接报错（`installer_linux.go:191` 注释：`cannot be represented safely by XFRM`）。WFP filter 支持 range 条件但语义不同 | VoWiFi 的 TS 通常是 `0.0.0.0/0` 全通，端口范围极少出现。**一期只支持 start==end，其余直接报错，与 voCore 行为一致** |
| **R5** | **优先级 / priority** | IMS 侧 XFRM 恒为 `priority 100`。WFP 用 `FWPM_FILTER0.weight`，语义不同（weight 是同层内的排序，不是全局优先级） | 先固定填一个值，真机观察是否被其他 IPsec 策略抢先 |

---

## 7. 建议的 C# 模型草案（下一步要建的）

中立、不绑定平台，先建模型再写两个 installer。

```csharp
namespace VoSharp.Ipsec;

public enum SaDirection { Inbound, Outbound }
public enum SaMode { Tunnel, Transport }

/// <summary>与平台无关的一条 SA。对应 XFRM 的一条 state / WFP 的一个 IPSEC_SA0。</summary>
public sealed record SecurityAssociation(
    uint Spi,
    SaDirection Direction,
    SaMode Mode,

    // 算法（用中立名，由 installer 各自翻译成平台枚举）
    string CipherAlgorithm,     // "aes-cbc-128" | "aes-cbc-256" | "3des-cbc" | "null"
    string AuthAlgorithm,       // "hmac-md5-96" | "hmac-sha1-96" | "hmac-sha256-128"
    byte[] CipherKey,           // null 密码时为 Array.Empty
    byte[] AuthKey,

    // 端点
    IPAddress OuterLocal,       // 隧道模式：本机外网地址；传输模式：本机地址
    IPAddress OuterRemote,
    IPAddress? TunnelLocal,     // 仅隧道模式的 inner 端点（可为 null）
    IPAddress? TunnelRemote,

    // 生命周期
    int LifetimeSeconds,
    long LifetimeKilobytes = 0,
    long LifetimePackets = 0,

    // NAT-T
    bool UdpEncapsulation = false,
    ushort UdpEncapLocalPort = 4500,
    ushort UdpEncapRemotePort = 4500
);

/// <summary>流量选择子。对应 XFRM 的一条 policy / WFP 的 IPSEC_TRAFFIC1 + FWPM_FILTER0。</summary>
public sealed record TrafficSelector(
    IPAddress StartAddress,
    IPAddress EndAddress,       // == StartAddress 表示 /32 或 /128
    byte IpProtocol = 0,        // 0 = 任意
    ushort StartPort = 0,
    ushort EndPort = 65535
);

/// <summary>一次安装请求。对应 XFRM 的 state+policy 组合 / WFP 的一个 IPSEC_SA_DETAILS0。</summary>
public sealed record SaInstallRequest(
    IReadOnlyList<SecurityAssociation> Sas,
    IReadOnlyList<TrafficSelector> LocalSelectors,
    IReadOnlyList<TrafficSelector> RemoteSelectors,
    IPAddress? InnerLocalAddress,   // ePDG 分配的虚拟 IP（隧道模式）
    int InnerPrefixLength = 32,
    IReadOnlyList<IPAddress>? DnsServers = null,
    IReadOnlyList<IPAddress>? PcscfAddresses = null
);

/// <summary>数据面抽象。Windows 实现走 WFP；将来如需用户态路径可另加实现。</summary>
public interface ISaInstaller
{
    Task<ISaHandle> InstallAsync(SaInstallRequest request, CancellationToken ct = default);
}

public interface ISaHandle : IAsyncDisposable
{
    IReadOnlyList<uint> Spis { get; }
    /// <summary>优雅过期（WFP 的 IPsecSaContextExpire0；XFRM 无对应物）。</summary>
    Task ExpireAsync(CancellationToken ct = default);
}
```

> **设计要点**
> 1. 模型里**没有 reqid**（见 §5）
> 2. 算法用**中立字符串 + 完整截断名**（`"hmac-sha1-96"` 而非 `"sha1"`），因为 WFP 的截断内建在枚举里（§3.2）
> 3. `CipherAlgorithm == "null"` 时在 Windows 侧切成 `IPSEC_TRANSFORM_ESP_AUTH`，不要用空 key（§3.1）
> 4. `LifetimeSeconds` 是**必填非空**（Linux 侧无限期，WFP 必须给值，§4）

---

## 8. 下一步（B8 的实施顺序）

| 步骤 | 内容 | 产出 |
|---|---|---|
| **S1** | 建 `VoSharp.Ipsec` 项目 + §7 的模型 | 可编译的中立 SA 模型 |
| **S2** | 写 `VoSharp.Wfp` 的 P/Invoke 层：`FwpmEngineOpen0` / `IPsecSaContextCreate0` / `AddInbound0` / `AddOutbound0` / `GetSpi0` / `SetSpi0` / `Expire0` / `DeleteById0`，以及 `IPSEC_SA_DETAILS0` / `IPSEC_SA_BUNDLE0` / `IPSEC_TRAFFIC1` / `IPSEC_SA_AUTH_INFORMATION0` / `IPSEC_SA_CIPHER_INFORMATION0` / `IPSEC_V4_UDP_ENCAPSULATION0` 的结构体布局 | P/Invoke 声明 |
| **S3** | 实现 `WfpSaInstaller`（先只做 **transport 模式**）——它同时也是 B9（IMS sec-agree）的实现 | 先啃简单的 |
| **S4** | **R1 spike**：验证虚拟 IP 在 Windows 上的承载方式 | 决策依据 |
| **S5** | 实现 `WfpSaInstaller` 的 tunnel 模式 | B8 完成 |
| **S6** | 把 `VoWifiManager` 的 CHILD_SA 轮询改成 VICI `child-updown` 事件订阅，并在事件里调 installer | 接上回调 |

> **建议 S3 先于 S5**：transport 模式更简单（无 inner/outer 分离、无虚拟 IP 问题），
> 且它顺带把 B9（IMS sec-agree 的 4 条 SA）一起做了，可以先用 `SecurityDisabled` 之外的方式验证整条 SIP 链路。

---

---

## 9. 二版修订：上一版里凭函数名推错的两处

| 项 | 上一版（错） | SDK 实测（正确） |
|---|---|---|
| `IPsecSaContextCreate*` 的参数 | 传 **inbound** traffic | **`um/fwpmu.h:4386-4392` 传 `outboundTraffic`，出参是 `inboundFilterId`** |
| `FWP_BYTE_BLOB` 大小 | 未给（隐含 12） | **16**（`size` @0 + 4 字节填充 + `data` @8） |

其余结构体的大小也全部改为实测值，不再手推。

### 9.1 实测布局（`clang-cl` + Windows SDK 10.0.26100.0）

| 结构 | 大小 | 关键偏移 |
|---|---|---|
| `FWP_BYTE_BLOB` | 16 | size 0，data 8 |
| `IPSEC_SA_LIFETIME0` | 12 | 三个 uint |
| `IPSEC_AUTH_TRANSFORM_ID0` | 8 | authType 0，authConfig 4 |
| `IPSEC_CIPHER_TRANSFORM_ID0` | 8 | cipherType 0，cipherConfig 4 |
| `IPSEC_AUTH_TRANSFORM0` | 16 | id 0，cryptoModuleId 8 |
| `IPSEC_CIPHER_TRANSFORM0` | 16 | id 0，cryptoModuleId 8 |
| `IPSEC_SA_AUTH_INFORMATION0` | 32 | transform 0，authKey 16 |
| `IPSEC_SA_CIPHER_INFORMATION0` | 32 | transform 0，cipherKey 16 |
| `IPSEC_SA_AUTH_AND_CIPHER_INFORMATION0` | 64 | cipher 0，auth 32 |
| `IPSEC_SA0` | 16 | spi 0，saTransformType 4，union ptr **8** |
| `IPSEC_V4_UDP_ENCAPSULATION0` | 4 | local 0，remote 2 |
| `IPSEC_VIRTUAL_IF_TUNNEL_INFO0` | 16 | tunnelId 0，selectorId 8 |
| `IPSEC_SA_BUNDLE0` | 88 | lifetime 4，numSAs 40，saList 48，ipVersion 64，mmSaId 72，pfsGroup 80 |
| `IPSEC_SA_BUNDLE1` | 112 | 同上 + **saLookupContext 84 + qmFilterId 104** |
| `IPSEC_TRAFFIC0` | 56 | addr 4/20，trafficType 36，filterId 40，remotePort 48 |
| `IPSEC_TRAFFIC1` | 72 | addr 4/20，trafficType 36，filterId 40，remotePort 48，localPort 50，ipProtocol 52，localIfLuid 56，realIfProfileId 64 |
| `IPSEC_TUNNEL_ENDPOINTS0` | 36 | ipVersion 0，addr 4/20 |
| `IPSEC_GETSPI0` | 80 | traffic 0，ipVersion 56，udpEncap 64 |
| `IPSEC_GETSPI1` | 96 | traffic 0，ipVersion 72，udpEncap 80 |
| `IPSEC_SA_DETAILS0` | 168 | traffic 8，saBundle 64，udpEncap 152，filter 160 |
| `IPSEC_SA_DETAILS1` | 224 | traffic 8，saBundle 80，udpEncap 192，filter 200，virtualIfTunnelInfo 208 |
| `IPSEC_SA_CONTEXT0/1` | 24 | id 0，inboundSa 8，outboundSa 16 |
| `FWPM_FILTER0` | 200 | filterKey 0，layerKey 64，weight 96，action 128，filterId 176 |

复现方式：

```bash
cd voSharp/tools/wfplayout
INC="/c/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0"
clang-cl /nologo /W0 /GS- /D_WIN32_WINNT=0x0A00 /DWIN32_LEAN_AND_MEAN \
  /I"$INC/shared" /I"$INC/um" /I"$INC/ucrt" /Felayout.exe layout.c
./layout.exe
```

> **这些数字被 `tests/VoSharp.Wfp.Tests/WfpLayoutTests.cs` 在运行时断言**，
> 也被 `WindowsIpsecBackend.Open()` 里的 `VerifyLayouts()` 校验。改任何结构体声明，
> 这两处会立刻失败 —— 结构体偏移错了在 P/Invoke 边界不会抛异常，只会静默损坏内核对 SA 的理解。

### 9.2 官方函数签名（`um/fwpmu.h`）

```c
// fwpmu.h:3344-3350
DWORD FwpmEngineOpen0(const wchar_t* serverName, UINT32 authnService,
                      SEC_WINNT_AUTH_IDENTITY_W* authIdentity,
                      const FWPM_SESSION0* session, HANDLE* engineHandle);

// fwpmu.h:4386-4392  —— 注意是 outboundTraffic，出参是 inboundFilterId
DWORD IPsecSaContextCreate1(HANDLE engineHandle,
                            const IPSEC_TRAFFIC1* outboundTraffic,
                            const IPSEC_VIRTUAL_IF_TUNNEL_INFO0* virtualIfTunnelInfo,
                            UINT64* inboundFilterId, UINT64* id);

// fwpmu.h:4441-4446  —— SetSpi0 收的是 IPSEC_GETSPI1（不是 GETSPI0）
DWORD IPsecSaContextSetSpi0(HANDLE engineHandle, UINT64 id,
                            const IPSEC_GETSPI1* getSpi, IPSEC_SA_SPI inboundSpi);

// fwpmu.h:4468-4480
DWORD IPsecSaContextAddInbound1 (HANDLE, UINT64 id, const IPSEC_SA_BUNDLE1*);
DWORD IPsecSaContextAddOutbound1(HANDLE, UINT64 id, const IPSEC_SA_BUNDLE1*);

// fwpmu.h:4485-4488  优雅过期，XFRM 无对应物
DWORD IPsecSaContextExpire0(HANDLE, UINT64 id);

// fwpmu.h:4397-4400 / 4413-4417
DWORD IPsecSaContextDeleteById0(HANDLE, UINT64 id);
DWORD IPsecSaContextGetById1(HANDLE, UINT64 id, IPSEC_SA_CONTEXT1** saContext);

// fwpmu.h:4558-4571  SA 生命周期事件（Win8+），比轮询更合适做监控
DWORD IPsecSaContextSubscribe0(HANDLE, const IPSEC_SA_CONTEXT_SUBSCRIPTION0*,
                               IPSEC_SA_CONTEXT_CALLBACK0 callback, void* context,
                               HANDLE* eventsHandle);
```

### 9.3 已实现的范围与剩余缺口

**已实现**（`src/VoSharp.Wfp/WindowsIpsecBackend.cs`）：IPsec SA context、inbound SA、
outbound SA、SPI 固定、lifetime、优雅过期、删除、按 context id 查询。

**未实现 / 需在 R1 spike 中验证**：

- **隧道模式的 `tunnelPolicyId`**：`IPSEC_TRAFFIC1.trafficType = TUNNEL` 时，
  `tunnelPolicyId` 指向一个需要用 `FwpmIPsecTunnelAdd0`（`fwpmu.h:4283-4291`）预先创建的
  `FWPM_PROVIDER_CONTEXT0` 隧道策略。当前实现传 0，意味着**传输模式可用、隧道模式需要补这条路径**。
- **ePDG 虚拟 IP 的承载**：见 §6-R1。这是隧道模式能否工作的前提。

---

## 附：本文事实的出处

```
voCore/internal/vowifi/ike/child.go:255-277          ChildSAConfig 字段
voCore/internal/vowifi/ike/child.go:89-95            trafficSelector
voCore/internal/vowifi/ike/installer_linux.go:31-36  内核/用户态分流（NAT-T → 用户态）
voCore/internal/vowifi/ike/installer_linux.go:68     reqid = InboundSPI
voCore/internal/vowifi/ike/installer_linux.go:77-120 link/address/state 命令
voCore/internal/vowifi/ike/installer_linux.go:147-178 policy 命令
voCore/internal/vowifi/ike/installer_linux.go:181-203 端口范围 → 报错
voCore/internal/vowifi/ike/installer_linux.go:255-264 算法与 encap（encap 为死代码）
voCore/internal/vowifi/ike/installer_linux.go:281-333 teardown 顺序
voCore/internal/vowifi/ike/userspace_linux.go:21     MTU = 1380
voCore/internal/vowifi/ike/userspace_linux.go:279-358 独立路由表 + ip rule
voCore/internal/vowifi/ike/userspace_linux.go:360-372 table/priority 由 SPI 派生
voCore/internal/vowifi/ike/esp.go:23                 用户态 replay window = 64
voCore/internal/vowifi/ike/esp.go:209                序号耗尽 → "rekey is required"（未实现）
voCore/internal/vowifi/ims/security.go:41-63         IPSecSAConfig 字段
voCore/internal/vowifi/ims/security.go:488-542       4 条 state 命令
voCore/internal/vowifi/ims/security.go:522-527       ealg=null → enc cipher_null ""
voCore/internal/vowifi/ims/security.go:582-678       6 条 policy 命令
voCore/internal/vowifi/ims/security.go:680-700       reqid 计算与碰撞处理

Windows SDK 10.0.26100.0 shared/ipsectypes.h:71-87     IPSEC_SA_LIFETIME0 / IPSEC_TRANSFORM_TYPE
                                          :90-99       IPSEC_AUTH_TYPE
                                          :102-109     IPSEC_AUTH_CONFIG
                                          :125-133     IPSEC_CIPHER_TYPE
                                          :137-145     IPSEC_CIPHER_CONFIG
                                          :295-367     IPSEC_TUNNEL_ENDPOINTS0 / IPSEC_TUNNEL_POLICY0
                                          :528-560     IPSEC_SA_SPI / AUTH+CIPHER INFORMATION / IPSEC_SA0
                                          :649-669     IPSEC_SA_BUNDLE0
                                          :698-751     IPSEC_TRAFFIC_TYPE / IPSEC_TRAFFIC0 / IPSEC_TRAFFIC1
                                          :755-759     IPSEC_V4_UDP_ENCAPSULATION0
                                          :783-800     IPSEC_SA_DETAILS0
```
