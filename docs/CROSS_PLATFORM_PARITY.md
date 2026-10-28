# Windows Linux 与 Asterisk 对齐

本次最新要求重新指定 **VoCat-sip 为 Linux 端**，以成熟 VoWin 为主要基准，补齐通话、
多模组恢复、管理与通知，并保留两边已有能力。当前范围以
[能力并集清单](../VoCat-sip/docs/FEATURE_UNION.md)为准。此前 Rust 方向和 SimAdmin
成果保留为参考，历史 Go／C#／Rust 测试互不替代；Asterisk 继续可选。

2026-10-26 实卡更新：[Linux 注册检查](../publish/linux-dual-modem-20261026/README.md)
证明 DITO 两轮真实注册、发现保持与重连成功，但注销持续 SIP 500，整轮仍未通过。
本地连接释放／恢复已核对；中国卡受现有地区准入限制未接管 Host IMS，Linux 双注册、
远端注销及通话媒体仍保留，不用软件回归或 Windows 实测代替这些项目。

2026-10-26 最新：[最终 MSK AUTH 拒绝](../publish/ike-final-rejection-20261026/README.md)软件阶段接受。
Go／C# 握手、Windows Manager、当前／历史认证归属都有原始 RED；修复及原字节新增测试
已接入，双端完整回归、旧通过／跳过集合、输入归档及八种构建／实际网页嵌入复核通过。
这不是整体双端完成或部署；[Windows 服务层双模组实测](../publish/dual-modem-carrier-20261026/README.md)
已通过选定的双注册、发现／停止、真实恢复隔离及同链路真实续注册检查，末次注销成功，
早期注销未响应仍保留观察。Linux 实卡、完整物理生命周期及 USB 双向语音仍未完成。

2026-10-26 当前：[Linux 状态界面阶段](../publish/ike-status-ui-20261026/README.md)已补实际
持久观察／设备 API、逐模组状态、可收起的诊断及不会重置限制的「重新检查」。前端 75 项
各三轮和合成浏览器交互通过；实际门禁回归另发现作用域拼写／未绑定作用域遗漏，修正后
38 主测试／36 子例各三轮及 1,663 输入专项复核通过。第二轮完整 28 阶段、原生／静态
1,904／1,902、完整相关包 race、八种构建及实际网页嵌入复核通过，本 UI 软件阶段接受。
首次管理员初始化超时不隐去，也没有替换现用服务；完整生命周期、Linux USB 语音及联合
实网验收仍保留，不将 UI 接受扩大成整体双端完成。

2026-10-26 后续：[Windows IKE 状态投影](../publish/windows-ike-status-20261026/README.md)
已接服务、逐卡缓存、总览／模组列表和 VoWiFi 卡片；等待黄灯、暂停红灯，详细原因按需显示。
19 个新增例、全量 274／1,802／700、关键三轮、构建和实际 WPF 合成图像核对通过。
当时尚未覆盖的 Linux 对应管理页已由上段独立阶段补齐，不以协议状态代替界面接线证据。
完整物理生命周期和双端实网验收仍保留；该 Windows 阶段 Go 全部 1,654 输入未改。

2026-10-26 后续：[桌面与 CLI 共用持久记录](../publish/windows-ike-owners-20261026/README.md)
补齐独立 CLI 入口，沿用当前用户桌面数据库与共享 SQLite 实现；不是另建内存／独立账本。
4,120 输入全量、23 个新增用例、关键三轮、双方向独立进程及八进程唯一准入通过；
全量 IKE 274／核心 1,798／桌面 685、构建与发布核对通过，原跳过保持。
仅 C# 侧改变；Go 1,654 输入核对未变。完整物理生命周期、状态 UI 和联合实网验收仍待完成。

2026-10-26 后续：[Windows 持久门禁](../publish/windows-ike-retry-20261026/README.md)已从
草案进入真实桌面 SQLite／Kernel／每模组 Manager／自动恢复与 IKE 逐包／拒绝回调。
4,116 输入专项及跨进程计时／Unknown、八进程唯一准入验证通过；完整 IKE 274／核心
1,781／桌面 679 项、关键三轮、Release 构建与最终复核通过，原跳过保留。
不再将 Windows 门禁整体列为未接线；物理生命周期、CLI／其他策略和实卡验收仍待完成。

2026-10-26 后续：[IKE 实际发送与应答 peer](../publish/ike-transport-peer-20261026/README.md)
已接入 direct／SOCKS 的逐次 INIT 检查及回退后端点更新。新增 8 主测试／26 子例各三轮
race 与专项复核通过，含真实 UDP 回退＋签名加密拒绝／DELETE；完整 19 阶段、八种构建及
最终复核通过。Windows 4,107 输入未改，不能把 Go 传输修复当作 Windows 门禁完成。

2026-10-26 后续：[ePDG IP 别名](../publish/ike-epdg-alias-20261026/README.md)已补顶层实际
IP 的持久匹配，专项三轮及完整 19 阶段、八种构建与最终复核通过；下层实际传输由上方
后续阶段独立接线和验证。Windows 对等门禁仍待接线，不能以顶层 fake datagram 的通过
代替真实传输路径覆盖。

2026-10-26 后续：[Linux 持久 IKE 门禁](../publish/ike-retry-gate-20261026/README.md)
已接到生产工厂、实际 Orchestrator／provider 及 runtime，包含发送前持久尝试、拒绝同步
提交、SQLite CAS 和单调计时。20 个新增主测试／41 子例各三轮 race 与专项复核通过，
原生命周期失败保持断言后通过；原生／静态 1,879／1,877 主测试、关键三轮、完整相关包
race、八种构建及最终复核通过。本轮 Windows 4,107 输入未变，
没有冒充 C# 门禁已经接线，也未部署或操作实卡。

2026-10-26 后续：[双端 IKE 退避证据](../publish/ike-backoff-evidence-20261026/README.md)
已修 C# Timer 3 单位并接入当前已认证 SA 的结构化原因、作用域及严格计时器状态，
共享 22 条样例。完整 Windows、Linux 原生／静态、关键三轮及完整相关包 race、八种构建
与最终双端复核通过；原跳过保留。
这不是持久重连门禁：作用域绑定、进程重启、取消期间提交及真实卡事件尚待接线；
本轮没有替换现用部署或操作实卡，整体双端目标继续保留。
后续[真实编排 RED](../publish/ike-retry-gate-20261026/README.md)确认 Enable／Retry／Reconnect
和关开线路此前能绕过限制；当时原源码不变，三轮每轮 5 个失败及 2 个通过对照保留。
Go 后续接线与完整回归见上方，细化传输边界及 Windows 门禁仍待完成，不再将 Go 编排列为完全未实现。

2026-10-26 最新：[初始 ePDG AUTH 双端修复](../publish/ike-responder-auth-20261026/README.md)
已接到 C# 真实首轮 AUTH／ePDG 名称／当前尝试状态，并修 Go 通用签名、SHA-2 通知及
两端签名算法／证书 IKE 用途。EAP-only 保留，当前认证不继承给后来的 SA。
Windows IKE 221／核心 1,747／桌面 673 项、关键三轮与构建通过；Linux 原生／静态
1,855／1,853 主测试、关键三轮及完整相关包 race、八种构建通过，最终独立复核通过。
不含撤销／OCSP、私有根桌面配置、运营商退避或真实联合验收，现用部署未替换。

2026-10-26 后续：[双端 IKE_SA_INIT COOKIE](../publish/ike-cookie-20261026/README.md)
已补非零 SPI／空 SPI Protocol ID 兼容、异常消息形状及相同初始载荷的有界刷新。
Go 实际 provider 先验证 COOKIE，再分类提案错误，混合消息不再获得换接入点／算法许可。
Windows IKE 188／核心 1,744／桌面 673 项、关键三轮与构建通过；Linux 修订版原生／静态
1,851／1,849 主测试、关键三轮与完整相关包 race、八种构建通过，旧跳过集合保留。
最终独立复核通过；现用部署未替换，Asterisk 声明输入未变，整体双端目标仍未完成。
[初始 AUTH 修复](../publish/ike-responder-auth-20261026/README.md)另有真实 RED，后续已接入
源码并通过双端完整软件回归，具体认证边界见该阶段；IKE 退避时长／自动重连、
其他 reader／USB 媒体与运营商联合验收继续保留，不把单端结果当作全部对齐。

