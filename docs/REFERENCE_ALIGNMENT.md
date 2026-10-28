# 参考实现与双端整合

2026-10-26 用户已确认 Linux 主线为现有 `SimAdmin-sip`，管理与协议均保持纯 Rust；
Windows 保留 VoWin／VoSharp（C#），VoCat 与 MDD 仅作技术参考，不作为运行依赖。
10 月 23 日的 Go 主线选择已经撤销。下文保留各阶段的来源及实现记录，不能把其他项目
的历史验证直接记为 Rust 的通过项；完整目标见 [Rust Linux 主线](RUST_LINUX_MAINLINE.md)。
Asterisk 是可选 PBX／中继，不替代客户端的逐模组网关能力。
逐模组独立的 Host IMS、VoWiFi、模组语音桥接、SIP 电话及短信仍是完整目标。

## 本地参考与许可范围

历史 Go 阶段的 VoCat-sip 基线为 `06e94ed9c035082421452d9d33905a6942161b41`，
当时补过 G.711 媒体能力宣告与协商检查，保留了原 LICENSE 与授权校验；未直接复制其
受限实现到 GPL 核心。该阶段不再是 Linux 主线。下表中的旧 VoCat 检出记录仍指旧参考
目录，参考价值、许可范围与当前 Rust 的实际实现应分别核对。

参考源码位于开发工作区的 `references/`，已从 Git 跟踪和发布包排除。以下为本次固定的
源码版本，不等于已编译、运行或验收了这些项目；没有执行它们的安装脚本。

