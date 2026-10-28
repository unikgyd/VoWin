# Linux 客户端选型与双端整合

本次最新决定：**VoCat-sip（Go）作为 Linux 补齐目标**，主要参照成熟的
**VoWin／VoSharp（C#）**，同时覆盖通话、多模组恢复、管理与通知，目标为能力并集。
当前矩阵与未完成项见[能力并集清单](../VoCat-sip/docs/FEATURE_UNION.md)。
此前 [Rust 主线恢复](RUST_LINUX_MAINLINE.md)与 SimAdmin 阶段保留为历史参考。
Asterisk 保持可选，不能用部署 PBX 代替客户端自身的网关能力。

此前选择 VoCat 主要基于可见源码的集成程度：VoCat 已把逐设备运行时、SIM 适配、IKE、IMS 注册、
短信、通话信令和浏览器媒体接口接到业务层；当前 SimAdmin fork 的原生协议基础和内线
网关已有测试，但运营商业务与媒体仍未贯通。该技术比较不抵消 VoCat 许可与当前发布目标
的差异，部署范围仍需遵守原项目许可。官方 SimAdmin 另有 VoLTE／VoWiFi 增强版，
其私有源码不在本 fork 中，不能把本地差距说成官方没有这些功能。

## 对照范围

2026-10-26 实卡更新：[原生 Linux DITO 注册检查](../publish/linux-dual-modem-20261026/README.md)
已真实执行两轮注册、发现保持和重连；注销均返回 SIP 500，包括等待 20 秒后的复测。
本地释放与原状态恢复核对通过，不掩盖整轮失败。中国卡受原地区准入限制未接管 Host IMS，
因此尚不能称为 Linux 双注册验收；没有拨号、发短信、音频操作或 Asterisk 改动。

2026-10-26 最新：[双端最终 AUTH 拒绝](../publish/ike-final-rejection-20261026/README.md)软件阶段接受。
当前初始／最终 AUTH 才能授权记录拒绝，不能借用其他地址族认证；两端握手、Windows
后端与实际 Manager／SQLite 已接通。原始 RED、原字节新增测试、完整回归及旧通过／跳过、
1,664／4,126 输入和八种 Linux 构建的实际网页嵌入复核通过，未替换现用部署。
Windows 双模组实测另立记录，不能据此宣称 Linux 实卡、USB 媒体或整体双端完成。

2026-10-26 当前：[Linux IKE 状态 UI](../publish/ike-status-ui-20261026/README.md)已接真实
投影／SQLite／设备 API 和网页，前端 75 项三轮与合成交互通过。显示按当前卡和设备绑定，
历史记录不能推断重试许可；补入实际门禁所有作用域回归后，38 主测试／36 子例各三轮及
1,663 输入专项复核通过。第二轮完整 28 阶段及八种构建、实际网页嵌入与最终复核通过，
仅此 UI 软件阶段接受。首次管理员初始化超时和原断言／期限保留，不宣称发布或现网验收完成。

2026-10-26 后续：[Windows IKE 状态展示](../publish/windows-ike-status-20261026/README.md)
已补上按卡的只读状态投影和绿／黄／红灯，完整回归及 WPF 合成渲染通过。
本阶段没有改 Go；其管理页对应状态仍需核对／补齐，不宣称两端 UI 已经完成对齐。

