# ePDG 发现与多 IMSI 回退

VoWin 的 VoWiFi 发现配置按 SIM 的 ICCID 保存，同一窗口的其他模组不受影响。
默认保持自动发现；指定 ePDG 只改变接入服务器的选择，不替代 SIM 的 EAP-AKA 运算，
也不把发现 PLMN 强行写成 IMSI 或 IMS 认证域。

运营商 IKE 退避另有[持久门禁](../../publish/windows-ike-retry-20261026/README.md)；
[独立 CLI 与桌面共用](../../publish/windows-ike-owners-20261026/README.md)当前 Windows 用户的
Preferences 记录，重启或切换入口不会清除限制。库嵌入者仍需显式注入自己的持久 owner；
手动 ePDG 地址、不同域名同 IP 及重传也不能绕过已有适用范围的限制。

[桌面状态](../../publish/windows-ike-status-20261026/README.md)现已区分 IKE 计时等待与异常
暂停；详细作用域／原因码可悬停查看。时长是最近核对的缓存值，不是实时倒计时。
「重新检查」不清空记录；界面不直接授权握手，也不在刷新状态时访问卡或数据库。

## 在桌面配置

进入「系统状态」，选择目标模组，在 VoWiFi 卡片中展开「高级 · ePDG 发现与回退」。

卡片上方始终显示当前 ePDG 域名、选中的 IP 和认证归属 PLMN。连接中显示「正在尝试」，
IMS 注册后显示「正在使用」；断开或失败后保留「最近尝试」，不把旧地址显示为在线。

- **优先尝试的 ePDG**：填写运营商提供的域名、IPv4 或 IPv6。多个地址每行一个，
  也可用逗号分隔；不含 `https://`、端口、路径或账号密码。首次优先尝试这些候选，
  DNS 失败或可回退的接入失败后仍可使用自动发现候选。
- **补充发现 PLMN**：填写 `MCC-MNC` 或连写的五／六位数字，多个用空格、逗号或换行
  分隔。例如 `515-66` 仅是格式示例，不是内置运营商规则。它补充标准 ePDG 域名，
  不改变当前 SIM 的认证身份；外国 ePDG 是否接受此卡仍由运营商决定。
- **仅使用手动地址**：必须至少填写一个地址；不查询自动 PLMN、位置或漫游候选，
  也忽略补充 PLMN。适合已知正确 ePDG 的排障，不保证绕过运营商策略。

### 按 PLMN 生成域名