2026-10-26 后续：[Windows IKE 状态证明反向补齐](../publish/windows-ike-retry-proof-20261026/README.md)
已接入真实 IKE、C# 后端及 Manager；去掉文字授权，精确保留身份／挑战、退避、失败清理与
地址族间的最终认证状态，并补非零 SPI 初始提案拒绝。IKE 161／核心 1,744／桌面 673 项、
关键 93／251／6 项各三轮及 Release 构建通过，原跳过与旧测试不改，独立复核通过。
Linux 1,630／Asterisk 60 个完整声明输入核对未变，未重跑其历史结果或替换现用部署。
COOKIE、IKE 退避时长与自动重连、完整 reader／USB 媒体及运营商联合验收仍未全部完成。

2026-10-26：[Linux 同卡多身份](../publish/profile-identity-20261025/README.md)
已补 EC20 双来源、按卡身份历史、成功优先及 EAP／SIP 线上身份与物理 AKA／业务来源分离。
新增精确的身份交换拒绝证明，并阻止畸形挑战、退避或清理失败被包装成普通接入回退。
全量原生／静态顶层 1,845／1,843 项、关键 95 顶层／142 子例三轮 race、完整相关包
658 顶层／817 子例 race、八种构建通过，原跳过保留；独立复核通过。
该阶段只改 Go；Windows 4,093／Asterisk 60 个声明输入核对未变，不重跑其历史结果。
Windows 更细回退策略、原生 QMI 多来源／卡槽与实卡联合验收仍待完成，现用服务未替换。

2026-10-25 后续：[Linux 内层 CP／TS 地址族计划](../publish/inner-family-20261025/README.md)
已从 Windows 请求顺序补齐到真实 provider，保持同 IP／代理／认证归属及先清理后重建。
最终 AUTH 验证前的 CP 不提供重试证明；混合拒绝、退避、格式、mapped IPv6 和越界策略
有独立拒绝测试；CP 保留位与空 SPI 的 Protocol ID 按标准忽略，不把严格拒绝当作互通。
原生／静态顶层 1,817／1,815 项、关键 66 顶层／107 子例各三轮及八种构建通过；
Windows 4,093／Asterisk 60 个声明输入未变，只做源码核对。仍不能宣称已部署或运营商双端通过。

2026-10-25 后续：[Windows IMS 证明边界反向补齐](../publish/windows-ims-gateway-proof-20261025/README.md)
已接入真实 REGISTER／VoWiFi 启动失败路径：关联响应证据保持单调，响应与静默原子互斥，
只有发送／等待路径的未响应网络故障获得证明；清理成功后才提交失败端点，同次已验证
接入不切换 MNC／IMSI。核心 1,737／桌面 673 项、244／6 项关键各三轮及构建通过，
原 5／17 项跳过保留；原始失败和修复过程均留档。Linux 1,614／Asterisk 60 项输入未变，
不冒用其历史测试作为本轮执行，也不宣称双端异常模型、退出或运营商验收全部一致。

2026-10-25：[Linux IMS 接入故障后的网关重建](../publish/ims-gateway-retry-20261025/README.md)
参考 Windows 既有故障转移，补齐同归属逐 IP 排除、网络层重建、预算和迟到监听器隔离。
实际 SIP provider 只有未收到匹配响应的已识别网络错误才签发重建证明，`100 Trying` 同样
阻断“无响应”回退；该阶段尚缺的 Windows 更细边界已由上方后续阶段补齐。Linux 原生／静态顶层
1,801／1,799 项、关键路径三轮及八种构建通过，旧测试逐字节不变，现用部署未替换。

2026-10-25：[Linux 读卡入口对齐](../publish/sim-reader-home-20261025/README.md)
将 PC/SC 和原生 QMI 的未知长度接到此前的归属计划，补上读卡／AKA 前的身份检查、失败
不发布绑定和临时归属不外送开通接口。QMI 直接读 EF_AD 并检查逻辑通道内的 IMSI，
不使用 OPL／默认两位长度。Windows 与 Asterisk 代码未改，仍待真实固件、多卡槽及双端联合验收。
本轮 Linux 原生／静态顶层 1,789／1,787 项、关键路径三轮、八种构建通过；Windows 与
Asterisk 只核对源码未变，不把历史测试算作本轮执行，实卡跳过和许可控制保持。

2026-10-24 后续：[Linux EC20 未知 MNC 计划](../publish/epdg-mnc-plan-20261024/README.md)
已按 Windows 已有行为补齐合法两／三位前缀和端点／认证归属绑定，接入实际编排、代理、
EAP／SIP 身份与同卡成功记忆。C# 新增共享样例回归，协议实现未改；其他 reader、
多 IMSI、IMS 超时端点重建和运营商双端验收仍待完成。

2026-10-24 后续：[SIM 归属／ePDG 修复](../publish/epdg-sim-plmn-20261024/README.md)
已双向补齐部分读卡细节：Go 接入正确的 EF_EHPLMN 多候选，分离发现与鉴权、检测读取期间
身份变化；C# 修正 EF_AD 第 4 字节与 `91xx`。两端共用 27 条解析向量，不声称未知 MNC
轮试、多来源 IMSI、完整归属文件及运营商双端验收已全部一致。

2026-10-24 后续：Linux [AMR 默认构建、静态回退及媒体自检](../VoCat-sip/docs/AMR_MEDIA.md)
已接入源码和发布工作流，补齐此前仅开发标签可用的差距。状态灯基于真实本地编码＋解码，
不把注册或库文件存在当作语音就绪；正式发布和实卡联合验收仍独立保留。
具体软件、架构和容器验证见[阶段记录](../publish/vocat-amr-release-20261024/README.md)。

2026-10-24 后续：[VoCat Go 短信／验证码只读 API](../VoCat-sip/docs/SMS_API.md)已补上
Windows 已有的独立 Token 接口，含默认关闭的监听器、持久摘要、管理员设置及中英 UI。
不借用管理会话、SIP 或机器人权限，不操作模组或修改已读状态。
查询／Unicode／验证码共享样例不等于启发式评分完全相同，也不等于实卡验收。
本轮 Linux 软件测试与仍未完成项见[阶段记录](../publish/vocat-sms-api-20261024/README.md)。

2026-10-24 后续：Windows 已反向补齐[首次准入回复号码快照](../publish/windows-sms-canonical-20261024/README.md)。
修复原线路回复仍使用当前国家码补号的问题；实际桌面、SQLite 与 UDP 证明原 +86 源不在
重启为 +63 后重解释，历史国内号码只读、原卡／付费权限与 Unknown 防重保持。
原始失败及两处旧测试行为修订均保留；本轮核心 1,711／桌面 673、原 5／17 跳过、
306／138 关键各三轮与 Release 构建通过。软件验证不是双端运营商验收。

2026-10-24 后续 Windows：[部署 v2 导入与独立 SIP 权限](../publish/windows-deployment-v2-20261024/README.md)
已接入真实 reader、设置页和 runtime。主／附加本地账号、固定 peer 的通话／短信／线路及
模组接收名单分离；null 兼容旧配置，空名单明确拒绝，显式禁主账号短信不会因保存被省略。
原卡回复碰到同名数字账号仍走原 SIM；peer 不借用同名账号权限或速率预算。
新十阶段：核心 1,574／桌面 626、原 5／17 跳过，169／224 项关键各三轮，Release 零警告／错误。
这不是生成器／管理界面或真实 PBX／WPF／运营商联合验收；Go 1,559 个输入未变，明确引用
其历史已接受结果，不声称本轮重跑 Linux。完整目标与后续缺口仍以下文和阶段记录为准。

目标是让 VoWin 与 Linux 客户端都能独立管理多个模组，提供可选的 Host IMS、模组语音桥接、
VoWiFi、SIP 电话和短信网关，并使用同一套 Asterisk 对接规则。两个仓库都需要修改和验证；
不能把 Linux 的诊断页面、AT 拨号命令或配置结构当成完整的 IMS 与双向媒体实现。

2026-10-23 用户要求重新选型，当前 Linux 功能整合主线为 VoCat-sip（Go），Windows 继续
C#，SimAdmin-sip（Rust）保留作参考。两端共享行为契约和测试样例，不以 .NET 组件作为
Linux 运行依赖。下表和历史章节仍描述 SimAdmin Rust 参考实现，不能当成 VoCat 的现状。

最新双向补强：Windows 的实际桌面入口现已接入
[SIP 短信 SQLite 持久防重](../voSharp/docs/SIP_SMS_DURABILITY.md)，发送前提交未知状态，
存储失败不发送；同时按 Go 侧的原事务校验原则收紧 IMS CANCEL 身份及异步回调归属。
这是有界提交准入与协议修复，不是送达保证或真实通话验收。Go 的
[可选固定 Asterisk 中继](../VoCat-sip/docs/ASTERISK_RELAY_PLAN.md)第一阶段已通过隔离 Asterisk 验证，
不能将其等同运营商实网验收；当前 Go 能力见其
[对齐状态](../VoCat-sip/docs/VOWIN_PARITY.md)。本轮未操作现用服务或实卡。