2026-10-26 后续：[C# 桌面／CLI 持久 owner](../publish/windows-ike-owners-20261026/README.md)
已共用当前 Windows 用户的 Preferences 数据库和一个 SQLite 实现。完整回归、关键三轮、
独立进程与 x64 framework-dependent 发布核对通过，CLI 持久化不再整体列为未接线。
本阶段 Go 1,654 输入核对未变，不是新的 Linux、实卡或生产部署验收。

2026-10-26 后续：[ePDG IP 别名阶段](../publish/ike-epdg-alias-20261026/README.md)的完整
19 阶段及最终复核通过；[实际逐包阶段](../publish/ike-transport-peer-20261026/README.md)
继续补齐直连／SOCKS 内部候选、重传、COOKIE、实际应答端点和清理。扩展 8 主测试／26 子例
各三轮 race 与专项复核通过；完整 19 阶段、八种构建及最终复核通过。
Windows [持久门禁](../publish/windows-ike-retry-20261026/README.md)已接入产品链路，专项及
跨进程计时／Unknown 与唯一准入验证通过，完整回归／关键三轮／最终复核通过；两端生命周期／CLI／其他
策略和实卡联合验收仍未完成。

2026-10-26 后续：[双端 Tw3／拒绝证据](../publish/ike-backoff-evidence-20261026/README.md)
已完成完整软件回归及最终复核：Windows IKE 268／核心 1,749／桌面 673 项，Linux
原生／静态 1,859／1,857 主测试、关键三轮与完整相关包 race、八种构建通过，原跳过保留。
当前仍缺持久自动重连门禁；后续[真实编排 RED](../publish/ike-retry-gate-20261026/README.md)
已确认 Enable／Retry／Reconnect 与关开线路可绕过限制，5 个失败和 2 个对照各三轮留档，
历史保留，Go 后续持久门禁已接入并通过完整回归与最终复核，后续 IP 别名／实际传输
候选发送边界仍在补齐；Windows 门禁和
物理生命周期／联合验收仍待完成。这不改变选型，也不意味着现用部署完成。

2026-10-26 最新：[双端 COOKIE 阶段](../publish/ike-cookie-20261026/README.md)已完成软件
验证与最终独立复核，包括 Go 实际 provider 的混合错误回退修复。Linux 修订版原生／静态
1,851／1,849 主测试、关键三轮及完整相关包 race、八种构建通过；Windows 独立回归通过。
该阶段未部署现用服务或操作实卡。后续[初始 AUTH 复查](../publish/ike-responder-auth-20261026/README.md)
已分别复现并接入 C# 初始 AUTH 和 Go 通用签名修复；双端完整软件回归、关键三轮及
最终独立复核通过。该 AUTH 阶段 Windows IKE 221／核心 1,747／桌面 673，Go 原生／静态
1,855／1,853 主测试；不能称为所有协议细节一致，具体实现／撤销检查边界以该阶段记录为准。

2026-10-26 后续：已反向补齐 [Windows IKE 回退证据](../publish/windows-ike-retry-proof-20261026/README.md)，
将 Linux 的挑战／退避／清理边界补到 C# 实际路径，完整软件验证通过。该阶段 Go 源码未变，
不是新 Linux 测试；COOKIE、退避计时、媒体及实机差距继续保留，不以此替代最终双端验收。

2026-10-26 进展：[EC20 双来源 IMSI／同卡成功历史](../publish/profile-identity-20261025/README.md)
已通过全量原生／静态顶层 1,845／1,843 项、关键三轮与完整相关包 race、八种构建及
独立复核；不是已发布或实卡通过。该阶段不补原生 QMI 双来源／多卡槽，不改变联合验收门禁。

2026-10-23 开始以新克隆的 `VoCat-sip` 为准，基线为
`06e94ed9c035082421452d9d33905a6942161b41`；SimAdmin 公开上游基线为
`e23311d14c63ca529273486cece67adad37b0311`，另包含本地 Rust 开发改动。
旧 `references/VoCat` 的审阅范围不能替代这个新版本的代码审阅。

| 能力 | VoCat 当前源码 | 相对当前 SimAdmin fork 的判断与待补项 |
| --- | --- | --- |
| 多模组独立运行 | `internal/vowifi/runtime/manager.go` 按设备保存编排器、重试及维护状态 | VoCat 更接近现成的逐模组业务入口；仍需验证真实双模组和跨模式隔离 |
| VoWiFi | IKE、AKA、CHILD_SA、用户态 ESP／Linux 安装器与 IMS 已串接 | 选 VoCat 的主要理由；单元测试不等于运营商验收 |
| ePDG 与双栈 | 后续已补按卡高级覆盖、多 PLMN、A／AAAA、预认证接入回退及成功记忆；三种 reader 的未知 MNC 计划、QMI EF_IMSI／EF_AD 校验、同归属 IMS 无响应重建及[内层 CP／TS 地址族计划](../publish/inner-family-20261025/README.md)已接入，最新全量以阶段记录为准 | Windows 已反向补齐[未响应网络故障的证明与清理边界](../publish/windows-ims-gateway-proof-20261025/README.md)；EC20 双来源／同卡身份历史后续已接入，其他 reader 的多来源、完整归属文件、多卡槽、逐 IP 诊断及其他异常策略仍待完成；Linux 历史证据见[网关重建记录](../publish/ims-gateway-retry-20261025/README.md) |
| Host IMS | 后续已接入独立蜂窝 Host IMS 后端、逐模组模式及业务选路，见[应用接入](../VoCat-sip/docs/HOST_IMS_APPLICATION.md) | 不等于模组内置 IMS；承载／AKA／ESP／媒体已软件接线，实卡、固件、多模组及冷启动仍待验收 |
| 媒体 | 有 RTP／浏览器 PCM；[默认 glibc 构建与 AMR 自检](../VoCat-sip/docs/AMR_MEDIA.md)已接入，静态回退版明确为 G.711 | 原生 AMR 已有回环；正式发布、真实其他硬件、musl 原生 AMR、AMR-WB 编码及运营商验收仍待完成 |
| SIP 服务端与 Asterisk | 后续已实现本地注册、分机 MESSAGE、内部 B2BUA／G.711、手机短信桥、设置 UI 及[可选固定中继](../VoCat-sip/docs/ASTERISK_RELAY_PLAN.md) | 内部注册、文本、群振／首接取消、双方挂断、短信桥与 RTP 已回环验证；真实软电话、跨网及运营商联合验收仍待完成 |
| 短信与通知 | IMS／蜂窝短信、数据库、通知及交互已接入；后续补上可选手机短信与 SIP MESSAGE 桥、独立权限、持久化防重及离线队列；[只读 Token API](../VoCat-sip/docs/SMS_API.md)亦已接入 | 实卡／实际软电话、长消息互通及真实 API 调用端仍待验收；验证码评分仅共享样例对齐，不宣称任意文本完全一致 |
| 工程交付 | Go 单服务、嵌入式 Web、SQLite、systemd／Docker | 适合 Linux 服务；仍需配置迁移、依赖审计、恢复、快速退出和实机验收 |

主要源码入口为开发工作区中的
[逐设备运行时](../VoCat-sip/internal/vowifi/runtime/manager.go)、
[IMS 业务](../VoCat-sip/internal/vowifi/ims/provider.go)、
[电话选路](../VoCat-sip/internal/server/call_api.go)、
[媒体](../VoCat-sip/internal/vowifi/ims/rtp_media.go)和
[ePDG 发现](../VoCat-sip/internal/vowifi/ike/epdg_resolver.go)。

## 本轮已落实

本节保留 2026-10-23 初始审阅与修复记录；后续实现以当前对照表、VoCat 对齐清单和
各阶段记录为准，不再把当时未实现的能力当作当前源码缺口。

VoCat 不再将未实现的 AMR／AMR-WB 或 DTMF 宣告为 PCM 桥接能力。只选择实际实现的
PCMA／PCMU、8000 Hz、单声道；未知格式、错误时钟、声道和静态 PT 重定义不能落入
μ-law 分支。缺少 AMR 后端而对端只有 AMR 时明确失败，不把“接通但噪声”当成功。
后续已接入[可选 AMR-NB 原生后端](../VoCat-sip/docs/AMR_MEDIA.md)，但未完成默认发布
依赖与运营商联合验收，并未把最终目标缩小成仅支持 G.711。

VoWin 同步补强实际媒体设置路径的声道、完整 rtpmap、重复冲突和静态 PT 检查，支持
对端动态 PT 的 G.711。两端与 Rust 参考共享八条 G.711 数学样例，并检查全幅 16 位 PCM
的符号和量化误差。新实现依据协议行为独立编写，不跨许可复制 VoCat 的协议实现。

此前在途的 Rust 工作已收尾：隧道继承握手后的本地 IKE Message ID，支持主动 DPD、
相同密文重传与有时限的本地主动 DELETE。Windows 同步修正 DELETE 的所属 SPI 检查，
并修复未浮动 NAT-T 时零开头 IKE SPI 被当成封装标记的问题。该部分保留作参考。

后续已新增[可选 SIP 注册层](../VoCat-sip/docs/SIP_GATEWAY.md)：默认关闭，独立分机账号、
SHA-256／MD5 Digest、实际 UDP 来源记录、续期／注销、设置 API 和中英 UI。启动恢复
已保存配置，退出只清理本服务；后续又接入可选分机 MESSAGE 和内部 G.711 双腿通话。
运营商网关仍未完成，分机文本不是手机短信。Windows 同步修复响应身份匹配，双端
共享九条身份契约。最新内线通话范围见 [SIP_CALLS](../VoCat-sip/docs/SIP_CALLS.md)。

运营商客户端的[通话生命周期加固](../VoCat-sip/docs/IMS_CALL_LIFECYCLE.md)也已接入：
逐会话单通话准入、并发接听互斥、重复 INVITE 重放、取消与迟到接通清理、
ACK 恢复及有界写入。它是外线桥接前置修复，不代表选线、外线群振、PCM 桥或
Asterisk 中继已经完成。现有 C# 用例另行回归，本阶段未更改其协议实现。

随后已接入[可选逐模组 IMS 外呼](../VoCat-sip/docs/CARRIER_OUTGOING.md)：显式路由与权限、
持久化尝试防重及 PCM／RTP 桥，在合成后端上验证独立并行、取消、双方挂断与重启。
它只使用已注册 VoWiFi IMS。随后接入[入呼群振及 HTTP／分机首接仲裁](../VoCat-sip/docs/CARRIER_INCOMING.md)，
包括逐线接收账号、迟到接听排空及 PCM 双向桥；随后补上[网页电话及跨页提醒](../VoCat-sip/docs/WEB_PHONE.md)。
Host IMS、USB 语音、可选 Asterisk 协议中继和真实浏览器／运营商验收仍待完成；Asterisk 分机管理已单独部署。
后续新增[Host IMS 协议前置层](../VoCat-sip/docs/HOST_IMS.md)，完成独立蜂窝承载接口、
LTE PANI、强制安全、QMI 双栈 P-CSCF 解码和已确认资源回滚；生产接管／SIM 适配器／
模式 UI 仍未完成，不能当作 Linux Host IMS 已能启用。后续已实现
[ModemManager 承载所有者](../VoCat-sip/docs/HOST_IMS_BEARER.md)及真实 D-Bus／SQLite／进程锁
验收，仍未接入业务编排；实际模组只进行了只读发现，没有创建实卡连接。
另已完成[蜂窝 SIM 事务／严格 QMI 协议适配器](../VoCat-sip/docs/HOST_IMS_SIM.md)，
并将 ISIM 解析与通道清理缺口修回 C#；后续已有[生产 Linux 端点 opener](../VoCat-sip/docs/HOST_IMS_ENDPOINT.md)，
业务接线、真实 UICC／注册和模式 UI 仍待完成。
后续已有[自有主机网络租约](../VoCat-sip/docs/HOST_IMS_NETWORK.md)，隔离真实内核
验证双栈地址、独立策略表、设备绑定、媒体引用和资源保护；尚未完成生产 WDS
P-CSCF 来源、SIP／RTP 接线及跨重启网络恢复，不代表已有可启用的 Host IMS 线路。
后续又补上[USB 自有 WDS／P-CSCF 来源连接器](../VoCat-sip/docs/HOST_IMS_RUNTIME.md)，
包含真实私有代理、USB function／mux 证明与网络复用时的来源核验；实际固件支持、
native MHI／PCIe、DNS-only、业务模式、真实 SIP／RTP 与恢复编排仍待完成。
Linux 测试仅部署到用户指定的 `root@10.0.0.2` 隔离目录，不使用 WSL，不改现用 Asterisk。

测试和源码记录见[本轮验证](../publish/vocat-alignment-20261023/README.md)。没有替换现用
VoWin、安装 VoCat 服务、启用真实 SIM 测试，或写入生产 Asterisk 配置。

## 实施顺序与完成条件

1. 完成 VoCat 原生 AMR 的默认发布、SDP／RTP／RTCP 边界和 ePDG 双栈候选策略，再把
   Windows 对应遗漏同步补齐。注册状态与媒体可用状态分开。
2. 新增可选 SIP 服务端及 Asterisk 对接：分机认证、内部互拨、来显、群振、首接取消、
   迟到接通清理、双腿挂断与双向媒体。不得固定 IP、账号、前缀或运营商号码。
3. 接入逐模组 Host IMS 与模组音频桥接，允许每个模组独立选模式、停止或恢复。
4. 接通短信网关、只读验证码 API、通知与交互权限；不确定收费请求不自动重放。
5. 完成真实双模组、重启恢复、热插拔、跨网段软电话、现用 Asterisk 和发布包验收。

## 许可和运行边界

VoCat 是自定义 Research & Evaluation 许可，不因为 README 使用 open-source 一词就
具有 GPL 的使用范围。用户确认会依其许可实施；本轮保留原 LICENSE 和全部授权校验，
未删除或绕过地域、评估期、设备数量及 SIM 限制。非商业用途本身不替代其他适用条件，
额外用途仍以原许可或权利人的额外授权为准。

功能改动记入 VoCat 的 NOTICE，原许可文件未变；不会把其受限源码直接混入 GPL 仓库。
原文见 [VoCat LICENSE](../VoCat-sip/LICENSE) 与
[上游许可](https://github.com/MengMengCode/VoCat/blob/master/LICENSE)。