| 项目与固定版本 | 已核对的重点 | 本次使用边界 |
| --- | --- | --- |
| [VoCat](https://github.com/MengMengCode/VoCat/tree/07868c6392f0f5ba9481d6a822fef167268bc3f7) | 先核对 LICENSE／NOTICE 与文档入口 | 当前是 Research & Evaluation 自定义许可，包含地域、用途和期限限制；不编译运行、不直接复制其代码进双端发行源码，进一步复用须另核授权 |
| [MDD Sim Gateway](https://github.com/MddIdd/mdd-sim-gateway/tree/761d38ea0f465e3ff6dd197454897841d76751ed) | SWu 配置解析、ePDG 重协商选择器测试、DNS 占位地址判断、第三方清单 | 根项目 GPL-3.0-only；部分 Asterisk 补丁另属 GPL-2.0-only，不能直接混入 GPLv3 协议核心；逐文件核对 |
| [pagecat VoWiFi gateway](https://github.com/pagecat/vowifi_gateway/tree/e3719840b93961f933aab3dac8bd2641936e2bcc) | 内外层地址族独立、IPv6 前缀配置、P-CSCF 恢复、入站／出站 IKE 分片 | 根 LICENSE 为 MIT，但其 SWu 源流还涉及 GPLv3 上游；不因根许可证而将全部文件视为 MIT |
| [SWu-IKEv2](https://github.com/fasferraz/SWu-IKEv2/tree/bf5d258b7298db65e64b43065b153325eba47a9e) | 原始 Python SWu 编解码、配置属性、EAP 与 CHILD_SA 流程 | 保留 GPLv3 许可边界；Windows 不兼容的图片文件未检出，核心源码可读；不作为两端的运行依赖 |
| [strongSwan](https://github.com/strongswan/strongswan/tree/f9cd483cd97182def563e17b86e4fbd3a90c03d2) | 独立核对 IKE 配置属性、TS 列表保留及 SIM/AKA 模块 | 稀疏检出相关源码；所读配置与 TS 解析文件标注 GPL-2.0-or-later，其他文件仍逐项核对；不捆绑守护进程 |

MDD 的 NOTICE 明确说明它派生自 pagecat，pagecat 的 SWu 又参考 SWu-IKEv2。因此三者属于
同一实现谱系，不能当成三份独立正确性证据。strongSwan 和 RFC／3GPP 原文用于交叉检查；
某个参考项目已经通过运营商测试，也不能替代我们的端到端验收。

## 已落实到双端的改进

本次不是直接复制第三方协议源文件，而是结合源码边界审阅与标准重新实现以下行为：

- Windows 的实际 IKE 配置解析改为整体校验：截断值、尾部残片、错误的已知地址长度或
  IPv6 前缀长度使整个配置失败，不再返回畸形消息前半段的地址／P-CSCF。
- Linux 新增原生 Rust SWu 配置解析基础，与 Windows 共用 29 条合成样例。保留位按标准
  忽略，完整的未知属性可跳过；长度与属性总数有界，IPv4／IPv6 和 P-CSCF 类型分开处理。
  16 字节 INTERNAL_IP6_ADDRESS 是已有设备兼容形式，不宣称为 RFC 标准布局。
- IPv6 SUBNET 作为前缀单独保存，不假装成已分配的 UE 地址。参考项目的前缀派生提示了
  这个差距，但还需要实际 CHILD_SA 选择器、每卡稳定 IID 和生命周期验证，不能直接开功能。
- Linux 已注册 IMS 流可显式准备并提交二进制短信 MESSAGE；真实 raw ESP 双栈隔离测试
  覆盖相同事务重传、错误方法回复隔离、RP 报告关联、受保护回复与续期后的旧代际请求。
- 两端共用 33 条 RPDU 解析和 2 条构造样例。Windows 补上 RP-ACK／RP-ERROR 的字段校验、
  尾部拒绝和报告／来信分离，Linux 使用同样的长度与类型语义。SIP 202 不算 RP-ACK，
  RP-ACK 也不等于收件手机已收到；不确定发送不自动重放。

当前 Linux 管理入口仍为实验性注册模式；上述库接口不会自动开启运营商短信、电话或
VoWiFi。未实现或未验收的能力继续保持关闭。

后续还已落地原生 IKE_SA_INIT：复用已核对的多套件／COOKIE／端点隔离语义，并以
strongSwan 和标准交叉核对 SA 与变长属性边界。Windows 同步修正部分解析、未知选中属性、
COOKIE／Nonce 以及临时派生副本；Linux 返回明确未认证的上下文，不是 VoWiFi 已可用。
两端共享 15 条 IKE 消息、24 条 SA 与 4 组派生样例，Rust 另有 2 组独立 MODP 数值。
原生受保护 IKE_AUTH 传输与认证分片已补入，Windows 同步接入实际握手和后台接收路径；
两端精确匹配 36 个 Node／OpenSSL 独立 SK／SKF 样例。对照 MDD 和 strongSwan 的
分片布局后，加入 SPI／方向／事务隔离、ICV 验证后再缓存、片数／字节／期限限制及清理，
不照搬仅按 Message-ID 缓存的实现。重传复用整个分片批次；未经协商不发送 SKF。
Linux 原生 EAP-AKA／AKA′ 全鉴权已接入受保护 AUTH 传输，Windows 同步补上属性、
重传、KDF 协商、checkcode、加密属性和成功确认边界。两端精确匹配四条独立报文流程、
六组派生及一个 FIPS 已知数值；不沿用参考代码中未经验证就缓存或忽略嵌套安全属性的行为。
最终双向 IKE AUTH 与首个 CHILD_SA 协商也已接入：对照 MDD 的选择器保留测试、SWu
KEYMAT 顺序、strongSwan TS 列表和 RFC，保留完整 TSi／TSr 并拒绝越界，而不是替换成
通配路由。Windows 同步在实际 ESP 收发中执行选中策略，失败不提交重放状态。
双端新增 4 组 AUTH、36 组密钥派生、22 条 TS 帧、11 项联合覆盖及 20 个 ESP 提案。
不沿用参考项目省略最终对端 AUTH 的做法；Linux MTU、完整生命周期及多模组业务仍待接入，协商
成功不等于可用的 VoWiFi 线路。细节见 Linux docs/SWU_CHILD_SA.md。

后续已补原生 UDP ESP 与单 socket 分流，使用独立 Node／OpenSSL 向量核对两端实际
报文；借助 RFC 3948／4303 明确 Marker、重放提交与所有权，不把 Host IMS 传输模式
误作 SWu 隧道。Windows 同步补 UDP 校验和、原子分片检查和有界收包队列。
MDD 的 PMTU 探测与内层分片设计也已审阅；当前 Linux 尚无对应实现，仍是实网接入
前需要补齐的差距，而不是按最大 UDP 长度通过就宣告支持低 MTU 公网／VPN。
真实回环已从完整 IKE 握手走到隧道内 IMS 注册、续期和注销，仍不代表运营商验收。

嵌套 IMS 安全也已接入原生 Rust：参考多层 ESP 的分离原则并复用自己的 IMS 代际核心，
不改成运行第三方 Asterisk 补丁。Windows 同步补 SA 安装冲突检查；32 条独立双层报文
用于核对两端实际编解码。审阅 MDD 的 reauth_missing_security_server 案例后，Linux 对齐
Windows 已有的“已建立 SA、且新头确实缺失才沿用”的重新鉴权行为，坏头不回退；
没有复制其 GPL-2.0-only Asterisk 补丁源码进 GPLv3 核心。

## 后续整合与验收顺序

1. 将原生短信事务接到逐模组后台、真实 TPDU 编解码、持久化操作保护、收件入库、
   通知和已有 SIP／Asterisk MESSAGE 路由。不能把裸 RPDU 测试当成完整收发短信。
2. 完成已接入双层 ESP 的 Linux SWu 的 MTU／分片及完整生命周期，补齐原生 ePDG 发现，复用已有逐卡 AKA 与
   网络租约；多 PLMN、上次成功历史和手动覆盖都按卡隔离。DNS NXDOMAIN 只否定某个
   候选，不能永久判定整张卡不支持 VoWiFi。
3. 对照各实现的分片、DPD／退避、DELETE、P-CSCF 恢复、主动／被动重协商和选择器方向，
   保留对最终对端 AUTH 与完整性验证，不跟随参考项目省略安全检查的非目标。
4. 接上运营商 INVITE／MESSAGE、AMR 媒体与可选 Asterisk，验证入呼、外呼、群振首接、
   双向音频、取消及挂断；补齐 USB 语音后端和模式间独立切换。
5. 验证真实双模组并行、热插拔／换卡、启动恢复、快速退出、配置迁移和两端发布包。
   目前的软件回环、隔离命名空间与临时 Asterisk 只能覆盖对应层，不能替代这些验收。

Rust 继续在用户指定的 `root@10.0.0.2` 构建。开发验证不自动替换正在使用的 VoWin，
不修改生产 Asterisk，也不自动拨号或发送真实短信。