随后反向补齐 Windows：固定 peer 双向独立凭据、精确 Realm／算法固定、本地 Registrar 共存，
INVITE／MESSAGE 同一总期限和迟到 To-tag 清理；桌面旧中继保留关闭草稿，V2 DPAPI 完整绑定，
不自动猜 Realm／复用共享密码。软件证据见 `publish/csharp-peer-parity-20261024`，尚未部署现用程序。

最新继续补齐 Windows [SIP 短信原卡回复与来源快照](../voSharp/docs/SIP_SMS_SIM_BINDING.md)：
回复同时固定原线路／SIM，实际 Kernel、Host IMS、VoWiFi 与 AT 发送均有来源校验；
收件源元数据原子判重入库，旧历史不猜当前卡。隔离 Asterisk 暴露的长别名截断促使
回复编码缩短至最多 78 字符，仍保留完整 256 位绑定，不从 To 回填被截断的目标。
该原卡阶段尚未实现 MT 持久离线队列，Rust 的 MT 也非持久队列；Go outbox 的跨进程占位、本地
同密码账号重建隔离及新版独立凭据部署生成器仍是后续缺口，不能因上述接线便宣告全部对齐。
本阶段软件与隔离互通证据见 `publish/sip-sim-binding-20261024`，没有替换现用程序或操作实卡。

随后已接入 Windows [MT 持久转发](../voSharp/docs/SIP_SMS_FORWARDING.md)：真实收件回调
同步捕获收件人，与 Inbox 同事务保存；SQLite claim 在 MESSAGE 载荷前提交 Unknown，
跨实例防争抢并用 attempt token 防迟到完成。账号／peer 有持久代际，同密码删除重建不
继承旧短信；独立来源收据覆盖删除 Inbox 后的重放，旧空计划不补发。实际桌面到 SQLite／
SIP 回环、peer 无正文 OPTIONS 及容量保护均已有专项证据；首次升级同事务登记历史空计划
来源，重开不重复迁移；并发初始化先取得 schema 写锁。原冻结与保留失败见
`publish/sip-mt-outbox-20261024`，修订整体验收见
[r2 记录](../publish/sip-mt-outbox-20261024-r2/README.md)，不等于实卡或完整 C# outbox 到生产 Asterisk 验收。
Go 随后已反向补齐[跨实例 CAS 与接收身份](../VoCat-sip/docs/SIP_SMS_DURABILITY.md)：
实际 SQLite 日志及设置比较交换、随机尝试 token、本地随机 epoch、同密码重置和旧 Manager
即时持久授权核验。数据库每次物理重连都重设并验证安全参数，不能确认提交就不发送。
本阶段只是软件验证，未替换生产服务或操作实卡；最终证据见
[阶段记录](../publish/vocat-sms-authority-20261024/README.md)。随后已补上 Go 的
[真实 Inbox／原计划同事务与来源收据](../VoCat-sip/docs/SIP_SMS_RECEIVE.md)：实际 IMS 回调
直接保存原接收计划，后续轮询不再用当前配置创建收件人；关闭／无授权／容量的空计划、
历史升级和删除后同来源重送均有有界保护。发送 claim 和应用初始／认证事务再核对实际 Inbox，
新增最多 78 字节的原 SIM 紧凑回复地址。AT 存量先归档，后续完整身份范围的新物理记录
才可转发；首扫描、未知来源及保护证据不足时保守留在本地收件箱。
该接收阶段尚缺的 peer readiness 已在后续阶段补齐；新版部署配置生成、实卡及双端联合
验收仍待完成，不能把两端宣称为全部对齐。
本轮 Go 隔离软件回归已通过，详见[接收阶段记录](../publish/vocat-sms-capture-20261024/README.md)；
该阶段当时尚未覆盖底层 UDP 重传和国内回复目标冻结，后续补强见下段。
本轮对照先发现 Windows 实际 Inbox 检查止于 claim；随后已在独立阶段补齐
[Windows 每次实际 UDP 来源核验](../publish/windows-sms-wire-guard-20261024/README.md)：
目的端等待后、每片、认证重试及超时重传都重查实际 Inbox／claim，错误或取消不复活 Unknown。
真实 SQLite＋UDP 11 项三轮与完整核心／桌面回归通过。随后 Go 的
[独立逐包阶段](../publish/vocat-sms-wire-20261024/README.md)已实现 MT 专用有界 UDP、
同一只读快照复核 Settings／Ledger／实际 Inbox 和原 Unknown claim；新私有日志 v3
在首次准入冻结 canonical 回复目标。新全量 1,701／43 原跳过／0 失败，完整 SIP／Store
race 317 项、91 项 SIP 及 6 项新增 Store 测试各三轮和双架构构建通过。证据是本阶段
一次全新 9 阶段运行，不沿用 Windows 结果代替；未变 UI／Windows 证据另明确复用。
该阶段 Windows 尚无 canonical 国际号码快照；后来源码与真实 UDP 复查确认，固定原线路
只绕过选线规则，仍会按当前线路国家码补号，旧“不会重补”的判断不准确。现已独立补齐
[首次持久准入回复快照](../publish/windows-sms-canonical-20261024/README.md)，不在排队／重启后重解释。
该 Go 逐包阶段当时尚缺的 peer readiness 和长消息分段见下方后续阶段。两者不是完全对齐，
上述软件结果均不等于实卡或双端联合验收。

随后[Go peer 离线探测](../publish/vocat-sms-readiness-20261024/README.md)已独立接受：固定端点
先做不含短信／SIM 身份或认证凭据的 OPTIONS，未可达保留原 Queued 与期限；探测成功
仍重查持久权限和实际 Inbox，再进行原 Unknown claim。探测、认证与每次正文共用总预算，
调用者取消直接传播；本地接收人优先，固定 peer 单 worker，运行实例内短缓存不跨重启。
全新九阶段：1,713 主测试／43 原跳过／0 失败，完整 SIP／Store race 329 项，140 个关键
用例及 6 个存储专项各三轮、静态检查与双架构构建通过。旧失败、冻结及全部旧测试体保留；
未变 UI／Windows 仅复用既有证据。仍不宣称实网、完整 PBX 或两端所有能力已对齐。

最新[显式 MESSAGE 分段](../publish/vocat-sms-parts-20261024/README.md)实现 Go 与 Windows
相同的原文块及输入范围：每块至多 280 个 UTF-8 字节、Unicode scalar 边界；多个块加
`[i/n] ` 编号，整包含头部和认证仍不超过 1,300 字节。MT 准入最多 16,384 UTF-16 单元，
补充平面字符占两个，不改变 MO／普通分机 MESSAGE 范围。原持久 claim、来源 guard 和
总期限继续跨分段使用，不把不同 Contact 的部分副本拼成成功，也不自动重试 Unknown。
Go 首段尚无 2xx 时保留底层失败分类、部分接受后失败强制 Unknown；Windows claim 后
任何失败均 Unknown。Go 默认五秒、Windows 八秒，不能把共享切分契约说成所有策略相同。
Windows 当前全新七阶段已通过：核心 1,504／桌面 496，原 5／17 跳过，99 项关键各三轮，
Release 0 警告／0 错误；C# 运行时未改，新增 18 项纯契约测试不能冒充新 wire／准入证据。
Go 全新九阶段已接受：全量 1,724／43 原跳过／0 失败，完整 SIP／Store race 340 项，
151 项关键及 6 项存储专项各三轮、静态检查与双架构构建通过，以阶段记录为准。
显式分段不是字素簇保护、UDH、透明重组、任意长度或真实软电话／运营商送达保证。
Go 资源仍为 4 MiB／256 条逻辑 MT／每条 16 个接收人，Windows 最多 2,048 个 intent；
新范围内的较长 Go v3 日志不保证被旧 v3 程序接受。新版配置生成与双端实网验收仍待完成。

前阶段已接上 [Linux 主应用 Host IMS 模式](../VoCat-sip/docs/HOST_IMS_APPLICATION.md)：
逐设备模式配置、独立 Manager、唯一 MM 映射、电话／短信／SIP 服务与简化 UI 已有源码。
Host 状态不再要求 WiFi 隧道；切换必须获得旧 owner 清理 ACK，并检查遗留持久记录，失败不偷换 AT 或卡片。
后台 legacy QMI Sync／射频／数据恢复已与 Host 隔离，冷启动不靠读取别的 AT 端口猜设备。
仍需已注册蜂窝；自动射频准备、真实多模组与运营商联合验收尚未完成，未替换线上服务。
本阶段 C# 无改动，Windows 与 Linux 共享行为契约，不宣称新 Linux 模式已达到实卡同等验收。