标准格式为 `epdg.epc.mnc{MNC补足三位}.mcc{MCC}.pub.3gppnetwork.org`。例如
`515-66` 生成 `epdg.epc.mnc066.mcc515.pub.3gppnetwork.org`，`204-04` 生成
`epdg.epc.mnc004.mcc204.pub.3gppnetwork.org`；三位 MNC 保持原样。这个命名格式见
[AOSP EpdgSelector](https://android.googlesource.com/platform/packages/services/Iwlan/+/b9ec687fee0110ee303921431a1eca2b1d35d500/src/com/google/android/iwlan/epdg/EpdgSelector.java)。
能算出域名不代表运营商部署了对应 DNS 或允许此卡接入。

补充 PLMN 输入框下方实时预览生成结果。点击「按 PLMN 生成并加入地址」会去重并追加
到优先地址框；补充 PLMN 留空时，使用当前 SIM 的合法归属候选。生成后仍需点击保存，
不会立即重连或改动卡片身份。

点击「保存此卡配置」，下次连接 VoWiFi 时生效；保存不会断开现有通话、Host IMS 或
已连接的 VoWiFi。需要立即应用时，在无通话时断开并重新连接 VoWiFi。
「恢复自动发现」只清除此 SIM 的发现设置，保留蜂窝模式、自动连接和默认外拨偏好。
每张卡最多保存 16 个地址和 16 个补充 PLMN。无 SIM、连接中或通话中不允许保存。

## 成功记忆与失败回退

只有完整完成 IMS 注册，才记录该卡成功使用的 ePDG 域名和认证归属。下次连接先重新
查 DNS，再优先选择成功的域名与合法归属组合；不会缓存并强用上一次的 DNS IP。
自动模式下成功记录先于其他候选；仅手动模式下，只能优先选择仍在手动列表内的地址，
不会把已移除的地址重新加回来。显式 CLI `CustomEpdg` 仍是单地址覆盖。

同卡多个 PLMN、域名与 DNS 地址按候选顺序尝试。DNS 无地址、IKE 初始超时、初始提案
协商失败，以及 AKA 挑战前的身份拒绝可继续回退。隧道建立后若 IMS 注册发生超时或
传输故障，只有实际 REGISTER 的发送／等待路径证明从未收到匹配 SIP 响应，才允许
在旧会话清理成功后排除当次端点并重建。`100 Trying` 也算已响应；401 后的 AKA／读卡
错误、发送前元数据错误、任意普通 TimeoutException／IOException 和混合错误不构成证明。
响应与静默结论原子互斥，响应证据在注册会话内不重置；清理失败暂停自动恢复，不另开资源。
首个已验证隧道后的重建固定其认证归属，每次该身份的连接循环最多 16 轮，不因 IMS 静默
继续另一个 MNC 或 IMSI。明确 SIP 拒绝、运营商暂停和用户取消不按网络超时换候选。

上述边界已从 Linux 反向接入 Windows 的实际启动路径，软件验证见
[Windows 证明边界记录](../../publish/windows-ims-gateway-proof-20261025/README.md)。
两端并非共享异常体系或所有恢复策略；没有把回环 REGISTER 当作运营商或驱动退出验收。

成功记录位于 `%LOCALAPPDATA%\VoWin\epdg-success.json`，最多保留 128 个条目。索引使用
ICCID 与 IMSI 的联合 SHA-256，不存完整卡号、IMSI 或 AKA 密钥；同卡不同身份也不会
互相套用认证归属。恢复自动发现保留成功记忆；损坏或不可写的记录不会阻止注册。

## IPv4 与 IPv6 自动选择

系统 DNS 分别查询 A 与 AAAA，某个域名或地址族无结果不会丢弃其他候选。一个 PLMN
发现失败后，仍可使用同卡另一个合法 PLMN 的地址；它们不一定对应同一个认证归属。

直连时，Windows 按每个 ePDG 地址查询最佳路由和源地址，跳过已确认没有可用路径的
候选，保留可用的 IPv4 / IPv6。查询失败且无法判定时保留候选，由 IKE 实际验证。
此检查使用只读 [GetBestRoute2](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getbestroute2)，
不发送探测包、不改路由；有本机路由不代表远端 UDP 500 / 4500 必然可达。初始 IKE
超时或认证前的明确网络不可达错误可回退到下一端点，认证错误不按网络故障处理。

电脑到 ePDG 的外层网络与 ESP 隧道内的 IMS 地址族独立。例如电脑只走 IPv4，仍可由
运营商在隧道内分配 IPv6；不能因本机没有公网 IPv6 就关闭隧道内 IPv6。注册默认先
请求 IPv4 + IPv6 的 CP / TS，从响应中保留两种地址，选择同族的 UE 地址与 P-CSCF。
运营商仅支持单栈时，在明确的地址族拒绝、最终 AUTH 验证成功但地址配置不可用等
允许条件下回退 IPv6、IPv4。配置载荷依据 [RFC 7296 第 3.15 节](https://www.rfc-editor.org/rfc/rfc7296.html#section-3.15)。
EAP 成功但最终 AUTH 校验失败，不允许通过换地址族绕过验证。

每条 IMS 注册流使用一组可用地址与 P-CSCF，不会为“双栈”同时重复注册两次。状态页
显示外层与隧道内实际选中的地址族，诊断包记录逐地址的选路结果。

2026-10-25 后续已将基本内层请求计划独立补到 Linux，见
[CP／TS 地址族阶段](../../publish/inner-family-20261025/README.md)。对照过程中保留 C# 已有的
标准接收语义：CP 属性保留位忽略，空 SPI 的 Notify Protocol ID 也应忽略；不能为“严格”
而把这些字段变成连接失败。Linux 本轮做了软件验证，C# 声明输入核对未变，未重跑其历史结果；
混合／畸形通知等更细策略的双向比较、不同卡及运营商验收仍未完成。

使用 SOCKS5 UDP 中继时，不按电脑到 ePDG 的直连路由过滤目标；IPv4 中继可封装 IPv6
目标，反向亦然，最终能力由中继与运营商决定。显式本地地址约束中继 UDP socket，
不能被静默忽略，也不会因代理失败改走直连。显式仅 IPv4 / IPv6 策略继续生效。

## 区分身份和网络

SIM 文件解析与 Linux 共用 [27 条 EF_AD／EF_EHPLMN／CRSM 向量](../../contracts/sim-home-plmn-v1.json)。
EF_AD 的 MNC 长度读取第 4 字节，不读取全文件末尾的 RFU 扩展字段；少于 4 字节不能
提供权威长度。EF_EHPLMN 使用 `0x6FD9`，两位 MNC 仅接受指定位置的 `F` 填充。
`91xx` 表示读取已完成但有主动 UICC 命令待处理，可保留本次数据；这里只读，不执行 FETCH。
溢出或非字节状态值忽略，待 GET RESPONSE 的 `9Fxx` 不当成完成数据；原有 `9F00`
带数据兼容保持。依据 [TS 31.102 4.2.18／4.2.84](https://www.etsi.org/deliver/etsi_ts/131100_131199/131102/19.04.00_60/ts_131102v190400p.pdf)
及 [ETSI TS 102 221 10.2.1.1](https://www.etsi.org/deliver/etsi_ts/102200_102299/102221/18.04.00_60/ts_102221v180400p.pdf)。
详见[双端修复记录](../../publish/epdg-sim-plmn-20261024/README.md)，不代表运营商实卡通过。

SIM 的零售品牌、ICCID 发卡信息、当前驻留网络、AT 报告的 IMSI 与 EF.IMSI 读数可能
不是同一个概念。菲律宾／荷兰显示切换不能仅凭国家文字判为 DNS 错误；需结合两种
IMSI 读取来源、ICCID 是否保持一致、EF_AD、实际 EAP 与 IMS 注册结果判断。

核心只保留同一已核验 ICCID 下实际读到或历史学习到的合法 IMSI，不按品牌猜造身份。
Windows 的 EF_AD 优先、未知长度合法前缀及等价归属排序，与 Linux 的归属计划共用
[8 条候选样例](../../contracts/ims-home-mnc-v1.json)，见[阶段记录](../../publish/epdg-mnc-plan-20261024/README.md)。
该候选样例阶段没有修改 C# 协议；后续 REGISTER／重建修复见上方记录，不能因此宣称
两端所有 reader、重试分类或运营商实测都已对齐。
候选顺序为历史注册成功的身份、当前身份，再到其他同卡历史／读取候选；连接遇到
允许回退的 DNS 或 IKE 失败时才尝试下一条。硬件 AKA 前仍核对当前卡片，用户取消不会
转成继续尝试其他身份。运营商明确拒绝后的暂停与重试限制继续生效。

EF_AD 已确认的 MNC 长度优先；未知时保留两位和三位的合法前缀候选。不同 IMSI 的
MNC 长度不能互相套用。手动填写标准 ePDG 域名时，可以在当前 IMSI 的合法前缀内
辅助判断 MNC 长度，但不能借外国域名把整个认证归属改掉。若从未读取到正确的归属
IMSI，手动填写正确 ePDG 也未必能完成认证。

自动发现保留归属／等价归属 PLMN 和显式配置的发现候选；不会仅因驻留网改变就盲用
那个网络作为归属网。Android 的相应选择也区分 IMSI 归属、等价归属和获准的驻留 PLMN，
并提供静态地址输入，参见 [AOSP EpdgSelector](https://android.googlesource.com/platform/packages/services/Iwlan/+/b9ec687fee0110ee303921431a1eca2b1d35d500/src/com/google/android/iwlan/epdg/EpdgSelector.java)。

## 日志和验证边界

2026-10-26 后续：[Tw3 与拒绝证据](../../publish/ike-backoff-evidence-20261026/README.md)
修正了原诊断把 GPRS Timer 3 单位读错的问题，`01AA` 实为 10 分钟。严格策略数据须有
长度前缀，裸值只保留诊断兼容；缺失／畸形／零值／deactivated 分开。真实 IKE 与 C#
后端可返回当前初始 AUTH 已验证的原因和作用域，不沿用上一条 SA。完整 Windows、Linux
原生／静态、关键三轮、完整相关包 race 与八种构建及最终双端复核通过。当前还未接持久
自动重连门禁，退出／重启
不应被未来策略误判为真实断电／移卡；取消期间提交及实卡验收仍待完成。
后续[Linux 实际编排与原 RED](../../publish/ike-retry-gate-20261026/README.md)已从复现接入
Go 持久门禁，完整 Go 回归与最终复核已通过，后续 IP 别名／下层发送等边界仍在补齐。
Go 的[顶层别名阶段](../../publish/ike-epdg-alias-20261026/README.md)已完成全量与最终复核；
[实际逐包阶段](../../publish/ike-transport-peer-20261026/README.md)继续下沉候选／重传／COOKIE
准入和实际应答 IP，扩展专项各三轮、完整 19 阶段、八种构建与最终复核通过。
本端[持久门禁](../../publish/windows-ike-retry-20261026/README.md)后续已进入正式桌面 SQLite、
Manager／工厂及实际 IKE 发送／拒绝回调；4,116 输入专项及跨进程计时／Unknown 与唯一
准入验证通过，完整回归、关键三轮和最终复核通过。原基础阶段 4,107 输入未改是历史边界，不代表当前源码
未改变；完整生命周期／CLI／其他策略和联合验收仍未完成。

2026-10-26 后续：[COOKIE 阶段](../../publish/ike-cookie-20261026/README.md)已补 C# 非零
Responder SPI 的挑战处理、空 SPI Protocol ID 接收兼容、畸形 Notify 拒绝及有界刷新。
真实 IPv4／IPv6 UDP 用例和完整 IKE／核心／桌面回归、关键三轮与构建通过，原跳过保留。
Go 同时补齐实际 provider 的异常准入／混合错误顺序，并通过完整独立 Linux 验证。
这不等于初始证书 AUTH、运营商退避计时、自动重连或实卡已完成；后续[AUTH 缺口复现](../../publish/ike-responder-auth-20261026/README.md)
已由后续阶段接入修复并通过双端完整软件验证。C# 使用选中域名检查 SAN、链及 IKE 用途，
不会拿任意 IDr 代替它；直接 IP 需要匹配 IP SAN，不靠 CN 或猜测归属放行。
当前没有证书忽略开关，私有根 UI、撤销检查及运营商验收仍独立保留；软件与现用部署分开。

2026-10-26 Linux 已参考本端补入 EC20 双来源及同卡历史，并区分线上尝试身份与物理
AKA／短信来源，见[阶段记录](../../publish/profile-identity-20261025/README.md)。该阶段全量、
三轮关键／完整相关包 race 与八种构建通过；这次未改 C# 协议，也未用 Go 结果代替 Windows 的测试。
Linux 新增的畸形挑战／跨轮退避证明边界已在后续[Windows 阶段](../../publish/windows-ike-retry-proof-20261026/README.md)
反向补齐到本端实际 IKE／后端／编排路径，并加入失败 DELETE／本地关闭和先前认证保护。
错误文字不再授权换身份／算法，合法初始提案拒绝不因非零 Responder SPI 被漏掉。
IKE 161／核心 1,744／桌面 673 项、关键三轮与构建通过，原跳过保留；这仍不是实卡验收，
COOKIE、IKE 退避时长与自动重连等策略不能称为全部一致。

连接日志记录尝试序号、候选归属 PLMN、身份来源，以及每个 ePDG 域名的 DNS 状态、
地址数、耗时和解析器，不新增完整 IMSI 到普通日志。Windows 仍使用配置的系统 DNS，
不自动换公共 DNS、不改 hosts，不因 SOCKS5h 自动改为代理端解析。

DNS 解析出地址只完成第一阶段；还需分别通过 IKE、EAP-AKA、隧道地址分配和 IMS 注册。
失败时可打开「记录详细日志」并导出诊断包，重点保留首次失败到回退结束的日志。
诊断包和身份历史属于敏感资料，不要提交到公共源码仓库。

离线测试覆盖混合读取身份、成功历史优先与新 DNS、换卡清理、两／三位 MNC、域名失败回退、
仅手动模式、PLMN 生成、当前地址显示、单栈 / 双栈自动选路、CP 双地址配对、代理跨地址族、
外国 PLMN 不改认证归属、逐卡持久化及深浅主题布局；它们不能代替
菲律宾／荷兰实际切换环境的运营商验收，也不能使不支持 VoWiFi 的卡自动获得该服务。