前阶段接上 [PreparedAccess 与单线路 Host IMS runtime](../VoCat-sip/docs/HOST_IMS_ACCESS.md)：
同一租约的固定卡身份／AKA、NAS＋WDS 查询、网络和安全租约可交给真正 IMS Provider。
正常换小区会更新 PANI；准备完成不等于 REGISTER 成功。失败／取消仍保留同一 owner，
停止先关 IMS 再清理 access 与自有 PDN，不触发 VoWiFi RFOff 或基带恢复。
当时主应用 DeviceID→MM 映射、逐设备模式／业务分发／UI 尚未接入；最新进度以上一节为准。
Linux 使用纯 Go，Windows 保留已有动态 PANI 刷新，本阶段 C# 不改；原许可与授权 gate 保留。

VoCat 后续已补[Host IMS 自有内核网络租约](../VoCat-sip/docs/HOST_IMS_NETWORK.md)，
在指定 Linux 的独立 network／mount namespace 验证真实双栈路由与 socket 绑定。
生产 WDS／P-CSCF 证明、SIP／RTP 接线、网络跨重启恢复及模式业务仍未完成，
不能将这一组件等同于 Windows 的实卡 Host IMS 能力；本阶段没有修改 C# 实现。
后续已有[USB 自有 WDS／P-CSCF 来源连接器](../VoCat-sip/docs/HOST_IMS_RUNTIME.md)，
已接到网络租约复用核验；真实固件、native 端点、业务与 SIP／RTP 消费者仍未完成。

2026-10-24 的[消费者接线](../VoCat-sip/docs/HOST_IMS_DATAPATH.md) 已补实际 SIP
socket／默认端口探测绑定、protected 回复 ESP 覆盖和退出栅栏。MM 小区缓存不生成
新鲜 PANI；同时反向修复 C# PPP 路由 ADD 的取消／迟 ACK 窗口，未替换运行中的程序。
XFRM namespace／重复私网地址隔离、完整媒体、模式业务与实卡联合验收仍未完成。

后续[同承载安全边界](../VoCat-sip/docs/HOST_IMS_OWNED_SECURITY.md) 禁止蜂窝路径落回全局
XFRM installer，补 required ESP socket 与失败资源移交契约、无密钥共同安全计划及客户端
UDP 入站漏项。随后已实现私有 `kernelSecurityLease`：独立 namespace／mark／reqid、
原始 XFRM／TC socket、持久 ID 预留与无密钥 WAL，允许已确认对象的定向清理。
仅支持原网络租约已重新建立后的 security cleanup-only recovery；未知 ACK 不按对象匹配
猜测接管，不具备完整冷启动 PDN／网络恢复。实际 Host IMS 模式与业务编排仍未接入。
Windows 同步修复 PPP index／LUID 贯通、owner 替换竞态和 WinDivert 实际全局路由校验；
这是错承载拒绝发送，不是相同地址双模组的完整支持。Linux NAS 自有查询源已在同租约下
证明查询窗口，并核对前后 WDS 与 NAS 事件代际；没有连续小区覆盖或硬件采样时间，
不能把旧缓存或查询完成时间当作实时 PANI。真实固件与注册联合验收仍待完成。

协议审阅是双向的：Rust 已有的兼容性和边界处理也要反查 VoSharp，发现遗漏时同时补代码与
回归样例。Windows 成熟的 IMS／VoWiFi 行为则用于 Rust 原生实现；设备、承载和音频后端
保留平台差异，不机械复制 Windows 依赖。配置结构、诊断入口和未接入的接口不算协议能力。

## 官方发行版与本 fork 的范围

官方 SimAdmin 已发布 VoLTE、VoWiFi 和 Full 增强版，发布说明包含原生 IMS、IPsec、
双栈、EAP-AKA 隧道及 IMS 短信能力，不能将它描述为“SimAdmin 没有 VoLTE／VoWiFi”。
但[官方构建流程](https://github.com/3899/SimAdmin/blob/main/.github/workflows/build-ota-package.yml)
明确将增强版放在私有 `SimAdmin-Code` 仓库的 `volte`、`vowifi`、`full` 分支；公开仓库
仅构建标准版。本 fork 来自公开 `main`，未包含这些增强分支核心源码。

下表的 Linux 缺口只描述本 fork 当前可构建的实现，不评判官方增强版是否具备相应能力。
这里新增的原生 Rust 实现不是已经复用了官方私有协议栈；若增强版源码可获得，应先评估
复用与替换，再继续补齐。官方发行版声明见[发布记录](https://github.com/3899/SimAdmin/releases)，
其双向语音和 Asterisk 网关能力仍须另行核实，不能仅从版本名称推出。

## 早期 Rust 路线差距记录（历史参考）

以下表格及后续 Rust 迁移记录保留用于追溯，包含较早实现和后来补充，不能当作当前 Go
主线的完成矩阵。Linux 主线已选择 VoCat（Go），Rust 保留参考；当前 Go 差距和接受边界以
[VoCat 对齐清单](../VoCat-sip/docs/VOWIN_PARITY.md#仍需补齐或验收的能力)及本文开头的阶段证据为准。

| 能力 | Windows 当前实现 | Linux 当前证据与缺口 | 对齐完成所需验证 |
| --- | --- | --- | --- |
| 独立模组实例 | ModemPool、按线路操作与媒体归属 | 收发短信已按线路隔离；Host IMS 实验注册任务、后端和界面支持独立续期／停止；其他通话和控制仍需移除第一台选择与全局锁 | 短信私有 D-Bus 与注册任务合成双线路已有证据；仍须真实双模组、热插拔和多模式并发验收 |
| Host IMS | Windows PPP、用户态 ESP、AKA、SIP 与 RTP；实时状态新增寿命与关闭检查 | Rust 已有承载、Netlink、原生 AKA、ESP、注册续期的逐模组任务和显式实验入口；分组件已隔离验证，真实联合链路未验收 | 实际选卡／固件与网络管理器协调、多模式持久化，再补运营商电话／短信、媒体和实机验收；不复用 Windows 驱动 |
| 模组语音桥接 | AT／QMI 控制与 USB Audio 绑定 | ModemManager Voice／AT 控制存在；不能据此证明 SIP 双向媒体 | 针对选中硬件的音频绑定、G.711／AMR 媒体、取消／挂断回收 |
| 独立 VoWiFi | IKEv2、ePDG 多候选／双栈／历史、IMS；实际 ESP 已执行选择器、校验和及有界队列 | 本 fork 原生 IKE／EAP、最终 AUTH、UDP ESP、嵌套 IMS 安全和注册续期已串通；完整生命周期及逐模组管理仍待接入，不包含官方私有增强分支 | 实网 SWu、MTU／分片与逐模组管理；多 PLMN 和成功历史按 SIM 隔离；注册／失败回退／注销 |
| 可选 SIP Server | Registrar、认证、内线、群振、首接取消、号码规则 | 纯 Rust 注册、MESSAGE、双腿内线、同分机群振、首接取消、迟到接听清理和 G.711 RTP／RTCP 已通过原生 UDP 测试；私有设置与启动恢复已接入 | 实际软电话回归、保持／重协商、DTMF、固定媒体端口与外部对接；不能引用 C# 原型结果代替 |
| Asterisk 中继 | 参数化部署、固定中继；新增严格挑战解析和 BYE 鉴权 | 可选固定认证中继已接入 Rust、私有设置及启动恢复，按前缀／分机授权，与注册内线共存；真实 Asterisk 的 MD5／SHA-256、来显、PCMA、取消与挂断通过 | 生产配置部署、中继消息、运营商入呼／外呼与媒体；Windows 与 Linux 的实际联合验收，不固定账号／IP |
| 收发短信 | PDU、长短信、送达状态、SIP MESSAGE；SQLite 收费准入、原卡回复／来源快照和 MT 持久队列／收件人代际 | 网页、Hub、任务及原生 Rust SIP 使用按线路提交器；SIP 的防重记录持久化并随新版短信备份恢复 | 原生 SIP 到模拟 D-Bus、原卡回复与重建防重已有证据；Go 已补跨实例 CAS、接收代际、接收原子化、来源收据、紧凑回复、逐包 guard、peer readiness 及有界显式 MESSAGE 分段；真实长消息互通、实卡及双端联合验收仍待完成 |
| 短信和验证码 API | 独立 Bearer Token、只读筛选接口 | 同路径和 JSON 字段的独立 HTTP 服务与 Web 设置已实现；Token 摘要保存、默认关闭；真实部署访问路径待验收 | 相同路径、字段、时间、关键词、线路、鉴权、默认关闭、限流与不消费语义 |
| 通知和远程交互 | Telegram／微信／QQ 交互，Bark 仅提醒，分级权限 | 多渠道模板、规则和队列较完整；不等于已具备交互命令 | 借鉴模板／队列；保留单向与交互边界、独立权限及会话选线，不自动重放收费命令 |
| SIM 与 eSIM | SIM／eUICC、QMI／AT／PCSC 路径 | ModemManager、lpac、QMI／MBIM APDU | 显式硬件归属、取消与超时、Profile 操作结果、密钥和激活码不进日志 |
| 状态和日志 | 分层状态灯、详情、脱敏导出 | Web 管理、通知／任务日志 | 对应每条线路的控制／网络／IMS／媒体／SIP 状态；导出不含 Token、SIM 密钥或原始敏感数据 |
| 发布和升级 | 依赖系统 .NET 的轻量 Windows EXE | Rust、React 与 Linux OTA／systemd | Linux x86_64 先原生构建，ARM 目标另行验证；不破坏 Windows 包；配置迁移与回滚 |

原 Rust 迁移阶段曾选择纯 Rust 协议；这是历史决策，不再是当前 Linux 主线要求。
DDNS、备份和任务调度仍是可借鉴的工程能力。
两端共享操作契约、协议样例和边界语义；承载、设备发现和音频后端保留平台实现。
不强制相同 UI，也不能以诊断页或空适配器宣告 IMS／VoWiFi／媒体可用。

## 已完成的共享基础

2026-10-21，Windows 的验证码识别改用与 SimAdmin 短信核心一致的候选评分、负面上下文、
全角数字和分隔数字规则，避免把订单号、金额或前面的编号当验证码。两库各保存相同的
`contracts/otp-extraction-v1.json`，供 C# 与 Rust 测试直接读取；裸数字不再自动当验证码。

两端的长短信计算也使用相同的 `contracts/sms-segmentation-v1.json`。Linux 修正 GSM 扩展字符
占两 septet、默认字母表、UTF-16 单元及边界浪费的估算；Windows 修正实际 TPDU 重复 UDHL
和补充平面字符超长问题。Windows 测试检查每段数据不超过 140 字节并独立解包还原原文，
不是只核对预估段数。限制依据见 [3GPP TS 23.038](https://www.etsi.org/deliver/etsi_ts/123000_123099/123038/19.00.00_60/ts_123038v190000p.pdf)。

新的[Go／C# SIP MESSAGE 分段样例](../contracts/sip-message-parts-v1.json)属于另一层：
280 字节原文加可见编号，不使用上述运营商 TPDU／UDH 编码，两份契约不能混为一种分段。

上述修改仍为源码开发状态，不自动替换正在使用的 VoWin，不启动真实模组、电话、短信或生产 Asterisk。
它们不代表上表其余 Linux 网关能力已经实现。

Linux 收信现在为每台模组建立独立任务，校验真实 ModemManager 信号来源、短信对象归属和读取前后的
硬件／SIM 身份。慢模组不会阻塞另一台的收信；发现失败不再由短信任务重启共享的 ModemManager。
延迟删除还会复核服务所有者、SIM 身份与短信原文；来源不能确认时保留模组副本，不冒险删除。
新增 `slot_id` 迁移不回填旧短信归属，备份导入导出也保留这个字段。

Linux 的可选短信 API 已接到实际 SQLite 数据库与基本配置页，默认独立监听回环地址，支持与 Windows
相同的查询参数、只读语义和 JSON 字段。两端共用 `contracts/sms-api-query-v1.json` 的 32 条参数样例
和 11 条 Unicode 关键词样例；正文按 UTF-16 长度截断且不切开 emoji。Linux 的内部北京时区旧时间会
明确转换为 UTC；Token 仅存摘要，配置权限为 0600，修改要求管理页密码保护及真实管理员会话。

Linux 发送现固定 D-Bus 唯一服务所有者、模组和 SIM 快照，创建后复核短信归属、正文／号码和状态，
再提交一次。移除、服务重启、换卡或同卡忙时不使用其他卡；不同线路独立提交。先保存带线路
的 pending 记录，返回后更新 submitted 或 unknown；应用／连接中断不自动重放。网页和自动任务
提供线路选择，旧任务只在唯一已核实短信模组下兼容运行，原失败重试已停用。

私有 D-Bus 新增 8 项发送测试，包含双模组选线、重复／未知硬件、服务所有者变化、换卡、对象
归属和内容变化、草稿状态、未知结果及连接取消时的所属任务。与现有短信 API／收信等相关
用例合计 38 项通过；没有发送真实短信。短信列表仍汇总各线路，运营商送达报告需继续补齐。

## 双向鉴权兼容与校验

2026-10-21 的 UIM／AKA 审阅已落地到两个原生实现，而非仅同步 C# 对照副本：

- VoSharp 借鉴 Rust 的单层 `80` 响应封装，支持短长度和 `81` 长度形式，并按 Rust 通用
  UICC 路径补上 `6Cxx` 的 Le 修正。只修正有明确短 Le 的命令，不覆盖 SELECT 的数据末字节，
  不猜测扩展 APDU；同步与异步传输行为一致。
- Rust 借鉴 VoSharp 的总长度兼容形式、RES 4–16 字节、CK／IK 各 16 字节及 AUTS 14 字节校验；
  通用 `UsimAuthService` 补上 `9Fxx` 续读，保留已经收到的分片，并在失败时关闭所属逻辑通道。
- 两端拒绝空封装、截断数据、未知／嵌套封装、错误状态字和多余尾部。可选 Kc 必须恰为一项
  8 字节字段；修复 Rust 空封装的越界及 VoSharp 忽略尾部解析失败的问题。解析器只接受最终
  `9000`，`61xx`／`9Fxx` 交给传输层续读；续读与 Le 修正均有次数上限。

两库的 `contracts/usim-aka-response-v1.json` 保存完全相同的 42 条合成响应，检查结果类型与
密钥内容，而不只检查是否抛异常。另测有效响应的逐字节截断、512 种空封装声明、续读分片、
Le 修正及循环上限。标准布局与可选 Kc 见 [TS 31.102 第 7.1.2.1 节](https://www.etsi.org/deliver/etsi_ts/131100_131199/131102/19.04.00_60/ts_131102v190400p.pdf)；
`80` 外封装和总长度形式是有边界的固件兼容，不宣称为标准要求。Rust 的 `UimApduResponse`
和 `UsimAkaApduResult` 调试输出改为长度及状态，不打印响应体或鉴权密钥。

Windows 鉴权相关 116 项、Rust 设备层 42 项离线测试通过，Rust workspace 编译检查通过。
本轮只补强已有 UIM／AKA 基础；Rust 专用代理交换／重试路径仍需统一审阅，不能将通用路径的
保证外推到所有调用。完整 Linux Host IMS／VoWiFi 注册与媒体仍待实现和实机验收。

## 原生 IMS 注册核心

Rust 新增 `crates/simadmin-sip-runtime/src/ims/`，独立于局域网 Registrar，包含稳定事务身份、
二进制 RES 摘要、有界 AUTS／423、安全能力检查、先安装安全关联再发鉴权响应、续期和注销。
续期广告新 SPI／客户端口，保留服务器端口；缓存已计算的鉴权，不因 nextnonce 重读 SIM。
如出现 rspauth，则验证摘要及 qop／cnonce／nc 后再更新 nonce。仅保留可清零的派生 HA1，
取消、超时、拒绝、到期或承载代际变化后清除有效状态与鉴权缓存。

每张卡的 `RegistrationOwner` 防止重叠流，跨流保留 403 暂停及 Retry-After。停止先清本地
状态，再限时注销；不恢复或重启模组，不派生后台清理任务。平台必须提供真实受保护通道、
选中 SIM 的鉴权器，并按 refresh_after 调度续期；这些生产适配和后台任务尚未接入，
`host_ims`、`vo_wifi`、`carrier_calls` 仍关闭。后续原生 ESP 的独立验证见下一节；IKE 仍待实现。

16 项 IMS 测试使用合成 P-CSCF 和真实 IPv4／IPv6 回环 UDP，含实际 UsimAuthService 与模拟
APDU 的贯通、取消缓存清理和双卡独立故障；安全回调测试不验证 ESP 加密。两端新增相同的
`contracts/ims-aka-digest-v1.json`：5 组摘要向量、20 组挑战解析样例。VoSharp 同步加入
4096 字节／32 字段上限及严格语法检查，finally 清理自己的 RES 副本、HA1 与计算缓冲区，
不修改调用者的 RES；Windows 尚未加入会话级 rspauth 验证。详情见 Linux 的
`docs/IMS_REGISTRATION.md`，本轮未操作真实卡、生产 Asterisk 或已安装桌面程序。
注册核心阶段 Rust 协议合计 45 项通过、2 项 Asterisk 用例在普通运行中显式忽略，随后单独在隔离
容器通过；设备层 42 项、SIP 设置与生命周期 7 项、Windows 扩展回归 304 项通过。
rustfmt、协议库拒绝警告的编译和 workspace check 通过；Clippy 未安装，未执行成功。

## 原生 IMS ESP 与 Linux socket

Rust 已实现 HMAC-SHA1-96／HMAC-MD5-96 搭配 AES-CBC、3DES-CBC、NULL 的传输模式 ESP，
支持地址／端口／SPI 选择器、填充、完整 UDP 长度与校验和、64 包重放窗口和序号耗尽检查。
临时关联先服务 REGISTER，成功后提交；旧事务保留原代际，5 分钟后失效，空闲清理由所属
任务定时执行。没有进程级全局密钥或自动裸 UDP 回退，NULL 算法仅完整性保护、不提供保密性。

`LinuxImsChannel` 绑定明确本地地址、接口、P-CSCF 和承载代际，使用自己的 raw ESP／UDP
socket；要求相应权限，不改变路由、XFRM、防火墙或模组。已在独立网络命名空间跑通
IPv4／IPv6 的真实 socket 注册、故意丢包后的重传、续期、旧代际 INVITE／回复和注销，
并检查来源伪造、激活后的裸 UDP、损坏 ICV、错误接口和端口，以及关闭后的 socket 回收。
Linux raw IPv6 的 IPV6_V6ONLY 返回 EINVAL 问题已修复，UDP 仍保留该限制。

两端共用 32 组 Node／OpenSSL 独立生成的 `contracts/ims-esp-v1.json`。VoSharp 据此补上
外层 IP、完整 UDP 长度和校验和验证，将重放窗口的更新移到所有检查通过后，损坏的已鉴权
高序号报文不再挤掉合法包。生成器使用 OpenSSL 不代表 Rust 运行依赖它；密码算法采用
RustCrypto，Windows 保留原 C# 实现。具体范围和复现命令见 Linux 的 `docs/IMS_ESP.md`。

此通道未接入后台 IMS PDN／P-CSCF、真实选卡鉴权和逐卡任务，也没有实现 ePDG IKE／
CHILD_SA 隧道或运营商媒体，能力标志仍关闭。隔离测试不是蜂窝网关或实网运营商验收。

## Linux IMS 承载租约和鉴权前置

Rust 的 `ImsBearerPool` 显式绑定 D-Bus 服务所有者、硬件与 SIM，按模组独立占用；
只创建 IMS APN，默认要求 multiplex，不断开已有上网 PDN。P-CSCF 来自所选模组，
要求 APN 与实际本地地址匹配，不使用 DNS 或网关代替。仅回收本次确认创建的路径。
调用取消或超时后保留原请求回复观察，未完成或结果丢失时禁止重试和重叠清理；
已知路径需显式核验恢复，旧代际不能影响新会话，析构不等待模组。

QMI proxy 核对响应方向、服务、CID、事务、方法和标记，限制帧数和绝对事务时间；
发出 AUTHENTICATE 后不因传输、解析或通道清理失败自动重做 AKA。两端共同限制
61／9F／6C 交换为 8 次和 4096 字节正文，6C 带正文即终止。
共享 `contracts/host-ims-pdn-v1.json` 包含 20 组地址、11 组参数样例；Windows 补上
十进制字节、前缀及严格 CSV 解析，避免 `/64` 被当成 IPv4 或前导零触发八进制解析。

设备层 56 项通过，包括 8 项实际私有 D-Bus 和 4 项私有 Unix proxy 测试；Windows
相关回归 439 项通过。后续内核网络接入见下一节；硬件绑定 AKA、注册及逐卡后台任务
仍未贯通，实际 `Modem.Command` 的可用性也须按插件验证。Host IMS、VoWiFi 和运营商通话
能力标志继续关闭；不是实际 SIM、固件接管或通话验收。Linux 详情见 `docs/IMS_BEARER.md`。

## Linux IMS 网络和 ESP 生命周期

`prepare_network()` 已把已拥有承载接到原生 Netlink，核对接口索引、名称、MAC、sysfs
对象及选中硬件祖先链，再添加地址和逐租约策略表。完整源地址与绑定出口接口共同选线，
同样的本地 IP／P-CSCF 可在两条线路并存；不改主默认路由或 DNS。媒体主机路由可单独
添加和回收，最多八个。只删除确认创建且指纹未变的对象；借用地址、外部替换和依赖
本地地址的外部路由受到保护，未知 ACK 不能变成重试或清理授权。

网络请求由各自任务串行执行，取消初始化仍回收已确认改动；清理未完成时阻止重叠网络
租约及模组承载释放。`open_on_network()` 验证 P-CSCF 和网络后打开原 raw ESP 通道，
网络取消、失效或状态通道断开即清 socket，代际置为无效，旧注册不能继续显示有效。
Windows 同步补强 QMI IMS 和正式路由核验中的无效／自身 P-CSCF 拒绝。

真实 mount／network namespace 测试覆盖双栈重叠地址、取消、媒体容量与回收、借用地址、
外部替换及同名接口重建；raw socket 两地址族另验证生命周期联动。虚拟接口不证明实际
USB／QMAP 硬件链和运营商接管，后台真实 AKA、逐卡任务、自动 RA／网络管理器协调、
IKE／ePDG 与运营商媒体仍未完成。Linux 说明见 `docs/IMS_NETWORK.md`，能力标志仍关闭。

## Linux 所选 SIM 原生 AKA

Rust 新增 `ImsBearerPool::qmi_aka()`：只使用明确的 QMI 端点，绑定字符设备、sysfs 对象和
所选硬件祖先；同一连接核对 DMS IMEI、直接 EF.ICCID／EF.IMSI，操作前后及 AUTH 前复核
服务和选卡。应用清单提供完整 AID，IMS 优先使用 ISIM；只有明确不存在 ISIM，才按
当前 IMSI 和 EF.AD 的 MNC 长度回退 USIM。ISIM 不可读或身份改变时不静默降级。

按硬件共享占用，异卡并行；取消关闭自己的 socket，阻塞线程真正结束后才释放占用和
发布终态，不自动重放已发出的 AUTH。最近挑战和结果不确定的指纹分别有界保留，防重
状态尚不跨进程持久化。旧 eSIM／APDU 路径尚未统一使用此占用器，不能外推为全局串行。
`LinuxQmiAkaProvider` 校验预期 IMS 身份与 IMEI，Host IMS 入口另绑定同线路的网络生命周期。

两端新增 12 条 ICCID BCD 样例。Windows 修正中间 F 填充被忽略的问题，失败 AKA 解析先
完成校验再分配密钥；Rust 的 QMI／APDU／结果自有缓冲区释放时零化。设备层 70 项、SIP／
IMS 50 项、Windows 493 项通过；9 项选卡鉴权测试连续五轮通过。真实内核网络、双栈 raw
ESP 和两项隔离 Asterisk 测试另行通过，正式容器及配置未改。曾发现结果发布早于工作占用
释放的竞态，已调整顺序，初次失败与最终日志均保留。

这些仍是私有 D-Bus、Unix socket 和合成卡的隔离验证，不是真实 SIM／USB 祖先链验收。
后台逐卡注册、统一 APDU 生命周期、原生 QMI PCO、IKE／ePDG 及运营商媒体尚未贯通，
能力继续关闭。API 和复现见 Linux `docs/IMS_AKA.md`；没有新包、部署或桌面替换。

## Linux 逐模组注册任务与管理入口

`HostImsPool` 已连接所属承载、网络、原生卡鉴权、ESP 注册与自动续期，后台提供显式
选卡、启动、状态与按代际停止，前端新增独立状态灯、确认弹窗及单线停止。默认不启动、
不恢复设置；这是仅注册的实验入口，不是已完成的电话网关或真实运营商验收。

任务按硬件隔离，卡拒绝策略按 ICCID 保留；已知拒绝在创建 PDN 前检查，同卡换 IMSI、
重建任务或换硬件不能绕过。旧代际不能停止替换任务；退出是不可撤回的快速取消信号，
不等待模组恢复。已取消的鉴权工作和本地网络清理未确认完成时不释放承载，未知结果
阻止替换。已知换卡／承载失效立即中止通道，不再发送旧身份注销；过期或关闭通道不显示在线。

Windows 同步新增实时注册查询，以 UTC 和单调计时限制寿命；Host IMS 不再把过期或
已关闭传输的历史成功结果当成在线。Linux 完整回归发现并修正两项旧 DDNS 优先级失败：
LAN-only 桥不能优先于对应地址族的蜂窝默认出口，仪表盘和 DDNS 改用同一规则。

本轮 Rust 设备层 74 项、SIP／IMS 58 项、backend 192 项及 Windows 512 项通过；其中
8 项线路任务测试使用合成卡和真实双栈回环 SIP。真实内核网络、raw ESP 和 2 项隔离
Asterisk 测试另行通过。前端 lint、类型检查及构建通过，离线浏览器验证启动确认、独立
停止和窄窗口布局；这些不是原生硬件整链路验收。正式 Asterisk 配置及桌面程序未替换。

真实硬件、固件接管、多模式持久化／恢复、全局控制迁移、统一 APDU、IKE／ePDG、
运营商业务与媒体仍未完成；电话能力标志仍关闭。详情见 Linux `docs/IMS_HOST_LIFECYCLE.md`。

## 已弃用的 C# Linux 对照原型

以下为确定纯 Rust 路线之前的对照记录。该原型不再作为 Linux 发布方案，相关测试不证明
当前 Rust 内线／媒体可用。默认应用构建不启用 `dotnet-reference` 比较特性，也不启动 .NET。

Rust 管理端已接入可选的共享协议子进程，默认关闭，无预设账号。配置保存在权限 0600 的私有
文件中，状态不回显密码；修改要求密码保护、管理员会话和同源请求。开关恢复、组件检测、
停止／异常时回收所属进程已实现，未安装组件不妨碍原有 Rust 功能。可选组件依赖系统 .NET 10，
不分发私有运行时或 Windows 驱动；独立源码随 Linux 仓库保存，可单独构建。

Linux 私有环境已执行 87 项共享协议测试，覆盖摘要认证与重放、注册、内线群振、取消、
迟到 200、BYE、MESSAGE、G.711 和 RTP 来源限制。Rust 另通过真实子进程的 UDP 注册、
启动／停止／重启／端口回收、取消请求终止所属进程，以及服务配置开关恢复。固定中继
协议用例使用合成对端，不能当作实际 Asterisk、手机或运营商媒体验收。

共享媒体修正已同步 Windows：G.711 不加载 AMR；Linux 分别解析系统 NB／WB 动态库，WB
缺失不禁用 NB，非 Windows 不提取 Windows DLL。Linux 宿主仍明确报告运营商通话／短信、
Host IMS 和 VoWiFi 不可用；源码中含有 IMS 协议类不等于平台承载已经接入。

## 当前纯 Rust 协议迁移

原生模块位于 `crates/simadmin-sip-runtime/src/native/`，已由 Linux 管理端直接调用。
已覆盖严格 UDP 报文边界、摘要认证与重放限制、分机注册和 MESSAGE、发送权限、
原线路回复、收信转发及监听回收。短信直接进入 Rust 提交器，无子进程或 HTTP 回调。
Windows 和 Rust 新增 `contracts/sip-sms-operation-v1.json`，校验零值及最大合法数字
CSeq 的稳定收费操作身份，避免重启或网段变化后把原事务误作新发送。

Rust 已新增双腿通话 actor 与 G.711 RTP／RTCP 中继，支持同分机多端群振、首接取消、迟到 200
的 ACK／BYE、早期对话 BYE、双端挂断、UDP 重传、振铃与 ACK 超时、关闭时通知及端口回收。
通话先释放本地状态与媒体，不等待远端清理才恢复空闲。两端共用 8 组 G.711 PCM 样例；Rust
另检查全部 65536 个输入的量化误差，并验证两通并行时的挂断与音频隔离。固定中继阶段原生协议 29 项、
管理端 7 项通过；Windows SIP／G.711 相关 97 项通过、1 项真实 LAN 测试显式跳过，不代表手机听感验收。

真实 Asterisk 22.5.2 镜像已在无外部网络的临时容器中验证摘要注册、分别主叫的双向 PCMA 回声、
双方挂断与振铃取消。修复 Rust 错将不带用户的 `sip:主机:端口` Contact 拒绝、接通后立即误挂断的
问题，并保留 ACK／BYE 的远端目标。VoSharp 已支持该形式，新增相应用例后固定中继 24 项通过。
容器不挂载生产配置或设备，不修改运行中的 Asterisk；脚本位于 Linux 的 `scripts/test-asterisk-docker.sh`。
固定认证中继又已通过真实 Asterisk 的无 REGISTER、MD5／SHA-256 双向鉴权、原始来显、双向
PCMA 和取消／挂断。两端共享 21 条挑战样例；VoSharp 借鉴 Rust 的严格解析、BYE 一次鉴权和
先清本地状态再做远端清理，阻塞挂断回调仍能回收本地会话。Linux 中继与本地注册分机共存，
默认不启用通话方向或呼出账号，不中继 MESSAGE，未知内线不会转回 PBX；尚无真实 SIM 或跨网段媒体验收。
不能当成完整蜂窝网关验收。详细操作与边界见 Linux 的 `docs/SIP_CALLS.md`。

当前限普通单路 RTP/AVP G.711，媒体来源固定到 SIP 来源 IP 和 SDP 端口，使用临时相邻端口。
保持／重协商、ICE、SRTP、DTMF、固定媒体端口范围及中继消息仍待补齐。实际接入限制见
Linux 仓库的 `docs/SIP_CALLS.md`，不能将内部通话能力外推为运营商电话网关已经完成。
接下来仍须完成 Linux 运营商通话与媒体、Host IMS、VoWiFi 与生产 Asterisk 的联合接入，
并继续消除其他模组控制中的第一台选择和全局锁；不能将总目标缩小为注册或短信服务。

## 多来源参考与短信业务基础

整合范围现加入 VoCat、MDD Sim Gateway、pagecat、SWu-IKEv2 与独立的 strongSwan 对照。
固定版本、许可限制、共同上游关系和实际采用内容见 [参考实现记录](REFERENCE_ALIGNMENT.md)。
两个客户端仍为 SimAdmin Linux 和 VoWin Windows；Linux 保持纯 Rust，Asterisk 可选。

原生 IMS 注册流现提供单次准备／提交的二进制短信事务，区分 SIP 接收、RP 接受、RP 拒绝和
不确定结果；取消后不能再次提交同一对象，续期前准备的请求不能使用旧路由发出。
接收报告必须匹配当前注册归属、In-Reply-To 和 8 位 RP reference；不按 reference 单独匹配。
真实 ESP 双栈隔离测试已加入 MESSAGE、同事务重传与受保护报告回复。平台后台、TPDU、
持久化业务、收件入库及实际运营商仍待接入，Host IMS 实验界面仍不宣告短信已开放。

两端加入相同的 RPDU 与 SWu 配置样例。Windows 实际 IKE 配置解析拒绝部分成功的畸形负载；
Linux 已有相同的原生解析并接入最终协商。IPv6 子网与主机地址明确区分，
前缀分配与稳定 IID 仍需实现，不能据此声称 Linux VoWiFi 可用。

## 原生 SWu 初始交换

Linux 已接上显式端点的 IKE_SA_INIT、三个 DH 组、COOKIE、同包重传、NAT-D 与 key schedule，
但返回的是未认证上下文，没有 EAP、最终 AUTH 或 CHILD_SA，不能承载 IMS。当前只在
回环 UDP 中验证，不访问 SIM 或更改系统网络；Linux VoWiFi 能力仍关闭。

Windows 同步拒绝部分消费的负载链、冲突的 SA last 标记、重复／截断的变长属性、
未知属性被丢弃后的假选中套件、重复初始负载和错误长度 Nonce。SK／SKF 的外层终止语义
单独处理，避免误把内部负载类型当作外层链。临时 DH 与派生缓冲区也补上清理。

两端共用 15 条 IKE 消息、24 条 SA 结构、4 组独立 HMAC 派生样例，Rust 另检查 2 组
独立 MODP 数值。Node 验证器只检查公开合成样例，不作为运行依赖或正式密码学审计。
入口、约束和未完成项见 Linux 的 `docs/SWU_IKE_INIT.md`。

## 受保护的 IKE_AUTH 与分片

Linux 原生 AuthTransport 消费 INIT 上下文，按序收发受保护请求；取消、超时或错误会释放
所属 SA 和端口。双方协商后才使用 SKF，每片有独立 IV 和完整报文 ICV；重传整批字节。
重组检查 SPI 对、方向、交换和编号，认证后才缓存，限制 64 片、明文字节及绝对期限。
合法更大片数可重新开始，旧小片数、重复和损坏报文不污染缓存。

Windows 修复只声明分片却只会解密 SK 的不一致，接入真实 IKE_AUTH 各轮和会话控制器。
直接接收和后台收包均支持重组；对端主动请求完成后的第一片重传可触发缓存响应。
两端共用 36 个独立 AES-CBC／HMAC SK／SKF 字节样例，另有双栈真实 UDP、丢包、乱序、
取消及非法填充测试。Linux 最终 AUTH／CHILD_SA 与 UDP ESP 已接入，逐模组管理及完整业务仍未完成，不开放 VoWiFi 能力。

## 原生 EAP 全鉴权

Linux AuthTransport 已驱动 EAP-AKA／AKA′ 完整鉴权，成功返回明确的 EapCompleted，
而不是可承载 IMS 的 CHILD_SA。结构校验前移到调用 SIM 之前，同一最近请求原样重发只
回放响应；取消中的鉴权不可再次使用该客户端。AKA′ 支持 KDF 1 的显式协商和 AUTS
列表保留，检查分离位和可选网络名策略；不在同一上下文悄悄换方法。

Windows 同步补上重复／未知属性、身份 checkcode、嵌套安全属性、成功编号和受保护
结果指示的校验，并在失败／释放时清理自身的密钥副本。两端共享四条完整报文流程、
六组派生和一个 FIPS 数值。Linux 真正从 INIT 走到受保护 EAP 成功，Windows 另跑完整
IKE 握手和错误最终 AUTH 拒绝；旧的空收包取消测试已替换。细节见 Linux docs/SWU_EAP.md。
最终 IKE AUTH、首个 CHILD_SA 与 UDP ESP 隧道已接入；逐模组鉴权适配和运营商业务仍待完成。

## 最终认证与首个 CHILD_SA

Linux child::establish 已串联 INIT、EAP 和最终双向 AUTH；验证原始 transcript、MSK 派生的
MIC 及一致的 IDr 后，才交付 CHILD 密钥与策略。ESP 选中提案须与原编号及套件匹配，
拒绝保留 SPI、未知／重复 transform 和未协商的传输模式或 IPComp。TSi／TSr 按原提案的
联合覆盖检查，不扩大地址、协议或端口范围。配置须整体有效，选择同族且处于授权范围
的本地地址与 P-CSCF；外层 IPv4／IPv6 不决定内层地址族。

Windows 同步将策略从实际握手传入 EspTunnel，收发检查方向、地址、端口和已分配地址；
接收策略失败不提交重放窗口。实际 ESP 也补齐 MD5 完整性，以及算法名称和密钥长度
一致性检查。地址族回退只针对已认证但无同族配置的情况，不吞掉认证或选择器错误。

双端共享 4 组 AUTH、36 组 KEYMAT、22 条 TS 帧、11 项联合覆盖和 20 个 ESP 提案；
独立 Node 验证器不导入项目实现。Rust 真实回环覆盖 COOKIE、两种 EAP、八种内外层
组合、乱序分片、16 类坏响应／超时及端口释放。Windows 另覆盖完整握手及真实 ESP
包的策略和重放提交。Linux 已进一步接入 UDP ESP 与 IMS 注册，完整 DPD／重协商
及逐模组 VoWiFi 管理仍未完成，Host IMS raw ESP 不代替 SWu 隧道。详见 Linux docs/SWU_CHILD_SA.md。

## 原生 SWu UDP ESP 与 IMS 适配

Linux UdpTunnel 消费最终认证后的上下文，由一个任务独占 NAT-T socket，分别处理
IKE、ESP 和 keepalive。ESP 校验协商策略、UDP 校验和及 IP 保留位后才提交重放窗口。
队列均有界，超时或取消关闭本隧道，不访问模组。支持对端 DPD、IKE／ESP DELETE 和
已认证请求的缓存回复；CREATE_CHILD_SA 暂回明确失败，不宣称已实现重协商。

SwuImsChannel 已连接实际隧道与原生 IMS 核心，核对事务身份并重传相同 SIP 字节。
八种地址族／EAP 组合的真实回环已从 COOKIE／DH 到最终认证，再到隧道内注册、续期
和注销。另测两个重叠内层地址／SPI 的隧道独立收发和停止。合成身份不替代真实多模组。
嵌套 IMS sec-agree 也已接入独立 SA 代际；未显式启用或选择器不兼容时仍在访问 SIM 前拒绝。
MTU／分片及逐卡后台仍须完成。

Windows 同步加入实际 UDP 校验和、原子／非法 IPv6 分片检查、大包上限和 64 项 ESP
队列。双端共用 18 组独立 ESP 报文及 21 条策略用例。详见 Linux docs/SWU_DATA.md；
尚未开放 Linux VoWiFi，也未修改现用桌面程序和正式 Asterisk。

## SWu 内嵌套 IMS 安全

Linux 已将原生 IMS ESP 接入 SWu 订阅任务，提供显式启用、临时安装和成功后提交。
旧代际来信携带通道归属和回复路由，拒绝跨实例、失效或已回收的路由；不借用当前 SA。
REGISTER 取消关闭注册流，MESSAGE 取消只释放事务，保留通道接收迟到报告，不自动重发。
已建立 IMS IPsec 的续期／注销在缺少新 Security-Server 时沿用原 SA；首次或坏头仍拒绝。
该行为同时对照 Windows 现有实现和 MDD 的重新鉴权案例。

Windows 实际 SA 安装入口补上完整 SPI／端口冲突检查，先验证再替换状态；续期生成器
排除旧对端 SPI。旧测试的保留 SPI 先经独立 HMAC 核验，再迁移为合法值，未放宽实现。
两端新增 32 条独立双层 ESP 契约。Rust 实际双层回环覆盖 12 组算法／地址族，完整 IKE
到 IMS 的两种安全模式各有八组，并验证旧代际回复、拒绝降级、回收和取消。
细节见 Linux docs/SWU_IMS_SECURITY.md；真实 SIM、管理接入和运营商业务仍未完成。

## 构建与历史验证

Rust 在用户指定的 `root@10.0.0.2` 构建，工作目录为
`/root/vowin-builds/simadmin-parity-20261021.Ev4g61`。工具链为 Rust 1.99.1 与 GCC 15.2，
首次构建通过仅回环绑定的临时 SSH 下载转发取依赖；未改变系统 DNS 或路由。
远端 `simadmin-sms-core` 的 19 项测试已通过，含两份共享样例；backend 已成功编译，
收信使用私有模拟 D-Bus、API 使用原生回环 HTTP 与临时数据库验证，均不触碰系统总线或真实卡。
前端 ESLint、TypeScript 与 Vite 生产构建已通过。设置页截图来自隔离的合成数据预览，不是模组实机验收。

以下 Release 记录为纯 Rust 路线确定前的历史开发包，不是当前原生协议发行包。
此前短信基础版 x64 Release 与 `--help` 冒烟检查已通过，主程序为 10,116,560 字节；
该历史包不含后续新增 SIP 功能。当前 GNU libc 构建引用 GLIBC 2.39，不能视为旧 Debian
或 ARM 通用包；新增功能的构建证据与目标发行版、硬件运行须分别验证。

2026-10-21 的 SIP／多线路短信开发版已重新完成 x64 Release 与 `--help` 检查，主程序
10,231,472 字节，SHA256 为 `6ddf5c177b3ea5c190560a97441f23f97332887dff7b167abcc71a413c70796a`。
Linux 短信相关 38 项、共享短信核心 19 项及整个 Rust workspace check 通过；共享协议的
Linux 87 项、可选组件原生生命周期 3 项，以及 Windows 核心 943 项／桌面 357 项通过。
Windows 另有 22 项实机用例跳过。前端全量 ESLint、TypeScript、Vite 生产构建通过。
组件可从 Linux 仓库独立发布为 linux-x64，包中没有私有 .NET；未安装、启动或替换生产服务。

后续需将按模组的执行上下文扩展至发送、控制、通话和网络承载，将已经运行的 SIP 核心连接
真正的 Linux 运营商媒体、IMS／VoWiFi 后端与 Asterisk。不能因先完成短信基础或内线服务
就缩小上述目标，也不能把待实现功能的配置页当成验收结果。
