# Windows Host IMS / Host VoLTE

## 2026-10-23 ISIM 交叉验证加固

与 Linux 新增的卡内身份事务共享 `isim-identity-v1` 合成契约，修复 ISIM TLV 尾部
非 FF 数据及空白被接受的问题。AT+CCHC 返回失败现在会被识别；清理未确认时不交付
身份／AKA 密钥，已生成结果被擦除，同一读取器拒绝再次开通道。相关先失败用例保留。
这不代表新增了所有实例间的持久 UICC 通道恢复，或已替换现用桌面程序。
Linux 实现及接线边界见[蜂窝 SIM 事务说明](../../VoCat-sip/docs/HOST_IMS_SIM.md)。

## 2026-10-19 原生 I/O 与安全退出

桌面普通退出与手动停止线路分开处理。退出先取消扫描、连接、拨号和自动恢复任务，
各模组并行注销 Windows IMS / SIP、停止媒体并关闭本程序的本机句柄。Host IMS 注销
在仍有效的承载上尝试，发送 STOP 后交由独立 owner 收尾；桌面不等待模组恢复、重启
或重新识卡，也不重新打开 AT。手动停止线路或切换模式仍等待完整恢复后才返回。

接管时模组内置 IMS 为 `2,0`，恢复 `1,1` 由独立 owner 负责，不假定硬件会因窗口关闭
自动复原。owner 继续断开自己的 PPP、清理受控路由，并在必要时复原模组；父进程
退出造成 stdout / stderr 断开不再打断该流程。承载协议升级为版本 2，必须使用匹配的
内置组件；版本 1 在修改模组之前被拒绝。桌面退出不等于此时模组已经恢复。

`packaging/Update-ManagedOwnerBundle.ps1` 可在构建时更新已验证依赖包中的托管 owner。
它校验原有全部清单与文件 SHA-256，只替换 owner 和对应清单项，保留驱动及其他依赖
字节，输出新归档而不覆盖输入。此脚本不是运行时依赖，成品不捆绑 PowerShell。

退出相关定向测试 63 项通过；四组离线回归 1351 通过、22 项实机用例跳过、0 失败。
假 owner 的 STOP 屏障覆盖后台交接、显式停止继续等待、在途停止转为应用退出；
故障 writer 覆盖父端断开后的同步和异步输出。真实双模组带注册退出耗时仍待验收。
`publish/fast-exit-20261019/app/VoWin.exe` 已备份替换原入口，Release DLL 只读自检
验证版本 2 组件，不访问模组、不写路由、不启动承载。原有 32 个依赖文件逐字节不变。

WinDivert 收发不再持有生命周期锁等待原生调用。取消接收改为异步提交，退出同时
停止收发、对本实例的专用句柄调用 `CancelIoEx`，等待已进入的调用全部返回，再关闭
句柄和卸载 DLL。WinDivert 的同步包装内部使用 OVERLAPPED I/O，见
[WinDivert 2.2.2 源码](https://raw.githubusercontent.com/basil00/WinDivert/v2.2.2/dll/windivert.c)。
不会取消其他程序的句柄、停止系统驱动或强制释放仍在使用的缓冲区。

原生回收等待超过三秒会转入可追踪的后台收尾；应用退出仍须等它真正完成，同一承载
也禁止打开新的原始收发通道。主窗口与托盘保留，清理步骤写入
`%LOCALAPPDATA%\VoWin\Logs\shutdown.log`，单步骤超过三十秒显示等待提示。前台连接和
拨号先取消，再等待 IMS 注销、音频与本机原生资源；模组复原在独立 owner 中继续。
服务清理异常不再当作成功退出。

这修正了托盘过早消失和原生收发回收竞态，但不能保证修复驱动本身的内核死锁。
[Microsoft 的取消 I/O 说明](https://learn.microsoft.com/en-us/windows/win32/fileio/canceling-pending-i-o-operations)
明确指出取消请求仍依赖驱动配合。若旧进程已经进入终止状态而内核 I/O 不返回，新版
无法接管旧句柄；保存工作后正常重启 Windows，不反复强杀或强制卸载驱动。离线测试
覆盖卡住取消、并发回收、释放顺序和退出调度，实际双模组带载退出仍需单独验收。

随后实机退出日志定位到另一处已确认的软件故障：通信清理于 10:46:28 完成后，远控
服务被依赖容器通过多个注册别名重复销毁，第二次等待已释放的信号量抛出
`ObjectDisposedException`，使窗口停在退出失败状态。远控服务现复用同一个销毁任务，
重复调用和并发调用不会重新释放资源；通信与原生 I/O 屏障完成后的重复销毁异常只
记录日志，不再阻止正常退出。这一修复不放宽通信清理阶段的失败保护。

## 2026-10-15：承载组件协议与源码启动修复

现场错误 `Usage: VoSharp.HostIms --own COMn RasImsPppProbe.exe lease-seconds` 来自
10 月 8 日的旧 owner，不是 SIM、Asterisk 或运营商拒绝。本机调试版没有资源包时
回退到旧保存路径，但核心已经改成三参数托管 RAS 协议，故在 READY 前立即失败。

现在调试构建会自动嵌入仓库已有的 `packaging/runtime.zip`（首次需先生成），实际
owner 仍由校验后的包解压到 Program Files 保护目录。优先内置组件，不直接启动调试
输出旁的 owner 或放宽 SYSTEM 恢复任务的可信路径。手动设置与环境变量不绕过协议
检查；旧 exe 即使存在，也不能冒充当前的按模组托管承载。

`--describe-protocol` 仅输出 `ProtocolVersion`、`ManagedRas`、`PerModemBinding`，
无需管理员、不访问模组/RAS/任务计划或路由。桌面在改变当前接入前预检，承载启动 API
再次检查；10 秒超时，输出上限 8 KiB，可取消，不兼容时不启动 `--own`。完整包自检
也检验提取出的 exe，报告明确区分一个只读协议子进程与零承载子进程。

发布 `publish/hostims-startup-fix-20261015/app/VoWin.exe`：22.29 MiB，依赖系统
.NET Desktop Runtime，没有加入 PowerShell 或私有 .NET。离线 1184 通过、20 项
显式实机测试跳过、0 失败；其中兼容性及 owner 生命周期定向 66 通过。管理员自检通过。
23:35–23:36 的普通启动恢复 COM6 电信 Host IMS `registered` 与 COM10 DITO
VoWiFi `ImsRegistered`，没有拨号或发短信，程序保持在线。此结果不等于 SIP 监听
已启用或外部手机通话/音频已验收；SIP 保留可选开关及独立的全局/模组外拨门控。

本机安全软件阻止读取旧 WinDivert 下载归档，未关闭防护、添加排除或改安全策略。
打包器新增显式 `-WinDivertDirectory`，仅复用与锁文件三项 SHA-256 完全一致的已有
DLL/驱动/许可证，仍校验驱动签名，保留标准下载方式；不自动接受任意已有组件。

## 2026-10-13 桌面 Host IMS 自动恢复实测

显式启用 Host IMS 且首次注册成功后，每个桌面模组实例现在拥有独立的恢复控制器。
观察使用缓存状态，不周期性发送 AT / QMI 或消费 AKA。原注册续期终止时先让核心层
释放旧 SIP / ESP / PPP、恢复模组内部 IMS；清理完成且原 IMEI / ICCID / IMSI 未改变、
没有通话时，等待本地 5 / 15 / 30 秒退避及该 SIM 的有效运营商 Retry-After，重新移交
承载并建立新的 Windows IMS 注册 / SA。重建前还只读核对实际硬件与卡片身份。

连续失败最多 3 次；持续健康 2 分钟后重置连续计数，但同一次启用意图在滚动 24 小时
内最多自动重建 6 次。计数与意图仅在当前进程 / 本次启用中保留，不宣称跨重启防抖。
身份改变、原生承载丢失或隔离异常、模组恢复未确认、运营商暂停或非瞬时失败都暂停，
不会用重新拨 PPP 掩盖安全故障。手动停止、切换蜂窝模式和服务退出会取消并等待恢复
任务收尾；原 SIM 暂不可用时也能停止等待。VoWiFi 保留独立意图，模组重启期间依原有
版本 / SIM 校验暂停和恢复，不把两条接入合并为同一个开关。

等待 / 恢复 / 暂停、尝试次数和最近成功时间进入共用网关状态、设备实例和诊断包。
恢复过程中不把 PPP 存在或旧注册缓存显示为可用线路。重复停止现在保留先前的远端
注销确认，不再在第二次清理中将结果覆盖为未知。

显式管理员实机测试使用电信 PLMN 460-11：首次真实注册成功后，注入**所有者本地的
续期失败信号**，不是伪造运营商 SIP 响应；原会话失效后控制器自动重建一次并获得新的
真实注册，随后桌面停止完成远端注销、PPP 释放，额外观察 5 秒未再次启动。
通过报告：`publish/host-ims-auto-recovery-20261013/tests/host-recovery-live-stop-fixed.trx`。
首轮报告 `host-recovery-live.trx` 保留为失败记录：自动注册已成功，但重复停止丢失注销
确认使断言失败；上述修正后重跑完整用例通过，不把首轮写成通过。

当前恢复是完整交接，会短暂重启模组，**尚未实现健康 PPP 复用或通话无缝保持**。
该用例验证所有者恢复路径，不代表真实运营商 500 / 503 / Retry-After 已实测，也不代表
3600 秒授权的 SIP 自动续期或超过旧约 18 小时故障窗口的隔夜耐久已通过。
本轮未拨号、发短信或打开麦克风。VoWiFi 实网验证仍按用户安排等待换卡。

全量离线回归 **1104 通过、10 项显式实机测试跳过、0 失败**，报告为同目录
`host-recovery-complete_*.trx`；其中可选硬件分支默认未开启，不能视为全部实机验收。
独立显式只读复查 `host-recovery-final-identity.trx` 的 5 项均执行通过，SIM 为 READY，
模组 IMS 为 `1,1`，状态为 ModemInternalOnly；另外读回 QSIMSTAT 为 `0,1`。
发布包管理员只读自检确认 RAS 连接为 0，独立系统查询确认 IMS PPP 接口和 owner
进程均为 0，IPv6 路由数回到 23。没有保留本轮 Windows IMS 会话或后台耐久监测。

最新候选 `publish/host-ims-auto-recovery-20261013/app/VoWin.exe` 为 **22.21 MiB**，
23,283,781 字节，SHA-256 `0FD6A4DA4865205BB07C47D79A412D875A60351B84E3D143783B7A2403AE1217`。
依赖系统 .NET 10 Desktop Runtime，不包含 PowerShell；沿用已验证的原生组件包，
没有降低 Program Files 可信路径或路由隔离保护。管理员自检报告为同目录
`runtime-admin-check-stop-fixed.json`，未访问模组、修改路由或启动子进程。
证据索引 `publish/host-ims-auto-recovery-20261013/evidence.json`。

## 2026-10-13 注册失败反馈与跨 flow 退避

共享 SIP 注册层新增结构化失败信息：SIP 状态码、`Retry-After` 秒数、头格式是否无效、
是否要求新 flow 和接收时间。不保留原始头、注释、身份或 AKA 密钥。
续注册的 503 也交给所有者重建，不重复使用失效 flow；503 未带 Retry-After 按
[RFC 3261 21.5.4](https://www.rfc-editor.org/rfc/rfc3261.html#section-21.5.4) 的 500 语义处理。

退避门控由每个 Host IMS 卡槽 / VoWiFi 管理器持有，按 SIM 隔离，在当前进程中跨 SIP
对象销毁、停止 / 重开和新 flow 保留。有效 `Retry-After` 不被手动点击、换网络或系统
时钟跳变缩短；内部以单调时钟计时，等待可以取消，较长服务器时间不截短为本地重试间隔。
Host IMS 在模组交接前拒绝提前重试；VoWiFi 在 DNS / IKE / SIM 鉴权之前等待。
这是进程内状态，**尚未持久化到应用重启后**。

解析支持秒数、嵌套注释及参数；`duration` 不加入等待时间。重复头、溢出、矛盾参数或
损坏的引号不当作 0 秒。实现采取保守策略：无有效退避时间的 403、或无效 Retry-After，
暂停自动注册重试；用户显式重试可解除暂停，但不能清除仍有效的服务器计时。
该暂停是本实现的安全策略，不宣称覆盖全部运营商的注册拒绝处理变体。
语义参考 [RFC 3261 20.33](https://www.rfc-editor.org/rfc/rfc3261.html#section-20.33)。

网关状态卡、紧凑摘要与导出诊断区分「等待运营商退避」「注册重试已暂停」，展示可重试
时间，而不是笼统标为连接失败。Host IMS 状态保留结构化反馈，供后续自动重建使用；
该阶段尚未实现 Host IMS 故障后自动回到 Windows 模式；后续实现与验收见本文顶部。

新增协议 / 取消 / 网络唤醒 / 按卡隔离及状态显示回归已执行；全量离线结果
1084 通过、9 项显式实机测试跳过、0 失败，报告在 `publish/ims-retry-after-20261013/tests`。
这些测试不访问真实 SIM、不建立运营商 IKE、不拨号或发短信，不证明真实运营商已返回并
正确执行 Retry-After。候选包 `publish/ims-retry-after-20261013/app/VoWin.exe` 为 22.17 MiB，
沿用上一轮已验证的原生 owner 与依赖包，SHA-256
`20D4B4741A973C096328646EA2B10838CE89FDE0E686F1D09FF966E6A25D2043`。
发布包管理员只读组件自检通过（`runtime-admin-check.json`）；未访问模组或写路由。
新增代码的正常 Host IMS 实测也已通过：
`publish/host-ims-live-20261013/host-ims-retry-after-baseline.trx` 完成 401 → QMI USIM AKA →
C# ESP → 200，60 秒初始租约下保持注册 90 秒并收到 4 次 PPP 续租 ACK，远端注销确认成功。
测试后 5 项只读身份 / PDN 预检再次全部通过，独立核验 RAS、IMS PPP 接口、owner 进程均为 0，
AT 读回模组 `ims=1,1`。证据索引 `publish/ims-retry-after-20261013/evidence.json`。
本次未拨号、发短信或打开麦克风；正常注册路径通过不等于真实运营商退避分支或 SIP 自动续期
已经验收，也没有留下隔夜测试。VoWiFi 实网测试仍等用户换卡。

## 2026-10-13 原生 PPP 策略写入实测修正

真实 PPP 已暴露离线模拟未覆盖的启动回归：RAS 建立成功后，直接把
`GetIpInterfaceEntry` 的完整结果改一个位再交给 `SetIpInterfaceEntry`，返回错误 87。
当时 IPv6 SitePrefixLength=64、MTU=1432，不能把原因归结为无效的前缀长度。
失败发生于路由隔离阶段、尚未发送 REGISTER；owner 已执行模组 IMS 恢复。

当前改用 Windows SDK 要求的 `InitializeIpInterfaceEntry` 构造 SET 请求，只提供准确的
IPv6 family、接口 index / LUID 和 `DisableDefaultRoutes`，其余字段保留原生的“不修改”
哨兵值。写入前后分别读取完整快照，核对目标位及所有其他可写属性；不放宽路由守卫，
不重设 MTU、metric、邻居发现或其他接口配置。
依据 [Microsoft SetIpInterfaceEntry](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-setipinterfaceentry)
及本机 Windows SDK `netioapi.h`。下方完整注册实测已通过，证明本机 PPP 的策略写入及读回可用，
不证明长时故障已消除。

全量离线回归 1043 通过、9 项显式实机测试跳过、0 失败，报告在
`publish/host-ims-policy-fix-full-tests-20261013`。研究候选包为
`publish/host-ims-policy-fix-20261013/app/VoWin.exe`，22.16 MiB，SHA-256
`2E77E3CF882C5FAF71A057C56343E39C49FBBBA0F6052A620B7EEAE609AADBD9`。
管理员运行组件只读自检通过，实测 owner 从受保护的 Program Files 缓存加载；
此自检不访问模组、不写路由，不代替真实注册与隔夜耐久验收。

首次用该修正版注册时，模组虽能响应 AT，但 `CPIN?` 返回 `CME 13`（SIM failure）、
`QSIMSTAT?` 为 `0,0`，因此身份保护在 PPP 启动前拒绝继续。一次受控基带重启后读回
`CPIN READY`、`QSIMSTAT: 0,1` 和 `ims=1,1`，随后完整注册测试通过。
不能据此认定 SIM 故障由 PPP 策略改动造成，也不能跳过身份校验掩盖故障。
当前按用户选择先验电信 PLMN 460-11 的 Host IMS，VoWiFi 实网测试待换入支持的卡。

显式实机报告 `publish/host-ims-live-20261013/host-ims-live-policy-fixed-sim-recovered.trx`：
网络返回初始 401，经 QMI USIM AKA、C# ESP 保护后收到 200，登记有效期 3600 秒；
保持 90 秒期间注册有效、默认路由修复计数为 0，最后远端注销确认成功。
这次 PPP 租约为 300 秒，心跳间隔 100 秒，因此 **PPP 续租计数为 0**；不能把它写成续租验收。
退出后独立只读检查 RAS 连接为 0；重新显式执行的 5 项身份 / PDN 预检和 4 项原生策略测试
均通过，报告 `post-registration-identity-and-policy.trx`。

续租复测 `host-ims-live-short-lease.trx` 也已执行通过：
`VOWIN_HOST_IMS_PPP_LEASE_SECONDS=60`、`VOWIN_HOST_IMS_HOLD_SECONDS=90`，
再次完成 401 / QMI USIM AKA / C# ESP / 200，90 秒内收到 **4 次 PPP 续租确认**，
注册一直有效，默认路由修复计数为 0，最后远端注销确认成功。
期间只读系统查询确认 PPP `IgnoreDefaultRoutes` 已启用，MTU=1432、metric=9999 不变。
该用例现在要求：保持时间超过初始租约时，必须至少收到一次续租 ACK 且存在续租时间。
这是实际租约心跳验证，不是运营商 SIP REGISTER 自动续期验证。

第二轮退出后，独立只读核验 RAS 连接、IMS PPP 接口、owner 进程均为 0；
5 项显式身份 / PDN 预检再次全部通过（`final-restoration-identity.trx`），
AT 读回 `CPIN READY`、`QSIMSTAT: 0,1`、`ims=1,1`，IMS context 已恢复活动。
测试已结束，未保留 Windows Host IMS 注册或启动隔夜监测，模组已回到内部 IMS。
此阶段未拨号、发短信或打开麦克风；运营商 SIP 自动续注册、故障后自动回到 Host IMS、
超过旧故障窗口的隔夜耐久仍未验收，用户换入 VoWiFi 卡后再做另一条接入的实网验证。

## 2026-10-13 实机枚举纠正与只读预检

`Win32_SerialPort` 当前仅返回蓝牙 COM3 / COM4，但注册表
`HKLM\HARDWARE\DEVICEMAP\SERIALCOMM`、`.NET SerialPort.GetPortNames()` 与 PnP 均能看到
Quectel AT COM6、NMEA COM7、DM COM8、Modem COM9、WWAN 与 USB Audio，驱动状态正常。
此前仅据 `Win32_SerialPort` 判断「未接入模组」不成立；产品发现路径已经使用注册表 / .NET
枚举，本次误判来自测试端的检查方法，不是产品无法发现 COM6。

显式开启实机只读门控后，`HostImsRealIdentityProbeTests` 的 5 项均执行并通过：
SIM 归属 PLMN 460-11、EF.IMSI 权威归属可用且无冲突、QMI 与 AT 绑定验证通过、
USIM 通道就绪、LTE PANI 可读；当前卡无 ISIM，需采用已校验的 USIM 回退身份。
模组 IMS 已启用且有活动 IMS context，但 Windows-owned 地址和已验证路由均为 0，
状态为 `ModemInternalOnly`。模组在线不等于 Windows Host IMS 已注册。
测试不执行 AKA、PDN 激活、IMS 模式切换、拨号或短信；不输出 IMSI / ICCID / IMEI、
PDN 地址和小区位置。报告在 `publish/real-modem-preflight-20261013`。

## 2026-10-13 续注册 nonce 与失效 flow 分类

共享 `SipRegisterSession` 现在处理成功 REGISTER 的 `Authentication-Info nextnonce`：
按 TS 24.229 5.1.1.4.1(f) / 5.1.1.4.2(a) 替换缓存 Authorization 中的 nonce，
保留上次计算的 AKA response、realm、opaque、qop / nc / cnonce；不保存额外 RES / CK / IK，
也不因收到 200 OK 中的 nonce 就消费一次 USIM AKA。后续 401 仍执行新的 USIM 挑战。
支持带转义引号、反斜线和逗号的参数；空 nonce、重复 nonce、矛盾的多个头及格式错误
不选择其中一个继续，而是使会话失效、清除缓存凭据并要求新的初始注册流程。
仅更新 nonce，不声称实现了完整 `rspauth` 验证或普通 HTTP Digest 的 nextnonce 重计算。

续 REGISTER 在首个响应或鉴权后的响应收到 403 / 408 / 500 / 504 时，立即清除
`LastResult` / Authorization，并设置 `RequiresNewRegistrationFlow`。相同对象不再发出
REGISTER 或注销；刷新任务通过 `RegistrationFailed` 交回所有者，不再以旧 SA / 旧认证
重复三次。初始注册失败与续期丢失 flow 分开处理；本地瞬时发送故障仍保留有界重试。
依据 [TS 24.229 5.1.1.4.1、5.1.1.4.2](https://www.etsi.org/deliver/etsi_ts/124200_124299/124229/17.14.00_60/ts_124229v171400p.pdf)。

该阶段恢复能力边界：VoWiFi 所有者进入现有的整会话恢复流程；Host IMS 仅停止
失效的 Windows 会话并释放 PPP、请求恢复模组 IMS，当时尚未自动恢复成 Host IMS。
后续跨 flow 退避与桌面自动重建见本文顶部；完整 `rspauth` 验证、真实运营商 nonce
变体以及长时在线仍需实机验证，不能把协议修正视为 Host IMS 长时在线已完成。

相关协议回归 82 项通过，使用内存 SIP 对端和伪 AKA，不访问真实 SIM、不拨号或发短信。
最终全量离线回归 1038 通过、9 项显式实机测试跳过、0 失败；记录在
`publish/ims-flow-final-tests-20261013`。新版 `publish/ims-registration-flow-20261013/app/VoWin.exe`
为 22.16 MiB，嵌入组件只读自检通过；实机长时验收仍缺失。此前的硬件缺席判断已由上方实机预检更正。

## 2026-10-13 长时 PPP 隔离与 IMS 续注册修正

旧日志两次在约 18 小时后以 `PPP default route isolation was lost` 退出；
这证明触发了本地路由守卫，不证明运营商主动注销，也未确定默认路由重现的系统原因。

[C# 承载管理器](../src/VoSharp.HostIms/WindowsRoutes.cs)新增 IPv6 接口策略：
读取完整 `MIB_IPINTERFACE_ROW` 作为校验快照，以原生初始化的 SET 请求只修改本次受控
PPP 接口的 `DisableDefaultRoutes`，写入后读回。接口索引、本地地址与 LUID 必须一致；停止或部分应用失败时只恢复自己
修改的策略位，保留其他接口属性，索引被复用时不触碰替代接口。
策略禁止使用默认路由，不保证路由表永远不出现 `::/0`。原默认路由重新发布时，
仅在 next-hop、metric、protocol、origin、接口身份均与启动时一致且策略仍有效时删除，
随后复核无默认路由和所有自有 `/128`。未知路由、策略丢失、删除失败仍 fail closed。
修复成功输出 `VOWIN_IMS_DEFAULT_ROUTE_REISOLATED count=N`；单调计数进入卡槽承载状态、
桌面分层状态详情与导出诊断，不冒充 IMS 注册或媒体验证。
原生接口与语义依据 [Microsoft MIB_IPINTERFACE_ROW](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_ipinterface_row)。

共享 SIP 会话还修正了续 REGISTER 再次发送初始空 nonce / response 的问题：
保存本会话上次计算的 Authorization，续注册和注销携带该值，401 新挑战替换；
不保存原始 AKA 向量，RES / CK / IK / RAND / AUTN 仍在交换后清零。旧 VoWiFi 日志
显示 DPD 成功而续期收到 500，此协议修正可能相关，但尚不能认定是唯一原因。
依据 [TS 24.229 5.1.1.4.2](https://www.etsi.org/deliver/etsi_ts/124200_124299/124229/17.14.00_60/ts_124229v171400p.pdf)。
续期最终失败现在保留末次具体原因；`Authentication-Info nextnonce` 等变体尚未完成本轮互通验收。

离线回归覆盖默认路由重现、自愈失败、未知路由拒绝、策略回滚、LUID 复用、
修复计数管道、AKA 续注册、新挑战及注销。最初仅据 `Win32_SerialPort` 枚举到蓝牙
COM3 / COM4 而推断没有模组，该推断已更正；原生策略写入、超过旧故障窗口的隔夜耐久
及真实运营商续期仍需单独验收，不能用硬件在线或只读预检代替。

## 2026-10-09 独立实例与线路所有权

桌面端采用一个窗口内的每模组管理上下文，不创建每模组桌面进程。蜂窝模块 IMS 与
Host IMS 互斥，VoWiFi 独立连接、停止和自动连接；蜂窝交接需要重启时会暂停并按用户
当前意图恢复本模组的 VoWiFi。运营商是否接受同时保持两条 IMS 注册仍需逐卡验证。

原生 owner 通过 SetupAPI 的 USB ContainerId 配对 AT/RAS/WWAN，重启后重新核对
ContainerId 与 IMEI；RAS entry 为 `VoWin IMS <IMEI-hash-prefix>`，路由和恢复守护只管理
该 entry，不等待或修改其他模组的 PPP。旧版恢复状态仍兼容原 entry。
路由检查使用实际绑定的接口与源地址，允许不同模组使用同一 P-CSCF，具体 API 语义见
[Microsoft GetBestRoute2](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getbestroute2)。

已修复桌面拨号等待 INVITE 最终响应时禁止挂断的问题。拨号准备阶段取消等待，INVITE
存在后交给呼叫管理器发送 CANCEL，并继续处理迟到的 200 OK（ACK/BYE），不提前取消其
响应读取。挂断失败保留呼叫状态并显示错误，不直接伪装为结束。

新版受保护 owner 的串行实测通过：QMI USIM + C# ESP 运营商注册/远端注销（约 101 秒），
owner 崩溃后的独立恢复、RAS/路由清理、`ims=1,1` 与任务撤销（约 98 秒）。未拨号、发短信
或重启 Windows。首轮两个硬件测试并行触发 COM6 资源冲突，已改为串行并为相关测试加上
不并行 collection；失败首轮不计作通过。

后续桌面命令实网测试通过（约 106 秒）：在运营商已返回 Ringing、桌面 DialCommand 仍
pending 时执行 HangupCommand，约 0.3 秒完成挂断和 INVITE reader 收尾；媒体路由归零、
IMS 注册保持，最后停止 PPP。测试不打开主机麦克风，也不发短信；这不是人工点击或
接通后 BYE/双向语音验收。实验记录位于 `publish/desktop-host-hangup-live-20261009`。

随后接通后的桌面挂断实测也通过（约 130 秒）：第二通收到 200 OK、进入 Active，保持
20 秒后执行 HangupCommand，约 0.5 秒完成本地收尾；用户确认手机正常结束通话。
媒体路由归零、IMS 注册保持，测试最后停止 PPP。记录位于
`publish/desktop-host-connected-live-20261009`。这次仍只测信令、不开主机麦克风、不发短信；
不能将此结果当作新版桌面双向语音或人工点击验收。

USB Audio 已按物理 ContainerId、Core Audio endpoint ID 和 WinMM 编号独立配对，每模组
各自持有 worker、PCM 管道、取消与回收状态；同名双端点及旧 owner 清理回归通过，当前
COM6 的只读配对和实际打开/关闭（仅静音 PCM、无主机录音或运营商呼叫）也通过。
最终发布包的独立 USB 音频 helper 也在 COM6 显式启动、传输静音 PCM、正常停止通过；
记录位于 `publish/usb-helper-multimodule-live-20261009`。默认串行回归为 781 项通过、
6 项实机测试跳过，实机项目必须另行启用；最终 UAC 自检确认管理员令牌和无 RAS 残留。
端点解析依据 [Microsoft WinMM / Core Audio 互操作](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-roles-for-legacy-windows-multimedia-applications)
和[音频端点 ContainerId](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-endpoint-container-id)，
不能用产品名称或固定 WinMM 索引持久化设备身份。
标准 UAC 的独立路径不需要 ADB；QDC507 特殊兼容路由另需核对物理接口与 ADB 身份。
[AOSP Windows USB transport](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/client/usb_windows.cpp)
的 legacy 注册未传 usb_name，当前固件序列号还可能重复为 `?`，因此不能按序列号或顺序配对。
真实双模组并发语音、Host IMS 与 VoWiFi 双注册保留为未验收项；用户当前只有一台模组。
后续已接入 uint64 ADB transport ID + DMS IMEI 配对：Windows 所选 slot 缓存的 IMEI 与
模组侧 3,660 字节只读身份助手核对，执行路由命令前在同一个 shell 内再次校验助手哈希
和 IMEI，防止重连后编号复用。助手仅发送 DMS Get IDs，不修改无线配置，不含 Linux
运行时或 IMS 协议栈；现有 C# Host IMS 路径不变。缺少或歧义身份、固件 ABI 不符时拒绝
操作。合成双匿名通道与编号复用回归、当前单模组 QMI/ADB 同身份及错误身份拒绝已通过；
两台真实匿名 QDC507 的同时路由/语音仍未验收。源码与重建方法见
[QDC507 身份助手](../../qmi/tools/qdc-identity/README.md)。

后续通话目标审查补齐了接听、拒接和 DTMF 的显式卡槽校验：卡槽已断开会报错，不再
回退到当前选中设备；省略目标时优先操作呈现中的通话。旧挂断/拒接的异步结果不能把
另一模组接替显示的活动/保持通话改成“已结束”，来电浮窗失败可重试且有新旧呈现代际保护。
该轮 10 项新增回归及浮窗控件检查通过，累计 791 项通过、6 项显式实机测试默认跳过；
记录在 `publish/module-call-ownership-final-20261009`、`publish/module-call-desktop-final-20261009`。
这一轮未发起运营商通话、短信或 IMS 交接，不能替代双模组实机验收。

身份配对补齐后的最新串行回归为 828 项通过、7 项实机默认跳过；另显式启用的单模组
身份守卫实测通过。记录在 `publish/qdc-multimodule-final-20261009` 和
`publish/qdc-identity-live-final-20261009`。首轮实测因旧 ADB 的额外 CR 行尾导致文本断言
失败，修正断言后复测通过；原生源码重建也与内嵌字节一致。本轮只部署临时身份助手并
查询 DMS，不拨号、发短信、重启模组或修改音频路由。

## 2026-10-08 当前桌面运行链路

桌面程序通过“一键启用通信”管理 Host IMS，诊断统一位于“日志与诊断 → 连接详情”。
`VoSharp.HostIms.exe` 现包含 C# RAS worker（`--ras`），直接调用 Windows RAS API；
无需原生 `RasImsPppProbe.exe`、PowerShell、`schtasks.exe` 或 `netsh.exe`。
独立恢复守护、SYSTEM 开机任务、父进程 EOF/有限租约和精确路由隔离仍保留。
CLI 只需配置 `VOWIN_HOST_IMS_PPP_OWNER`，原 RAS 路径参数仅保留 API 兼容、不执行。
SDK ABI、只读枚举和自有临时电话簿写入/回读已通过。管理员受控实机验收已完成：

- 新 C# RAS 建立真实 PPP，精确媒体路由增删与正常停止通过（约 96 秒，包含模组重启及恢复）。
- 仅终止本次启动的 owner，保留独立守护；RAS worker 随父管道断开挂断，守护恢复
  `ims=1,1`，核验无 PPP、RAS 或接口路由残留，并撤销该次 SYSTEM 恢复任务（约 94 秒）。
- COM 注册的 SYSTEM 任务实际启动受保护的 C# 程序，在线 owner 分支返回成功且未触碰模组；
  这是启动/互斥验收，不是 SYSTEM 实际恢复或 Windows 整机重启验收。
- 最终内置 owner 上的运营商注册通过，卡路径为 QMI USIM，使用 C# ESP；随后远端注销确认成功，
  PPP 正常释放并恢复模组 IMS（约 103 秒）。本轮未拨号、发短信或测试新版桌面端的通话听感。

上述实机测试单独显式启用，不应把默认回归中未启用的 lab gate 算作硬件验收。
`ManagedHostImsRecoveryLiveTests` 的崩溃测试使用 `VOWIN_TEST_MANAGED_OWNER_CRASH=1`，
SYSTEM 启动测试使用 `VOWIN_TEST_MANAGED_SYSTEM_RECOVERY=1`；两者必须指定管理员受保护缓存中的
`VOWIN_HOST_IMS_PPP_OWNER`，崩溃测试另指定已核实的 `VOWIN_HOST_IMS_AT_PORT`。
实机测试必须串行运行并先停止桌面通信。完整 Windows 重启、SYSTEM 实际恢复、多日耐久与
其他模组/运营商仍待验收，不能套用旧 C++/脚本路径的实网结果。
下方研究记录按时间保留，其中早期的脚本、手动注册界面和原生 helper 不代表当前交付路径。

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
2. **QMI WDS 多 PDN**：当前 Windows Quectel 驱动的私有控制文件已可由应用使用，可申请独立 WDS 服务文件/CID，并读取 WDS runtime settings；当前 IPv4 P-CSCF 列表有解析支持，实测 IPv6 会话未返回 P-CSCF。已有显式持有的独立 WDS CID/handle 租约：运行时 APN、地址或 P-CSCF 不合格即 Stop 回滚，释放时先 Stop 再释放 CID；仍需将 raw-IP/QMAP 数据流可靠映射到 Windows 网卡并验证 P-CSCF 路由，尚未接入自动注册。控制面可访问不代表驱动向 Windows 暴露了第二条 IMS 数据流。
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
AMR-NB 现在解析音频段 `fmtp` 的 `mode-set`，上行语音和空闲帧都只用该子集允许的模式；来电 SDP 应答保留限制，空、重复或越界的模式集会拒绝协商。本地测试用只允许模式 2 的 SDP 验证了语音与空闲帧模式。没有 `mode-set` 时维持原有模式 7 语音、模式 0 空闲帧；真实运营商媒体仍未验收。

`HostImsRegistrationClient` 只接受探针给出的 `HostRoutable` 端点候选，把 SIP socket 绑定到该 IMS 本机地址，再复用现有 REGISTER 和自动刷新。`RegisterWithCardAsync` 优先从 ADF.ISIM 读取 EF.IMPI（`6F02`）、EF.DOMAIN（`6F03`）及第一条 EF.IMPU（`6F04`），在 ISIM 上执行 IMS AKA。单卡且 QMI UIM 能明确映射到活动物理槽时，先尝试 QMI 逻辑通道；若身份读取失败，才在 AKA 前改用 AT。选定 QMI 后不会因 AKA 失败而通过 AT 重放同一挑战。首次 SIP 发包前及每次 AKA 前仍经 AT 从当前卡读取 EF.ICCID 核对，防止 QMI/AT 指向不同卡。仅当 `AT+CUAD` 返回可解析的其他 3GPP UICC 应用 AID 且 ISIM 无法打开，才判定 ISIM 缺席；此时必须有可靠的归属 PLMN/MNC 长度、无 PLMN 冲突，并通过活动卡的 EF.ICCID 严格核对身份，才从 IMSI 派生临时 IMPI/IMPU 并在 USIM 上执行 AKA。USIM 分支会在网络挑战前预检 QMI 逻辑通道，成功则用 QMI AKA，否则保留 AT AKA；QMI AKA 失败不会重放到 AT。EF.ICCID 在每次 AKA 前复核；CUAD/ISIM 读取失败、MNC 长度不明或卡身份不符时均拒绝注册。ISIM 文件规则依据 [3GPP TS 31.103](https://www.etsi.org/deliver/etsi_ts/131100_131199/131103/18.01.00_60/ts_131103v180100p.pdf)，无 ISIM 时的身份派生依据 [3GPP TS 23.003](https://www.etsi.org/deliver/etsi_ts/123000_123099/123003/19.07.00_60/ts_123003v190700p.pdf)。

现已通过独立卡槽会话接入 **系统状态 → 蜂窝 Host IMS 数据面 → 实验性 Host IMS SIP 注册**，以及 CLI 的 `host-ims register|status|stop`。注册前会重新运行探针并验证所选端点仍存在；持有已核验的 CID 5 PPP 租约时，未手选端点可按该租约公布的 P-CSCF 顺序自动选择首个路由已核验的匹配候选；其他多候选情形仍须明确选择。用户主动停止且原卡/模组仍有效时，先结束 Host 通话，再发送与原 Contact 匹配的 `Expires: 0` REGISTER；若网络返回 IMS-AKA 401，再用同一张卡回答挑战，最多等待 10 秒。只有 200 OK 才报告远端注销已确认；超时或失败时仍关闭本地 socket，并提示远端绑定可能保留到有效期结束。换卡、eSIM 身份变化、拔模组或进程清理仅本地失效，不用新卡为旧绑定发送 BYE 或注销。此入口不会激活 IMS bearer；蜂窝 P-Access-Network-Info 现可在注册前从模组的已驻网 LTE 小区自动读取，读不到时省略，不能编造位置。真实运营商仍需验证是否接受该值及 `ipsec-3gpp` 数据面。**REGISTER 成功仍不意味着真实 Host VoLTE 通话或 SMS over IMS 已可用。**
LTE 小区字段按 [Quectel `AT+QENG="servingcell"` 手册](https://www.quectel.com/content/uploads/2021/03/Quectel_EP06EG06EM06_AT_Commands_Manual_V1.0.pdf)读取同一条已驻网记录的 MCC/MNC、TAC、ECI 和 FDD/TDD，不把归属 SIM 的 PLMN 拼到漫游小区上；按 [3GPP TS 24.229 §7.2A.4.3](https://www.etsi.org/deliver/etsi_ts/124200_124299/124229/18.07.00_60/ts_124229v180700p.pdf)生成 `3GPP-E-UTRAN-{FDD|TDD};utran-cell-id-3gpp=` 后的 3 位 MCC、2/3 位 MNC、4 位十六进制 TAC 和 7 位十六进制 ECI。`LIMSRV`、非 LTE、缺失或越界字段不会被使用。当前插卡模组的只读测试确认可解析 FDD 小区；日志不输出具体位置。每次首次或续期 REGISTER 以及新外拨 INVITE 前重新读取小区；若本次读取失败，会清除旧 PANI，避免沿用过期位置。注册客户端 profile 随之更新，但小区切换本身不会立即触发 REGISTER，正在进行的通话也不会被改写。本地假 P-CSCF 测试覆盖注册后 FDD→TDD 变化、外拨 INVITE 使用新 PANI 及随后读数失效时省略该头部；真实运营商与通话中小区切换仍未验收。

SIP 客户端响应会核对顶层 Via 的 `sent-by` 与原请求一致。Windows Host IMS 的直连 UDP socket 还只接收所选 P-CSCF 地址发来的包（允许源端口变化）；这是单 P-CSCF 模式的保守边界，不是加密认证，也不能代替尚未实现的蜂窝 IMS IPsec。若运营商从其他 P-CSCF 地址回包，当前会丢弃并表现为超时，需要在多地址/切换策略明确后再支持。

若 P-CSCF 在 401 挑战中要求 `sec-agree` 却没有提供 `Security-Server`，或者在首次 200 OK 中要求安全协商而本机尚未建立 SA，注册会明确失败；不会把未受保护的会话报告为已注册。
AKA nonce 解码后的临时字节缓冲区，以及交给卡认证回调的 RAND/AUTN，现在在成功和异常路径均清零；回调抛错的本地测试核对了两个数组被擦除。RES/CK/IK 原有清理继续保留。发送前回调还会给已建立 SA 上的注销 REGISTER 附加 `Security-Verify`，因此无需为注销另开明文例外。

### 蜂窝 IMS IPsec 数据面状态

已按 [3GPP TS 33.203](https://www.etsi.org/deliver/etsi_ts/133200_133299/133203/18.00.00_60/ts_133203v180000p.pdf) 明确两组传输模式 SA 的方向：UE `port-c` ↔ P-CSCF `port-s` 使用 UE `spi-c` 入站、P-CSCF `spi-s` 出站；UE `port-s` ↔ P-CSCF `port-c` 使用 UE `spi-s` 入站、P-CSCF `spi-c` 出站。`HostImsSaPlan` 将已协商参数和 AKA CK/IK 展开为这两组 SA 的安装请求，使用独立密钥副本并在释放时清零。它只是可测试的映射，**还没有向 Windows 安装任何 SA**。

Windows [WFP 手动 SA 流程](https://learn.microsoft.com/en-us/windows/win32/fwp/manual-ipsec-sas)还需要受限的入/出站过滤器、SA context、入站 SPI 分配以及两组 SA 的安装与清理。IMS 的 UE SPI 必须在初始 REGISTER 中声明，因此 Windows 后端需要在发包前预留两组入站 SPI，再在收到 401 的 `Security-Server` 和 AKA CK/IK 后激活 SA；当前 `IChildSaBackend.InstallChildSaAsync` 的单阶段接口尚不足以完成此握手。默认产品路径仍不发送声明支持 `ipsec-3gpp` 的 REGISTER；经单独授权的初始探针现可用 C# 用户态 ESP 提案生成随机 SPI，但尚未激活 SA 或验证完整注册。

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
IMS context。把 COM6 当作 QMUX 设备会失败。

这台测试机上，Windows 识别到 `Quectel USB AT Port (COM6)`、`Quectel Wireless Ethernet Adapter`（驱动服务 `qcusbwwan`），并在移动宽带列表中显示物理接口“手机网络”。只读 AT 返回 `OK`、`+QSIMSTAT: 0,0`、`+QCFG: "usbnet",0`，移动宽带网卡当前未连接，IMS context 查询返回 `ERROR`。这证明 COM/驱动枚举可用，但**既不能证明也不能否定**插卡后的多 PDP/IMS 能力。`netsh mbn show interfaces` 的 `Additional PDP Context: No (Physical interface)` 描述当前条目是物理接口，不能单独当作固件“不支持多 PDP”的结论。

Microsoft 的[多 PDP 开发说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/mobilebroadband/developing-apps-using-multiple-pdp-contexts)要求设备固件/驱动能暴露多数据流；通过 Windows `ConnectivityManager.AcquireConnectionAsync` 申请专用 APN 还需要运营商授权的 `cellularDeviceControl` [受限能力](https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.connectivitymanager.acquireconnectionasync)。因此普通 WPF 进程不能把“调用 Windows API 激活 ims”当成已具备的后端。若现有 `qcusbwwan` 驱动不向应用开放额外 context，需另有获授权的 WWAN 路径、厂商 QMI WDS 接口或可独占的数据端口；这些都尚未在本机验证。

### 插卡后的 QMI 控制面复核（2026-09-29）

PNP 枚举仍没有通用 raw-QMI 设备，但当前 `qcusbwwan` 网卡在注册表发布了
`QCDeviceControlFile`。根据 [Qualcomm 开源 Windows USB 驱动](https://github.com/qualcomm/qcom-usb-kernel-drivers)
中的私有 IOCTL/服务文件协议，`qmiSharp` 已可自动打开唯一 Quectel 适配器的控制文件，
由驱动分配服务文件与 CID，再收发 QMI 服务消息体；不改驱动、不使用 ADB。
实机只读 DMS、NAS、UIM、WDS 查询成功，QMI/AT IMEI 一致，故 QMI 优先、AT 回退
路径已具备实际控制通道。多适配器时不自动猜选，附着 AT 后仍要核对 IMEI。

修正 QMI IMSA TLV/枚举解析后，管理员只读查询当前模组返回 IMS 经 WWAN
已注册、IMS 语音服务 available。它证明这张卡在模组内部 IMS 路径存在
VoLTE 服务，不证明把模组内 IMS 关闭并交给 Windows 后运营商仍会接受注册。
查询没有发 SIP、拨号或短信。

当前 SIM 已驻网，模组内 CID 5 的 `ims` PDP 已激活并报告 IPv6 地址与 P-CSCF；
Windows Quectel 网卡仍断开，未持有该地址。`netsh mbn` 对此物理接口报告
`Additional PDP Context: No`，并非证明其固件不支持多 PDP。

只读 QMI 检查证实两个 WDS CID 可以同时存在，WDA 报告 RawIP + QMAP v1。独立 WDS
客户端在模组内 IMS 开启时请求 IPv4 被拒绝为内部原因 208（IPv4 PDN 不允许），
请求 IPv6 被拒绝为 241（接口已被相同配置占用）。短暂将 `AT+QCFG="ims"` 从原值
`1` 改为 `2` 并重启后，独立 IPv6 `ims` WDS bearer 成功建立并获 IPv6 地址；但
Windows 网卡仍未接管该地址，WDS 设置也没有给出 IPv6 P-CSCF。试验自动 Stop 并
释放 CID，原配置 `1` 已恢复、再次重启，并核验 `ims=1,1` 与内部 CID 5 激活。
这证明“QMI 能建 bearer”仍不等于“Windows 拥有该 bearer”。

Windows MBN 配置库中已有手动 `ims` Additional PDP Profile。取得真正的管理员
令牌后运行[受控诊断脚本](../../qmi/tools/Probe-WindowsImsPdp.ps1)，得到明确失败：
接口不支持多个 PDP 上下文。当前 Quectel `qcusbwwan` 20.0.72.21 驱动向 Windows
报告最大激活上下文数 **1**；同系[开源驱动](https://github.com/qualcomm/qcom-usb-kernel-drivers)
在 `OID_WWAN_DEVICE_CAPS` 中也把 `MaxActivatedContexts` 固定为 1。这是 Windows
驱动暴露能力的边界，不能据此否定模组本身的多 CID 能力。
后续核对开源驱动的 [WDS mux 绑定实现](https://github.com/qualcomm/qcom-usb-kernel-drivers/blob/a42c05276c1aa45eab231760502665c8ccef6b14/src/windows/ndis/MPQMUX.c#L26060-L26165)发现 mux ID 由 miniport 为其自身适配器分配。它不是当前 Quectel 二进制的源码证明，但结合实机只呈现一个断开的 WWAN 接口，说明单独补发 WDS Bind Mux 命令并不能使 Windows 获得第二个 IMS IP 接口，甚至可能与驱动的 mux 分配冲突。后续 QMAP 研究应针对驱动/NDIS 多数据接口能力，而非仅在用户态增加一个 QMI 控制命令；PPP 仍是当前已实机打通的 Windows IMS 承载。

为隔离多 PDP 限制，又尝试把 `ims` 作为唯一的 Windows 主 PDP。临时 IPv6 Profile
可被 Windows 解析，不修改已有 Profile；[交接脚本](../../qmi/tools/Probe-WindowsPrimaryImsHandoff.ps1)
仅在确认原配置 `ims=1,1` 时短时停用模组内 IMS、重启并等待驻网，再调用
[主 PDP 探针](../../qmi/tools/Probe-WindowsPrimaryImsPdp.ps1)，最后断开并恢复原配置。
实测 `netsh mbn connect` 返回 `WWAN_STATUS_FAILURE`。WWAN 事件里 `APN=ims` 的
`SET ContextState` 曾收到 `Activated`，但 WWAN 随即下发 `Deactivate`；连续 90 秒
内 Windows 网卡都未连接，也没有取得非链路本地 IPv6。故短暂的 NDIS 激活不能算
Host IMS 数据面成功，具体撤销原因仍需驱动/WWAN IP 配置层进一步追踪。实验后
再次核验 `QCFG ims=1,1`、内部 CID 5 激活和 Windows 断开。真正的 Windows
经 WWAN 的 `HostRoutable` bearer 与 WFP `ipsec-3gpp` 后端仍未实现；下述 PPP 路径另测。

### MBIM、PPP 和模组 Linux 侧复核（2026-09-29）

按[移远 QCFG 说明](https://quectel.com/content/uploads/2024/02/Quectel_EC2xEG2xEG9xEM05_Series_QCFG_AT_Commands_Manual_V1.0.pdf)，
在已知 `usbnet=0`、`ims=1,1` 的基线上，用[受控 MBIM 组成探针](../../qmi/tools/Probe-WindowsMbimComposition.ps1)
短时切至 `usbnet=2` 并重启。模块接受命令，AT 口及 USB Audio 仍枚举，但 Windows
`netsh mbn show interfaces` 返回 0 个接口，没有可用 MBIM 路径。脚本在 `finally`
恢复 `usbnet=0`，复核内部 `ims=1,1`、CID 5 激活。这个结果只针对当前
`QDC507GLEFM21` 固件和所装 Windows 驱动，不能推断所有 Quectel 型号都不支持 MBIM。

COM9 的数据拨号口执行 `ATD*99***5#` 返回 `CONNECT`。用
[RAS 单次探针](../../qmi/tools/RasImsPppProbe.cpp) 调用 Windows `RasDialW`，
可以完成链路协商和 CHAP，但在 IPv6CP/IPCP 尚未取得地址时以 RAS 619 断开。
RAS ETW 显示对端拒绝了 CCP 压缩协商包；按照
[移远 PPP 示例](https://quectel.com/content/uploads/2024/02/Quectel_EC2xEG2xEG9xEM05_Series_PPP_Application_Note_V1.1-4.pdf)
禁用 CCP 后，不再出现该协议拒绝，但模组内部 IMS 仍占用 CID 5 时，对端在网络
控制协议请求发出后断链（RAS 628）。不能把第一次的 CCP 拒绝误读成 IPv6CP
拒绝。`--no-ccp` 使用并删除临时电话簿，不修改系统默认拨号项。
探针现接受 `--hold-seconds=15..300`；交接脚本可用 `-HoldSeconds` 传入。
临时电话簿将 PPP IPv6 接口 metric 设为 9999，`--check-phonebook` 可在不拨号时
核对该配置并删除临时文件。2026-09-29 活动 PPP 上复测确认接口 metric 为
9999，但系统只有这一条 IPv6 `::/0` 路由；因此**高 metric 仍会让普通 IPv6
流量走 IMS PPP**，不能当作隔离方案。

为检查模组内部 IMS 是否占用 CID 5，又用
[自动恢复交接脚本](../../qmi/tools/Probe-WindowsImsPppHandoff.ps1) 暂时停用内部
IMS 并重启。首次重启后 Windows RAS 设备尚未就绪，探针安全拒绝拨号；首次
恢复只达到 `ims=1,0`，再次重申原设置、受控重启才恢复 `ims=1,1`。脚本据此
增加 RAS 设备就绪等待及必要时的第二次恢复重启。修订后连续两次复测，均
**成功连接 CID 5**：RAS PPP 投影报告 IPv6 协商错误码 0，IPv4 协商错误码 718。
第二次连接保持 60 秒期间，Windows `VoWin IMS PPP Probe` 接口确实持有 IMS
前缀下的全局 IPv6 `/64`，状态为 Connected，且有经该 PPP 接口的 IPv6 默认
路由。模组 `AT+CGCONTRDP=5` 同时报告活动的 `ims` PDP 和同一前缀。这证明
IMS IPv6 数据面可以通过 USB PPP 进入 Windows，而非仅停留在模组内部。
正常挂断后，交接脚本再次核验恢复 `ims=1,1`；CID 5 内部激活、`usbnet=0`。

进一步核对 `CGCONTRDP` 的字段顺序，发现响应中的 DNS 两项为空，而最后两项
是 IPv6 P-CSCF。此前把它们误认为 DNS、据此声称 P-CSCF 缺失是不正确的。
当前 QMI WDS 独立 CID 在内部 IMS 活跃时查询 runtime settings 返回 `OutOfCall`，
不能代替 AT 读取该 PDP；但 `qmiSharp` 现已补充 WDS TLV `0x2E` 的 IPv6
P-CSCF 列表解析，以供 QMI 自己建立 bearer 时使用。Windows PPP DNS 仍显示
`fec0:0:0:ffff::1/2/3` 占位地址，但 Host IMS 可直接使用 AT 报告的 P-CSCF IP，
不依赖系统 DNS 来发现这两个地址。

修订后的只读 `host-ims probe` 会专门识别由受控脚本创建的
`VoWin IMS PPP Probe` 接口：仅在 Windows PPP 地址与模组报告的 IMS `/64`
前缀一致、接口已连接且到 P-CSCF 的路由核验通过时才判定可用；普通 VPN/PPP
接口不会被当作蜂窝 IMS。实机在 PPP 保持期间返回 `HostRoutable`、
`CanAttemptWindowsIms=true`，两个 P-CSCF 都核验为从该 PPP 接口、以 Windows
IMS 地址为源发出。CLI 的 IPv6 `IPAddress` JSON 序列化崩溃也已修复。
这证明了数据面和路由，不证明运营商 SIP 注册、IPsec 或通话媒体可用。
另用 [定向路由隔离诊断脚本](../../qmi/tools/Test-WindowsImsPppRouteIsolation.ps1)
在活动 PPP 上实测：从当次 `CGCONTRDP=5` 取得两条 P-CSCF IPv6，临时加入
仅这两个目标的 `/128` 路由，移除该 PPP 的 `::/0` 后，两条 P-CSCF 均选中
PPP 接口及 IMS 源地址，且 PPP 不再持有默认路由。脚本 `finally` 恢复原默认
路由并删除两条测试路由，事后只读复查确认。单独运行这个脚本需传入当次读取的
P-CSCF，仅是路由技术验证，尚非自动探测/持有的产品租约；应用侧 Host IMS
探针此前没有在同一个隔离窗口完成复测。随后给
[PPP 交接脚本](../../qmi/tools/Probe-WindowsImsPppHandoff.ps1)加入
`-IntegratedRouteProbe -HoldSeconds 90`：拨号连接期间从**当次**
`CGCONTRDP=5` 自动解析 P-CSCF，核对 Windows PPP 地址与模组 IMS `/64`，
应用仅为这些地址建立 ActiveStore `/128` 路由，删除 PPP `::/0` 并在恢复路由前
运行 VoSharp 只读 `host-ims probe`。实机返回 `HostRoutable`、
`CanAttemptWindowsIms=true`；PPP 后续正常挂断，模组读回 `ims=1,1`，
事后复查无 PPP 接口、测试路由或临时日志。自动探测不再依赖人工输入 P-CSCF，
但该脚本仍只是有界实验流程，尚非应用内可长期持有的 bearer 租约。
RAS 原生探针进一步支持 `--no-ccp --until-stop --max-seconds=N`：必须由父进程
提供 stdin 管道，连接后持续持有 RAS handle，接受 `STOP` 或父端管道关闭，
并轮询掉线及最长租约时限；退出时显式 `RasHangUpW`。交接脚本已改用此模式，
应用只读探针结束就发 `STOP`，不再等待固定 90 秒。实机的正常 `STOP` 与
模拟父端退出（仅关闭 stdin）均导致 RAS 挂断、退出码 0、IMS 恢复 `1,1`，
事后无 PPP 接口或定向路由。它证明了可由进程持有/释放的 Windows 数据链路，
当次试验时尚未把租约对象及 SIM/模组失效联动接入 VoSharp 卡槽。注意模拟实验仅关闭
RAS 子进程的管道，外层交接脚本仍活着并执行 `finally`；当时并未验证
外层脚本异常终止的恢复，不能仅凭此宣称完整崩溃恢复。

此缺口现增加[独立恢复守护](../../qmi/tools/Restore-WindowsImsOnOwnerExit.ps1)：
交接脚本在切换模组前，先记录原模组 15 位 IMEI、启动隐藏守护并等待其确认
已监视当前进程；正常恢复 `ims=1,1` 后以一次性完成标记解除监视。若外层
进程异常退出，守护先确认 COM 口仍是同一 IMEI，仅当模式仍为 `2,0` 或
`1,0` 时才最多两次设置 `ims=1` 并受控重启；未知模式或 IMEI 不符则拒绝
修改。实机在**拨 PPP 前**故意让外层进程跳过 `finally` 退出，独立守护观测到
`ims=2,0` 并恢复到 `1,1`，随后独立 AT 查询确认。正常路径也验证守护可在
恢复后解除监视。当时尚未验证 PPP 活动期间的外层崩溃；守护进程自身丢失、
系统重启后的残留设置恢复仍未验收，产品默认路径继续保留 USB Audio。
随后完成**PPP 活动期间的外层崩溃**实测：在 P-CSCF `/128` 路由生效且
VoSharp 返回 `HostRoutable` 后，让外层进程直接退出而跳过两层 `finally`。
RAS 子进程因控制管道关闭而挂断，PPP 接口和对应测试路由消失；独立守护核对
IMEI、观测到 `ims=2,0`，一次恢复重启后达到 `ims=1,1`，独立 AT 查询再确认。
故意崩溃开关现要求 `VOWIN_IMS_ALLOW_CRASH_PROBE=1` 才可运行。此结果证明
受控流程的进程崩溃回滚，不等于系统断电、守护自身崩溃或长期应用租约已验收。

现另有[托管 PPP bearer 租约](../src/VoSharp.Modem/WindowsHostImsPppBearerLease.cs)：
外层脚本 `-ManagedLease` 在自动 P-CSCF 检测及仅目标路由安装后，输出带接口
索引、本机 IMS IPv6 和 P-CSCF 列表的结构化 READY；保持父进程 stdin 管道，
每 2 秒检查 PPP 及路由隔离，直到 `STOP`、EOF、失联或最长时限。托管层严格
验证 READY，持续读取子进程输出，释放时发 `STOP`，失败时只终止自己持有的
外层进程，让 RAS 管道和 IMEI 守护完成恢复。显式启用的实机测试中，VoSharp
在租约在线时重新探针返回 `HostRoutable`、`CanAttemptWindowsIms=true`；释放
后独立复查无 PPP 接口或目标路由，模组 `ims=1,1`。首次实测暴露 30 秒
RAS 停止窗口过短，延长为有界 90 秒后完整测试通过。管理员会话下再次实测
成功，事后独立复核 `ims=1,1`、无 PPP 接口或临时路由。
现已通过 [ModemSlot](../src/VoSharp.Kernel/Pool/ModemSlot.cs) 显式接入该租约：仅在
空闲、稳定 SIM 上允许启动，关闭 AT 口交给受控脚本，READY 后重新打开并
核对 IMEI、ICCID、IMSI 和 Windows IMS 路由；停止时先结束 Host IMS 会话、
释放 AT 口、停止租约并恢复模组。扫描、切卡和蜂窝 AT 拨号与租约互斥。
CLI 需先为 `VOWIN_HOST_IMS_PPP_OWNER`（`VoSharp.HostIms.exe`）设置完整路径，
再显式执行 `host-ims bearer-start`；`bearer-status` 和 `bearer-stop` 可查看、结束。
WPF 系统状态页现在也提供相同的显式租约启停：脚本和 RAS 探针路径可手动
选择，开发树与临时探针文件存在时可自动填入；每次启用须确认模组 IMS
暂时移交与重启。页面轮询实际租约状态，READY 后重探端点，停止后清除
旧候选；切换模组/SIM 或重探端点也会清空先前的移交与身份外发确认。
该界面接线已编译，但尚未通过 UI 执行真实移交或独立验证恢复。

完整注册前的 AKA 挑战解析现在按 Digest 参数逐项读取：参数名不区分大小写，
允许 `=` 两侧空白、引号内逗号与带空格的 `qop` 列表；重复参数、残缺引号、
非 `AKAv1-MD5` 和仅 `auth-int` 仍拒绝，避免把本可处理的运营商 401 误判
为不支持，也不降级到普通密码 Digest。新增正反例后 VoSharp 全量测试
442/442 通过；这仍是协议级回归，未向当前运营商重发 REGISTER。

后续管理员实机重跑卡槽级 IMS PPP 启停，1/1 通过；独立 QMI IMSA
读回模组内部 IMS 经 WWAN 已注册、语音服务 available，RAS 无连接，
Windows 无残余 PPP/IMS 适配器。PPP owner 已经退出时，停止 Host IMS
现在跳过无承载可用的 BYE/远端注销，避免把本地清理拖进无意义的 SIP
超时；仍在线的 PPP 或非 PPP Windows IMS 承载保持正常注销路径。
总计 VoSharp 443/443 测试通过。Windows 设备列表有 Quectel `AC Interface`
媒体接口。PnP `AudioEndpoint` 类查询未列出它，但 VoWin 实际使用的 NAudio/WinMM
在空闲时读到一个模组麦克风和一个模组扬声器；诊断报告现在可只读复查该结果。
AT + USB PCM 降级代码未移除，真实非零双向音频仍须在受控通话中验证。

为下一次单独获批的完整运营商 REGISTER 实测，卡槽到 SIP 注册器现可注入
仅含阶段、状态码、安全算法类别和耗时的诊断回调；实机门控测试使用该回调，
本地假 P-CSCF 验证了成功响应可见且不会输出 IMPI/IMEI。原始 SIP、nonce、
RAND/AUTN、CK/IK、RES 不进入该回调。此轮未再向运营商发送注册。
卡槽级实机启动、同一模组/SIM 和路由核验、停止回滚已通过（测试附着禁用短信
初始化）；独立复查模组 `ims=1,1`、SIM Ready、无 PPP/目标路由或 RAS 进程。
应用不会自动发 REGISTER、SMS 或呼叫；不能将承载租约实测视作运营商注册
或 Windows 语音可用。
产品化还需：只为
IMS 流量选用 PPP，而非把系统默认 IPv6 流量送到 IMS；为需要 `ipsec-3gpp`
的网络实现真实安全关联。只读 ADB 检查没有找到模组 Linux 侧现成的 IMS
IPv6 接口或可直接转发的数据流，ADB 未用于数据桥接。

只读 [WFP IMS 预检](../../qmi/tools/WfpImsPreflight.cpp) 已在此机编译运行：普通
中等完整性令牌查询被拒（错误 5），管理员令牌可以读取 IPv6 入/出站传输层及
两个系统内置 IPsec transport callout。它不创建过滤器或 SA，也**不能证明**
进程具有 SA 写入权限、手动 SA 能安装或 ESP 包可收发。按
[Microsoft 手动 SA 流程](https://learn.microsoft.com/en-us/windows/win32/fwp/manual-ipsec-sas)，
真实后端还需针对 IMS 地址、P-CSCF、UDP 端口建立过滤器，预留入站 SPI，安装
两个双向 SA 并在失败、注销和换卡时清理。`IPsecSaContextCreate0` 不允许在动态
会话或事务中调用。现已实测把过滤器放在动态会话、SA 放在独立普通会话可行；
关闭动态会话会自动移除过滤器，但 SA 仍需要显式清理及崩溃恢复。预检只确认
系统组件存在，不会启用产品侧 IMS IPsec。

已新增[实验性原生 WFP 预留层](../src/VoSharp.Wfp.Native/README.md)：利用
`IPsecSaContextCreate1` / `IPsecSaContextGetSpi1` 为 UE 的 client/server
两个受保护 UDP 端口分别预留入站 SPI，并提供显式删除 SA context/过滤器的接口。
现已加入按 P-CSCF 端 SPI 和 AKA 派生的算法/密钥安装四条 ESP SA 的原生激活
函数，以及不打开 WFP 的密钥长度/算法守卫测试。Windows SDK 编译通过。
进一步在回环 `::1` 到未配置的 `::2` 上实测：两组 SPI 预留成功，四条 ESP SA
安装成功，显式删除返回成功；SHA1/AES、SHA1/NULL、MD5/NULL、SHA1/3DES
四种组合均通过，逐次只读审计均为残留过滤器 0、SA context 0。没有发包，也
没有触碰模组/运营商。首次使用
`UINT32_MAX` SA 寿命被 WFP 以 `FWP_E_OUT_OF_BOUNDS` 拒绝，改为 3600 秒后
成功，管理层的 SA 计划已同步修正。**蜂窝 PPP 上的加密包仍未验证**；精确远端
端口选择、到期重密钥和进程崩溃后清理仍是接入产品注册按钮的前置条件，不能
把“回环安装成功”写成 IMS IPsec 已通。
双会话回环实验另证实：动态过滤器 ID 可供普通会话的 `IPsecSaContextCreate1`
与 `IPsecSaContextGetSpi1` 使用；关闭动态会话后过滤器审计为 0。原生后端已
切换到这种结构，四组算法再次通过，SA 和过滤器残留均为 0。它只缩小了异常
退出时过滤器残留风险。另一次固定回环故意跳过释放并直接退出进程：退出前
审计到 4 个过滤器、2 个 SA context，新进程审计两者均为 0。这只验证了本机
此场景，不能代替部分创建失败、BFE 异常、蜂窝链路失效时的恢复验收。
现有[托管安全后端适配器](../src/VoSharp.Telephony/WindowsHostImsSecurityBackend.cs)
通过原生 ABI 实现 `IHostImsSecurityBackend` 的“预留 → 协商匹配 → 密钥激活 →
释放”顺序，假原生层测试验证 SPI 配对、密钥临时副本清零及失败清理重试；
进一步在管理员令牌下用实际 DLL 完成托管→原生的 `::1`→`::2` 回环预留、
激活、释放，事后审计仍为残留过滤器 0、SA context 0。它没有注入默认注册
流程，也没有随应用分发原生 DLL；当前进程内待清理句柄
可以重试；回环强制退出实验中动态过滤器及对应 SA 都被系统回收，但生产场景
的启动恢复和链路故障清理仍未实现。

管理员会话下又在**真实 Windows IMS PPP** 上完成一次无发包的 WFP 预留测试：
卡槽持有路由隔离后的 bearer，两个本机 UDP socket 绑定其 IMS IPv6，原生后端
为两个端口分别取得不同入站 SPI，随后释放预留及 PPP。没有调用密钥激活、
没有发出 SIP、UDP、SMS 或呼叫；独立复核模组 `ims=1,1`、SIM Ready、无 PPP
接口/路由、RAS 进程或可见 IPsec SA。这证明 WFP 可附着到真实 IMS 接口，
**不证明 ESP 实包或运营商注册成功**。另外注册流程现先验证 401 的
`Security-Server` 与本机 offer 兼容，才调用卡上 AKA；不兼容的本地测试中
AKA 调用为 0。

继续尝试完全 Windows 原生的 ESP 包级验证时，增加了双端 SA、相同密钥交付与
错误密钥拒绝的回环负控实验。给 Windows 回环接口临时加专用 ULA `/128` 后，
**错误密钥仍收到 UDP 负载**，因此回环收包没有经过预期的 ESP 校验，不能作为
数据面成功证据。测试后匹配的 WFP 过滤器/SA context 都为 0；临时地址已按
`store=active` 精确删除，复查回环接口只剩 `::1`。后续必须在非回环的 Windows
原生链路上完成密钥负控和双向 ESP 包验证，不能拿回环可达性替代。
为消除回环旁路歧义，又使用官方 Wintun DLL 在 Windows 上建立一次性虚拟网卡、
仅含实验地址的 `/128` 本地地址与对端路由。固定在该接口的 UDP 首包在 Wintun
被抓为 IPv6 Next Header 17；安装 SHA1/AES WFP SA 后，第二包被抓为 Next
Header 50，线上的 SPI 是配置值 `0xabcdef02`，测试明文未出现在 ESP 包中。
这是真正的**非回环出站 ESP** 证据，不涉及 WSL、ADB、模组或运营商网络。
结束后独立核对临时网卡、地址、路由及 WFP 过滤器/SA 均无残留。它尚未测试
IMS PPP 上的 ESP、SIP 或媒体，不能把
隔离实验网卡的成功直接推断为 Host VoLTE 可用。
后续在同一临时网卡上把 ESP 包改写成对端来包并重新计算合成密钥的认证标签：
Windows 将正确包解密交付本机 UDP socket；篡改 ICV 的包未交付，随后正确包
仍可交付。然而，**确认接收队列已空后，再次注入完全相同的正确 ESP 包仍再次
交付**。当前手动
WFP SA 实验路径没有通过防重放负控，因此程序返回失败，默认后端继续关闭。
必须先确认能否给该 Windows SA 路径启用可靠的防重放，或在能够看到 ESP
序号的入站层自行实施防重放，才可把它用于真实 IMS；这不是运营商问题。
另一项不发包的原生 WFP 试验把远端 UDP 端口精确加入过滤条件，并尝试在
`IPsecSaContextCreate1` 后跳过 `GetSpi`，直接安装预选入站 SPI；Windows 返回
`0x8032001B`（`FWP_E_INCOMPATIBLE_SA_STATE`），清理后过滤器与 SA context
均为 0。因此“先随机选 SPI、等运营商端口出来再建窄过滤器”的捷径在本机
不可行；必须保留 SPI 预留的状态机，同时设计有效的二阶段端口约束。
用户态备选路线也做了本机能力预检：管理员令牌可以创建 IPv6 `IPPROTO_ESP`
原始套接字，并向 Windows `::1` 发出合成协议 50 数据；同一套接字接收超时
（WSA 10060）。这不能排除非回环网卡可接收，但目前也不能证明可用的原始 ESP
数据面。补做的临时非回环 Wintun 注入实验中，原始 ESP socket 对合成、未认证
的协议 50 包也接收超时；该结果不等于真实 PPP 上所有 ESP 包均不可见。
现有用户态 `ImsIpsecTransport` 已补齐协商后双向远端 UDP 端口校验，相关单元
测试覆盖“SPI/密钥正确但远端端口不符”的拒绝场景；此改进不等于 Windows PPP
已能收发 ESP。
本机已安装的 Npcap 则在临时 Wintun 非回环链路上抓到注入的入站 ESP，并把
原始出站 ESP 送进 Wintun 的发送队列（DLT_RAW，字节匹配）。这证明实验网卡
上存在 Windows 原生用户态抓发入口，尚不能覆盖真实 IMS。为核验可迁移性，
受控切换模组 CID 5 到 Windows PPP 并隔离默认路由后，只读探针按 Windows
PPP 接口索引/GUID 查询 Npcap：**该 PPP GUID 未被枚举**。测试未发 SIP、SMS
或呼叫；随后 PPP 挂断，模组恢复 `ims=1,1`、`usbnet=0`，独立复核无 PPP、
两条临时 P-CSCF 路由及 RAS 探针进程。因此 Npcap/Wintun 成功不能直接
支撑当前 PPP Host IMS；需另行验证可针对 RAS PPP 的 WFP 网络层拦截，或
让 QMAP/其他驱动提供可被用户态包接口绑定的独立 IMS 网卡。
错误端口的认证成功包现会在防重放窗口提交前拒绝；同 SPI、同序号的有效包
随后仍可解开，避免错误选择器污染 SA 的接收状态。

**真实 PPP 的标准工具交叉验证（2026-09-29）**：为判断用户态 ESP 缺的究竟
是密码实现还是 Windows 包入口，使用上游 WinDivert 2.2.2-A 自带的 `netdump.exe`
（`SNIFF` 只复制、不阻断），并用 Windows 自带 `ping.exe` 在路由隔离窗口向当次
自动发现的 P-CSCF 发送不含 SIP/IMSI/IMEI 的 ICMPv6。官方示例在真实 IMS PPP
接口索引 72 上抓到 19 个出站 IPv6 包，源地址正是当次 Windows IMS 地址，
目的地址正是当次 P-CSCF；VoSharp 同时仍报告 `HostRoutable`。这证明 WinDivert
能看到**真实 RAS PPP 的出站包**，而 Npcap 未枚举该 PPP GUID 不再是用户态
ESP 路线的必然阻断。现有 C# `ImsIpsecTransport` 没有被 WinDivert/Wintun 替代；
它负责 ESP 封装、认证、解密和防重放，WinDivert 候选层只负责把原始 IP 包交给
它、再把它生成的包送出。新补的 IPv6 双向 ESP/重放单测通过。**运营商经模组
送达的真实 PPP 入站 ESP、运营商 SA 和 SIP 注册尚未验证**，因此不能据此发运营商 REGISTER 或
宣称 Windows Host IMS 已完成。测试后 PPP、定向路由、netdump 进程和临时
WinDivert 服务均为零残留，模组恢复 `ims=1,1`。
同一官方 DLL 的只读网络层句柄还在一次性 Wintun 实验网卡上，以当次接口索引、
源/目标地址和协议 50 限定过滤器，完整抓到从对端注入的 56 字节入站 IPv6 ESP
合成包（字节精确匹配）。这补足了 WinDivert 入站捕获能力的**非运营商链路**证明；
不证明真实 PPP 的运营商入站 ESP 会到达。进一步用 send-only 句柄发出一枚
合成出站 ESP，Wintun 收到逐字节一致的 56 字节包，证明同一隔离链路的
WinDivert 原始 ESP 注入可用；该隔离试验不验证真实 PPP 的入站 ESP。
实验网卡、地址、路由及临时驱动服务均已清理。
受保护阶段另新增 `HostImsUserspaceEspSession` 适配层：它直接调用现有 C#
`ImsIpsecTransport`，激活前拒绝发送，激活后只把协议 50 原始 IPv6 包交给
包 I/O；入站必须通过认证、端口选择和防重放才还原为 SIP datagram。假包 I/O
单测涵盖禁止明文回退、双向解密、坏认证标签和重复序号，结合原 ESP 单测为
12/12 通过。现在又增加 `WindowsHostImsWinDivertIo`，仅在显式指定 DLL 时
打开限定接口/P-CSCF/协议 50 的 receive-only 消耗型收包句柄和 send-only
发包句柄，发包前重验 Windows 路由；受保护会话已有显式 Windows factory
与它组合。一次性
Wintun 对端的**真实 C# P/Invoke 实验**通过：Wintun 收到 C# 发送的原始
ESP，C# 收到 Wintun 注入的反向原始 ESP，均为逐字节匹配的合成包。首次
实验因原始包入口的最短长度门槛误拒绝 56 字节样本而失败，已修正并重测
通过。随后在真实、仅有 P-CSCF `/128` 路由的 IMS PPP 上，C# 原始包 I/O
发送了**一枚不含 SIP/IMSI/IMEI 的合成无效 ESP**；Windows 自带 `pktmon`
按当次源/目标 IPv6 与协议 50 过滤，独立抓到精确的 16 字节测试标记。
这证明托管发送调用确实进入真实 PPP 的出站路径，**不证明运营商接收或回应，
也不证明运营商经模组送达的入站 ESP 可捕获**。测试后 PPP、临时路由、pktmon 过滤器和
WinDivert 服务均已清理，模组恢复 `ims=1,1`。
最初的托管收包句柄使用 `SNIFF`，会复制入站 ESP 后仍让原包进入没有对应
SA 的 Windows IP 栈。按 [WinDivert 官方语义](https://reqrypt.org/windivert-doc.html)
改为默认截取加 `RECV_ONLY`：C# 独占处理精确匹配的 ESP，避免系统栈对
同一包再作错误响应。修改后，一次性 Wintun 上的 C# 出站和入站逐字节
实验再次通过，独立核验临时网卡、地址和路由归零，实验驱动服务已停止。
**真实 PPP 上的运营商入站 ESP 仍未验收。**
现又在受控 IMS PPP 租约内增加**本机合成入站**实验：WinDivert
send-only 句柄以较高优先级、`Outbound=0` 和当次 PPP 接口索引注入
一枚源为 P-CSCF、目标为 Windows IMS 地址的 56 字节无效 ESP；产品
接收器以较低优先级和精确过滤器取得逐字节一致的包并消耗它。第二枚
入站包由独立的 C# 对端 SA 实例封装，带有效 HMAC/AES 标签和仅含假身份
的 SIP 200；同一真实 PPP 接收器将它交给 `HostImsUserspaceEspSession`，
解密后核对本机/对端保护端口并解析出 SIP 200。实验没有使用 SIM AKA、
真实 IMSI/IMEI 或向运营商发送 SIP/ESP，耗时约 96 秒，测试通过。
结束后独立核对 RAS/PPP/路由清零、模组 `ims=1,1`、`usbnet=0`、
SIM Ready、内部 CID 5 激活，并停止仅指向实验临时目录的 WinDivert
服务。这证明真实 PPP 的 Windows 接口索引和 WinDivert 入站接收路径
可与 C# ESP 解密和 SIP 解析联动，**不证明模组或运营商会实际送来 ESP**。

为避免 C# 发包和 C# 收包的对称错误互相掩盖，新增独立 Python
`cryptography` AES-CBC + 标准库 HMAC 生成的 IPv6 ESP 固定样本，
采用 [RFC 3602](https://www.rfc-editor.org/rfc/rfc3602) 的 ESP 排列和
[3GPP TS 33.203 Annex I](https://www.etsi.org/deliver/etsi_ts/133200_133299/133203/18.01.00_60/ts_133203v180100p.pdf)
的 CK/IK 扩展。VoSharp 可解出指定的 UDP 负载、源/目标端口和校验和，
并拒绝重复序号；这是跨实现的离线互操作证据，不是运营商入站 ESP 验收。
另用 Windows 本机的 OpenSSL 可执行程序直接核对 C# 生成的出站 IPv6 ESP：
HMAC-SHA1-96 ICV、AES-128-CBC 明文 UDP（含校验和）和 RFC 4303 填充均匹配。
测试通过 `VOWIN_TEST_OPENSSL_EXE` 指定该程序的绝对路径启用；未指定时
只运行不依赖外部程序的测试。这进一步验证 C# 密码/封包层，仍不验证运营商
的 SA 选择、真实 PPP 入站或呼叫媒体。

SIP 侧现可在初始明文 REGISTER 的 401/IPsec 协商后，将双受保护端口切换到
上述 C# ESP 会话：普通 UDP socket 被关闭，之后所有 SIP datagram 只经过
用户态 ESP 发送/接收委托，不存在静默回退。隔离的假 P-CSCF 测试覆盖
`Security-Client` SPI 预留、AKA 后 `Security-Verify` 的第二个 REGISTER、
保护端口和 200 OK 回程，确认第二个 REGISTER 未到明文 UDP 端口；
注册后的 INVITE/200 也仍走该受保护委托。这尚不包含真实网络的 RTP 媒体
路由、运营商入站 ESP 或通话音频验收。
另有不使用网卡或 SIM 的合成 IPv6 端到端回归，把 SIP REGISTER 的
401/AKA/受保护第二次 REGISTER/200 与实际 C# ESP 编解码器串在一起，
随后通过同一 ESP 会话完成 INVITE/200；假 P-CSCF 逐包解密并检查
`Security-Verify`。它验证协议层接线与 SA 端口方向，不验证 WinDivert
在真实 PPP 上收到运营商 ESP。
同一假 P-CSCF 回归现会在受保护 REGISTER 200 OK 前模拟一次损坏/未认证
ESP 包，并在 INVITE 200 OK 前重放已接收的包；接收泵均拒绝异常包后继续
处理下一枚有效响应。该注入仅验证 SIP 接收事务的
容错，不等同于真实 WinDivert 入站捕获或运营商 ESP 验证。
续注册重挑战回归另外找到了一个实际生命周期错误：首次注册成功后，
安全预留被移交给会话，但 AKA 激活回调仍引用已清空的局部变量，导致
再次收到 `Security-Server` 时空引用。现改为稳定引用；C# ESP 会话可在
保留 UE SPI/本机保护端口的情况下替换密钥及算法。对端保护端口变化
会在换钥前拒绝，避免旧传输配新 SA；相同端口的新 SA、受保护第二次
REGISTER/200 和后续 INVITE 已由假 P-CSCF 及双端 C# ESP 测试验证。
真实运营商续注册、安全关联换代和对端端口迁移仍未验收。
`WindowsHostImsUserspaceSecurityBackend` 仍是**显式注入的实验后端**。
CLI 的 `host-ims register --userspace-esp` 会选择它，但必须提供
`VOWIN_HOST_IMS_WINDIVERT_DLL` 的完整路径（同目录有 `WinDivert64.sys`），
并在单独批准本次订户身份外发后设置
`VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED=1`；CLI 默认不启用它。
WPF 系统状态页现在可以显式选择 C# ESP、选择 DLL，并为每次注册单独勾选
订户身份外发确认。这个确认在尝试结束后清空；页面不会自动启动 PPP
承载或自动向运营商发包。未选择 C# ESP 时仅能尝试不要求 ipsec-3gpp
的实验网络，仍需同样的身份外发确认。UI 接线和本地回归不代表运营商
完整注册或语音已验收。
此前阶段没有设置该批准开关或向运营商发真实 REGISTER；后续仅执行了
一次经单独授权的初始 REGISTER 探针，不构成运营商可用的 Host IMS 数据面。
Wintun 只作为一次性隔离测试网卡，
不属于生产 IMS 承载。

媒体地址不一定等于 P-CSCF。现有 PPP 租约因此增加 SDP 驱动的受控媒体路由：
Windows 侧呼叫在启用 RTP/RTCP 端点前，向持有 PPP 的进程分别申请目标 IPv6
`/128` 路由；进程只在已隔离、源地址与接口可核验的 PPP 上添加，最多八个，
持续检查并在租约结束时移除。请求失败会拒绝媒体并结束已应答的外拨呼叫，
不会静默改走普通网络。离线模拟呼叫覆盖了路由申请成功及失败后的 ACK/BYE
清理，PowerShell 脚本语法检查通过。后续在管理员令牌下，真实 Windows
IMS PPP 的媒体 `/128` 路由添加、重复申请、精确核验和租约结束清理均通过；
未向文档测试地址发送任何包。独立检查确认模组恢复 `ims=1,1`、
`usbnet=0`，SIM Ready，且无 RAS/PPP/临时路由残留。
不碰模组/路由的假租约进程另验证了 READY、正确 `ROUTE ADD` 回执、重复申请、
错误地址回执拒绝、进程在申请中退出时解除等待，以及 STOP/退出码处理。
多媒体 SDP 假 P-CSCF 测试还覆盖了会话级 `c=`、视频段地址/属性、停用的
`m=audio 0` 与活动音频段自己的 `c=`/`rtpmap`/`rtcp`：只有活动音频的
RTP/RTCP 目标会申请 PPP 媒体路由。此前全局正则可能误选视频或会话地址；
此问题已修复。外拨及本机 re-INVITE 的 SDP 应答现在也必须选择本机 offer
中实际宣告的音频 payload type；本地假 P-CSCF 回送未宣告的 AMR 动态编号时，
客户端会 ACK 后 BYE，且不会为该媒体地址申请 PPP 路由。来电 offer 仍可在
其自身宣告的编码中选择；同一测试的已宣告 AMR 编号仍能接通并建立媒体路由。
真实运营商 SDP 尚未验证。

当前插入卡的只读检查：QMI ISIM reader 不可用；AT 侧能明确判断 ADF.ISIM
缺失，USIM 的归属 PLMN 已权威确定、无 PLMN 冲突，因此代码的 USIM IMS
身份回退具备本地前提。测试输出不包含 IMSI/ICCID/IMPI/IMPU。经单独授权后，
仅获取运营商首个 SIP 响应的初始 REGISTER 探针已执行：首次尝试在发包前
因测试目录缺少 WFP 原生 DLL 而失败；改为由现有 C# ESP 实现生成随机 SPI
提案后，Windows IMS PPP 收到来自所选 P-CSCF 地址且匹配该事务的 SIP 响应。
测试运行器未显示成功用例的脱敏状态码，不能断定它是 401、支持 IMS-AKA
或接受当前 `Security-Server` 算法。探针未执行卡上 AKA、第二次 REGISTER、
完整注册、电话或短信；结束后独立确认模组和 PPP/路由恢复。
探针后续已补齐与产品注册路径一致的 LTE PANI，并在解析挑战头前记录脱敏
SIP 状态码；用只读实机 PANI 用例确认 `--logger 'console;verbosity=detailed'`
可显示成功测试的 xUnit 输出。探针本身未因这些改动重新向运营商发包。

完整 C# ESP 注册的独立门控实机测试已准备，但**尚未执行**。真实运营商
入站 ESP、受保护 SIP 和通话媒体仍无实机验收。此前的 WFP 预留、隔离网卡
双向原始包 I/O 及真实 PPP 单枚合成 ESP 出站抓包也不能替代这些证据。
另一项无发包实机测试现已让 `SipTransport` 在 IMS PPP 本机地址和固定接口上
预绑定初始/双受保护端口，再让 C# 用户态后端打开限定 PPP/P-CSCF 的 WinDivert
ESP 收/发句柄，核对随机 SPI 与实际端口，并证实激活前拒绝发送。测试通过，
同一 PPP 上的 Windows RTP/RTCP 双 socket 也成功绑定 IMS IPv6 并固定接口，
没有设置媒体远端或发包；没有执行 REGISTER、AKA 或 ESP 发包。退出后
独立确认模组、RAS/PPP、临时路由恢复。WinDivert 服务有时在句柄释放后仍
短暂运行；本次核对它仅指向实验临时驱动后正常停止，最终零残留。
它缩小了完整注册的本机入口与媒体绑定不确定性，但没有验证运营商入站 ESP 或 RTP。
显式门控的另一项实机测试已在受控 IMS PPP 上构造 IPv6/UDP/RTP PCMA 静音及 AMR-NB 模式 2 语音包，从 Windows 本机入站方向注入到当次 PPP 接口；绑定 IMS IPv6 和接口索引的 `RtpSession` 通过真实 UDP socket 收到两包，各解码出 160 个 PCM 样本，AMR 样本有非零能量。测试没有向运营商发送 RTP/RTCP、SIP 或订户身份；结束后独立确认模组 `ims=1,1`、`usbnet=0`、CID 5 激活，无 RAS/PPP/临时路由残留。这验证的是本机媒体入站与 Windows 解码路径，不是运营商实包或真实通话验收。
出站方向另有显式门控的真实 PPP 试验：为 `2001:db8::1974` 文档地址临时添加精确媒体 `/128` 路由，先安装限定该目的地和测试端口的 WinDivert 消费过滤器，再令 Windows `RtpSession` 用绑定 IMS IPv6/接口的 UDP socket 产生 PCMA RTP。截获包的源/目的地址、RTP/UDP 端口以及 WinDivert 报告的出站 PPP 接口索引均匹配；测试不回注该包，因此没有运营商媒体发包。最后先停媒体发送器再关闭过滤器，PPP 租约结束后独立复核模组和路由恢复。这仍不是实际运营商 RTP 或双向通话验收。
管理员重启后的另一轮受控卡槽 PPP 启停回归通过；结束后只读 COM6 显示 `ims=1,1`、`usbnet=0`，`rasdial` 无连接。RTP 接收现在先校验并剥离 padding 才计入收包/RTCP 统计，RTCP socket 只接受协商的远端 IP 与端口；本地测试覆盖有效及畸形 padding、伪造来源 Sender Report。VoSharp 445 项测试与 WPF 构建通过。本轮没有再次发送运营商 REGISTER，也未发送 ESP、RTP、电话或短信；完整运营商注册及双向语音依然待实测。
WPF 麦克风路径原先仅搜索 VoWiFi 的活动 RTP 会话，Host IMS 呼叫会遗漏真实上行 PCM。现在桌面录音通过当前 Call-ID/卡槽查找对应 IMS 通话管理器，而不是任意活动 VoWiFi 会话；SIP 网关已独占媒体、错误卡槽或挂断的呼叫不会收到该 PCM。录音设备明确选用非模组的主机麦克风，不再依赖可能指向 AC Interface 的设备 0。假 P-CSCF 的 Host IMS 通话回归已从 Windows RTP socket 收到非静音麦克风样本，证明应用层上行接线；它尚不能证明真实运营商双向语音。USB Audio 回退保持原样。
另审阅媒体发送时序：原先 SDP 远端启用后即启动空闲上行发送器，VoWiFi ESP 回调在其后才安装。现把受保护 RTP/RTCP 发送器与远端端点一起配置，真实 VoWiFi 会话缺少 ESP 或传输时拒绝媒体，不再存在普通 UDP 静默回退；异步发送异常会发布 `rtp.send.error`。本地测试确认受保护委托接收 RTP，普通 UDP 无包，并观察到模拟故障事件。VoSharp 446 项测试和 WPF 构建通过；这仍不等于真实运营商 Host IMS 注册、入站媒体或通话验收。
Host IMS 暂未处理 SIP `MESSAGE`，此前却在 REGISTER Contact 宣告 `+g.3gpp.smsip` 和 `Allow: MESSAGE`，可能误导网络将短信投给会回复 405 的 Windows UA。现仅 Host IMS 关闭该能力宣告，通话 INVITE/应答的 `Allow` 同步移除 `MESSAGE`；VoWiFi 已实现的 IMS 短信保持原有宣告。假 P-CSCF 的 Host 注册/通话测试与构造器双模式测试通过。受控 PPP 期间模组内部 IMS 暂停，即使不宣告 Host SMS，短信仍可能延迟，退出时原模组路径会恢复。447 项测试及 WPF 构建通过；真实运营商是否接受仅语音注册仍未知。
本机贯通回归现在将 401/AKA/受保护二次 REGISTER、`ImsCallManager` 的 INVITE/ACK/BYE，以及 Windows RTP socket 的非静音上行 PCMA 和下行 160 样本 PCM 放在同一个 Host IMS 会话中测试，并确认没有明文 SIP 回退。安全传输后端为测试替身，因此只证明应用层接线，不能代替真实 ESP、运营商注册或双向通话验收。VoSharp 全量 447 项测试通过。
管理员环境中再次运行只读实机预检：当前 UICC 无 ISIM；USIM 临时身份的归属 PLMN 来源可靠且无冲突，ICCID 可读；LTE 驻网小区可生成 FDD 类型 PANI。三项测试通过且不打印订户或小区标识，不执行 AKA、PPP 或 SIP。它支持尝试 USIM fallback 路径，不代表运营商已接受注册。
同一环境里重新执行真实 IMS PPP 的显式门控合成入站 ESP 测试：限定 PPP 接口/P-CSCF 的 WinDivert 接收器收到本机注入的 IPv6 ESP，C# 会话解出预期的测试 SIP 200；未向运营商发送 SIP 或媒体。测试后独立核验无 RAS、PPP 接口或路由残留，模组 `ims=1,1`、`usbnet=0`、SIM Ready、CID 5 激活。该结果仍不证明运营商真实入站 ESP 或完整注册。
PPP 停止流程现在即使 SIP 注销/本地清理抛错，也继续停止 owner、释放租约并恢复模组内部 IMS，同时汇总并报告各阶段错误。故障注入测试把注册清理设为抛错，验证假 PPP owner 仍收到 STOP，承载状态清空；VoSharp 448 项测试和 WPF 构建通过。真实模组异常恢复仍需单独验收。
续注册重试耗尽后，`RegistrationFailed` 现在异步关闭失效的 Host IMS 会话，并在有 PPP owner 时停止租约、尝试恢复模组内部 IMS；从刷新任务之外调度，以免停止过程等待自身任务而死锁。回退前核对失败会话身份，旧会话迟到的失败事件不能停止新会话。假 owner 测试覆盖自动回退与陈旧事件保护，VoSharp 450 项测试和 WPF 构建通过；运营商真实续注册失败场景未实机验收。
刷新任务在 `RegisterWithCardAsync` 返回前已启动；卡槽在发布客户端后现在会再次检查注册仍有效，若极短的回调安装窗口内已失效，则报错并排队回退 PPP，而不是返回虚假的“已注册”。这条竞态防护经过全量回归，但尚无实机时间窗口复现。
管理员令牌下又完成一轮只读 QMI 控制面/读卡预检：自动发现的 Quectel 私有控制端点可申请两个独立 WDS CID，WDA 回报 RawIP/QMAP v1；模组内 IMS 已注册，但 Windows 仅有一个断开的物理 WWAN 接口，不提供 Additional PDP。因此 QMI 控制通道可用不等于 Host IMS 独立数据流已映射。UIM 报告物理槽 1 为主卡、槽 2 为空；QMI APDU 映射现接受明确空置的备用槽，拒绝第二张卡、活动备用槽或截断槽响应。读卡/AKA 入口还强制核对 QMI 与 AT 的 IMEI，避免把挑战发给未经配对的模组。实机 QMI USIM 逻辑通道预检在该核验后通过；未执行 AKA、重新发送 REGISTER 或改变模组配置。VoSharp 454 项、qmiSharp 62 项测试及 WPF 构建通过。
媒体路由又补齐非 PPP 路径：若 Host IMS 注册没有 PPP owner 提供的定向媒体路由租约，通话在启用每个 RTP/RTCP 目标前会重新检查 Windows 最佳路由，要求目的地仍使用注册时的 IMS 接口索引和 IMS 源地址；此前无 provisioner 时直接放行的行为已移除。假路由回归确认不能把虚构的 Host IMS 探针当作媒体路由授权。它为将来的 QMI/MBIM 映射接口保留可行路径，但目前并不证明此模组已映射独立 QMAP 数据流。
管理员令牌下两次执行同一真实 IMS PPP 的不发包联合预检：卡槽在受控交接和恢复守护存活时，QMI/AT IMEI 配对仍有效、QMI USIM 逻辑通道可预检，Windows RTP/RTCP socket 绑定 IMS IPv6/接口，C# ESP 后端预留双端口和随机 SPI；第二次还直接读卡上 EF.ICCID，确认与选中 SIM 一致，断言和输出均不泄露卡号。未执行 AKA、SIP REGISTER、ESP/RTP 外发、电话或短信。每次测试后独立核对模组 `ims=1,1`、`usbnet=0`、SIM Ready、内部 CID 5 活动，且无 RAS 进程/连接、PPP 网卡或路由残留。这证明这些本地前提可在同一 PPP 租约内并存，不证明运营商会接受完整注册或送来真实语音包。
注册前的 `Security-Server` 候选解析现在拒绝超出 0–1 范围或非有限数的 `q` 值，以及重复/畸形的参数，不再让后出现的 SPI、端口或算法静默覆盖先出现的值。假 P-CSCF 回归确认这些候选在 SIM AKA 前被拒绝；正常多机制优先级选择不变。VoSharp 全量 464 项和 WPF 构建通过。此防护不代表当前运营商实际使用了这些异常头字段，也不替代真实注册测试。
注销阶段若 P-CSCF 对 `Expires: 0` REGISTER 再发 401 并附新的 `Security-Server`，现会先核验该提案确实已被 UE 宣告且可安装，再调用卡上 AKA；兼容的新 SA 会替换旧 SA 并用于受保护的二次注销 REGISTER。不兼容提案在第二次 AKA 前拒绝，避免用旧 SA 假称远端已注销。假 P-CSCF 两种分支的回归通过，VoSharp 全量 466 项及 WPF 构建通过；真实运营商注销换钥仍待实测。
PPP bearer 不再把 3600 秒当作正常通话的绝对终点：C# owner 每隔不超过 300 秒要求续租，PowerShell 转发 `RENEW` 给 RAS 并回 ACK；各层在没有新心跳时仍保留最多 3600 秒的失联上限。PPP/无默认路由/P-CSCF 及媒体精确路由由原有 2 秒巡检持续核对，续租分支不再重复运行会阻塞 ACK 的 `Find-NetRoute`。假 owner 覆盖心跳和进程中途退出，真实模组以 60 秒初始租约保持超过 75 秒、确认至少四次续租后正常停用；事后独立核验内部 IMS、CID 5 与 Windows RAS/PPP/路由恢复。VoSharp 469 项测试及 WPF 构建通过。长期通话和系统断电后的恢复仍未实测。
交付侧现在用 `Build-WindowsImsPppPackage.ps1` 在 `win-x64` publish 时从仓库的 `RasImsPppProbe.cpp` 编译原生 helper，并把它与交接、路由、恢复三份脚本放在发布目录 `host-ims`；需要 Visual Studio x64 C++ 工具，但普通 `dotnet build` 不需要。独立的新目录 `dotnet publish` 已核验生成 `VoWin.exe` 和四个配套文件，发布包中的 RAS helper 的 `--check-modem` 找到当前 Quectel 设备，脚本语法和源码 SHA-256 对照通过。WPF UI 优先解析随包路径，脚本手动运行也优先使用同目录 helper；源码树仍可回退到临时 helper。单独运行路由诊断时，开发 CLI 若存在就参与探针，不存在则只验证 PPP 与路由；显式传入错误 CLI 路径仍在修改模组前报错。管理员实机测试将发布包脚本和 helper 指向卡槽级 `StartHostImsPppBearerAsync`，完成一次真实 IMS PPP 启停，事后独立确认模组 `ims=1,1`、`usbnet=0`、SIM Ready、CID 5 激活，Windows 无 RAS 连接、PPP 网卡或路由残留。发布版 UI 本身尚未交互测试，也未做运营商注册或通话验收。WinDivert 仍是外部显式选择的原始包入口，不会由发布步骤暗装。
随后在最终发布包 helper 的同一次真实 PPP 租约内，又通过显式门控的无发包联合预检：QMI/AT 模组配对及 USIM 逻辑通道、EF.ICCID 一致性、Windows RTP/RTCP socket 绑定、双受保护 SIP 端口与 C# ESP/WinDivert 预留全部成立；预留激活前发送被拒绝。使用的实验 WinDivert 驱动签名有效。测试不执行 AKA、REGISTER、ESP/RTP 外发、电话或短信；结束后独立核对模组 IMS/usbnet/SIM/CID 5、RAS、PPP 网卡及路由均恢复。这比单独打包检查更接近完整注册前提，但仍不证明运营商入站 ESP 或真实通话。
呼叫对话路由也按 [RFC 3261 §12.1](https://www.rfc-editor.org/rfc/rfc3261#section-12.1) 修正：外拨 INVITE 可使用 REGISTER 的 `Service-Route`，但最终 200 OK 未给 `Record-Route` 时，ACK/BYE 的对话路由集为空，不再继承 INVITE 的 Route；来电 INVITE 的多个 `Record-Route` 则按请求顺序保存，而不是倒序。假 P-CSCF 用例验证外拨无记录路由和来电双级路由，VoSharp 全量 469 项测试与 WPF 构建通过。这是协议正确性修复，尚未由运营商实网 INVITE/来电确认。
被叫侧响应也补齐同一规则：100/180/200 使用请求的完整 Via 链，建立早期/确认对话的 180/200 按原值、原顺序回显 INVITE 的 `Record-Route`，避免只保留第一条 Via 或让代理失去记录路由。假 P-CSCF 用两条 Via、两条 `Record-Route` 验证所有响应及随后 BYE 的路由；VoSharp 469 项测试、WPF 构建均通过。该用例不替代运营商真实来电验证。
首次 Host IMS 注册失败现在也自动释放当次 PPP owner：注册锁释放后才进行卡槽回退，并以 owner 引用和注册尝试代际核对，避免旧失败异步停掉新会话；回退失败会连同原注册错误一起报告。假 owner 用例覆盖停止及陈旧尝试保护。另一次管理员实机负测在真实 IMS PPP 上故意提供错误端点，注册在 AKA/SIP 发包前失败，卡槽自动 STOP；事后独立读取模组 `ims=1,1`、`usbnet=0`、SIM Ready、CID 5 活动，Windows 无 RAS 连接、PPP 网卡或路由残留。这验证了该类失败能恢复 USB 音频回退前提，但尚未模拟运营商 401/ESP 后失败或真实通话。
系统状态页的注册失败处理现在立即刷新 PPP/注册状态；如果当次 PPP 已自动退出，就清除旧 HostRoutable 探针候选和身份外发勾选，避免用失效端点再次尝试。失败状态文案同时覆盖首次注册与刷新失败，不再一律写成“注册刷新失败”。WPF 构建通过，尚未对发布版 UI 做交互实测。
另修复用户态 ESP 会话的算法激活门槛：`Security-Client` 默认宣告
SHA-1/MD5 × AES/3DES/NULL 六组组合；此前只接受与默认 SHA-1/AES 提案
完全相等的选择，即便其他组合已被宣告也会在 AKA 后失败。现在允许受支持的
已宣告算法组合，但严格保持 UE 预留 SPI 和两个保护端口不变。六组双向
ESP/错误 SPI 与端口的离线测试通过；六组 C# 出站包还用独立 OpenSSL 程序
核对 HMAC、AES/3DES/NULL 负载与填充。真实运营商算法选择仍未知。

2026-09-30 经用户明确授权完成当前 SIM 的真实运营商 Host IMS 注册验证。第一轮
收到初始 REGISTER 的 401、AKAv1-MD5 挑战和可兼容的 `Security-Server`；QMI USIM
AKA 返回 RES/CK/IK，协商使用 ESP `hmac-sha-1-96`/`null`，但受保护第二次
REGISTER 未获回应。按 [3GPP TS 33.203 §7.1](https://www.etsi.org/deliver/etsi_ts/133200_133299/133203/19.01.00_60/ts_133203v190100p.pdf)
区分出站 UDP 客户端端口与接收响应的服务端端口，将受保护 REGISTER 的 `Via`
从前者改为后者后，第二轮门控实测在约 352 ms 收到受保护的 200 OK，注册期限
3600 秒，卡路径为 QMI USIM。后续交互 CLI 在同一 PPP/P-CSCF 路径再次收到
200 OK，持续监听约七分钟，状态仍为 `registered`，但期间没有外部 INVITE；
因此真实来电、RTP 与双向语音仍未测试。没有发送短信或主动拨号。两轮测试均已
停止 PPP；交互轮读回模组 `ims=1,1`，Windows RAS 无连接。

交互轮注销收到 200 OK，但响应回显原 Contact，旧逻辑将其视为仍绑定并报告
`RemoteDeregistered=false`；不能据此断言远端仍有有效绑定，也不能反称注销已
确认。现在只有回显的 Contact 明确 `expires=0`（或响应统一 `Expires: 0`）时才
视为删除；回显正有效期仍报错，本地两种用例已覆盖。实际运营商注销响应的
到期字段尚需下次实网核验。注册接收泵的前八个错误现可通过脱敏诊断回调看到，
便于区分无入站 ESP 与 ESP/UDP 解析失败。

PPP 路径现已有实验性卡槽生命周期，处理路由隔离和内部 IMS 回滚；还需
在该承载上验证运营商来电的受保护 SIP 和媒体，并解决 WFP 隔离试验发现的入站重复包交付（用户态 C# ESP 已有防重放），再验证安全关联续期与异常退出恢复。并行研究 QMAP mux
对应的独立 Windows 数据接口，以争取 Internet/IMS 双 PDN 与更高吞吐。若继续
追查 WWAN 主 PDP 失败，应同步抓取 `SET ContextState`、驱动内部 WDS
StartNetwork/RuntimeSettings 和 NDIS IP/媒体状态，查明短暂 `Activated` 为何被
撤销。**不要仅把驱动上报的 `MaxActivatedContexts` 从 1 改大**：
同系源码还用单个 `WWanServiceConnectHandle` / `DeviceContextState` 跟踪连接，
只改能力值不会自动得到第二个可路由的 IMS 网卡，反而可能破坏现有网络连接。

2026-09-30 使用无线 ADB 对同一手机发起真实主叫，选卡弹窗中明确选择卡 1（卡槽 0、订阅 ID 2），拨打模组号码。Windows Host IMS 多次收到受保护的 INVITE，并回 100/180；手机侧进入振铃提示。Windows 发出含 AMR SDP 的 200 OK（实测 SIP 长度约 1020–1155 字节，小于 1432 字节 PPP MTU），但未收到 2xx ACK；手机 Telecom 始终为 `DIALING`，约 35–40 秒后网络以 480/远端释放结束。保留注册 Contact 及 MMTel 特征标签的 200 OK 再试仍未接通，因此选卡、Contact 缺失和 MTU 均不能单独解释失败。脱敏元数据确认实网 INVITE 的 SDP 含 QoS precondition；现有终端没有完成 183/PRACK/UPDATE 与资源状态协商，按 [RFC 3312](https://www.rfc-editor.org/rfc/rfc3312) 这是下一步重点，但尚未证明为唯一根因。当前 `AnswerAsync` 在发送 200 后就标本地 `Active`，不等于网络确认；每次失败后须清除该残留呼叫，避免后续主叫听到忙线。测试未发短信，尚无双向 RTP/语音证据。

同时修复手工构造的拒接、忙线、CANCEL 和 BYE 响应丢失请求事务上下文的问题，改为从原 SIP 请求生成响应，以保持受保护端口与 Via 链。来电 180/200 的 Contact 也改用当前注册的 UE Contact（去掉 REGISTER 专用的 `expires`），这一互操作调整在上述实网复测中未单独解决接通问题。

后续同日诊断版重新取得 Windows PPP 与 `REGISTER 200 OK`，再次由手机卡 1 主叫。收到的真实 INVITE 是约 1964 字节，`Supported` 含 `precondition,100rel`，QoS SDP 精确为 `curr local none`、`curr remote none`、`des mandatory local sendrecv`、`des optional remote sendrecv`。这里的 `local` 是**主叫方**的本地段；在 Windows 回答中应反转为 `remote`，不能误以为只需 Windows 本地声明已预留。当前实现直接回 180，之后回 200，仍未收到 ACK。按 [RFC 3312 §5.2、§6](https://www.rfc-editor.org/info/rfc3312/)，回答方必须保留 mandatory 强度、反转方向，并在条件满足前暂停提醒/建立；下一步是可靠 183 携带 SDP 回答、PRACK/UPDATE 状态交换，以及从实际蜂窝承载确认资源，而不是虚报 `curr:qos ... sendrecv`。这是有力的失败原因线索，尚未证明为唯一根因。

本地状态机已改为发送 200 后等待匹配的 2xx ACK 才发布 connected/Active；无 ACK 的未建立来电约 35 秒后自动清除，防止假忙线。定向来电、Host IMS 和 VoWiFi 硬化测试 51 项通过；真实双向 RTP/语音仍未验证。

随后按上次真实 SDP 增加了**待实网验证**的早期前提条件流程：当 INVITE 的主叫本地段有未满足的 `mandatory local sendrecv` 且支持 `100rel` 时，Windows 在 100 后先发送带 SDP 回答、`Require: 100rel` 和 `RSeq` 的 183；按 [RFC 3312 §11](https://www.rfc-editor.org/rfc/rfc3312#section-11) 同时声明 `Supported: precondition, 100rel` 与 `Allow: UPDATE`。SDP 正确把主叫的 `local` 翻转为 Windows 视角的 `remote`，并请求状态确认。183 依据 INVITE 实际选择可用 AMR/PCM 编码，媒体收发保持关闭。传输层重发 183 直到已接受 PRACK；PRACK 自带 SDP 或后续 UPDATE 明确报告主叫 `curr:qos local sendrecv` 后才发 180，接听的最终 200 不再重复初始 SDP 回答，匹配 ACK 后才启用 RTP。未满足条件或无人确认会超时结束，Windows 本端无实证的资源状态保持 `none`，不虚报资源已预留。除呼叫管理器和传输层单测，还在 Host IMS 客户端／已绑定承载 RTP 的贯通回归中验证了 100→183→PRACK→UPDATE→180→200→ACK→BYE；VoSharp 全量 484 项通过。但运营商是否接受该信令、是否下发专用语音承载、最终 ACK 和双向 RTP 仍须实测。

同日受控升级至包含上述改动的新版 CLI：旧会话先取得运营商注销 200，再释放 PPP；只读探针确认模组内部 `ims` 已恢复且 CID 5 活动。新进程重新接管 PPP，按卡槽租约核验两个 P-CSCF 的 Windows 路由；在**未手工填写** CID、本机地址或 P-CSCF 的情况下，自动选首个候选并取得受保护运营商 REGISTER 200 OK，卡路径为 QMI USIM。此轮在线会话尚未收到新的真实来电，故新的 QoS 流程仍只是本地回归，不可声称接通或双向语音已验收。

诊断时还发现 CLI 原先没有采集电脑麦克风，RTP 只会在无 PCM 时发送舒适静音；用它接通后让手机侧听声音，不能直接验证上行语音。现增加仅供 Windows CLI 的显式 `mic list|start [设备号]|status|stop`：必须在收到 ACK 且当前卡槽 Host IMS 通话处于 Active 后手动 `mic start`，自动排除模组的 `AC Interface`，按当前 Call-ID/卡槽把 8 kHz 单声道 PCM 送给既有 RTP 路由；通话结束自动停止采集。空闲时启动会拒绝，默认不采集。本机诊断版只读枚举到模组输入与 Realtek 麦克风，空闲拒绝已实测；重新受控接管 PPP 后再次取得运营商 `REGISTER 200 OK`，但尚未真实接通，因此麦克风到运营商 RTP 的路径仍未验收。

另在不建立电话、不保存录音的本机短测中，Realtek 输入的 WinMM `WaveInEvent` 成功打开、停止，并交付了非零数量的 PCM 回调；此证据只覆盖本机采集 API，不覆盖 CLI 的 Call-ID 路由、运营商上行 RTP 或远端听感。

为消除聊天与 CLI 手工接听的时序误差，诊断 CLI 新增 `answer-next <主叫号码> [--mic]`、`answer-next status|off`。只有当前卡槽已完成 Host IMS 注册且没有活动通话才允许布防；来电必须属于同一卡槽、处于 Ringing、Call-ID 一致且主叫号码精确匹配，才会消耗一次性布防并发送接听。布防 10 分钟后过期，未匹配号码不会自动接听；可选 `--mic` 只在匹配 ACK 之后的 CallConnected 事件触发电脑麦克风，结束时停止。它不能绕过 QoS 的 183/PRACK/UPDATE：应用只有在发送 180 后才产生可接听事件。改动后的 VoSharp 490 项测试与 CLI 构建通过；实机已受控注销旧注册、停止 PPP、确认模组 `ims=1,1`，再接管 PPP 并收到新的受保护运营商 `REGISTER 200 OK`。截至这次布防，仍没有新来电验证最终 ACK 或双向语音。

当次布防窗口到期后，CLI 报告一次性接听已关闭、Host IMS 仍为 `registered`；在线进程在窗口内没有输出新的 INVITE。PPP 的 P-CSCF 精确路由再次核验通过，Windows 默认播放设备只读查询为 Realtek 扬声器，不是模组 AC Interface；手机无线 ADB 设备列表仍未出现手机。这一轮没有外部主叫，不能据此判断新版 QoS/ACK 流程是否被运营商接受，也不能声称语音已接通。

为下次已接通但无声的实网排障，源码加入只读 `host-ims media-status`：按当前 Host IMS 媒体会话分别统计 RTP 发送任务成功、合法源且协商载荷的接收包、解码成 PCM 的帧数和发送异常；麦克风状态另显示采集回调数与通过 Call-ID/卡槽校验的交付数。这样可区分 Windows 接收、解码、麦克风和上行发送各段，但 `sent` 不证明空口或对端收到，且包含舒适静音包。定向媒体统计测试和全方案 607 项测试通过，CLI 构建通过。当前在线进程仍是上一版，统计入口须在下次受控注销/重启后才可用于实机；没有为部署诊断功能中断现有注册。

续注册调度补上本地回环端到端回归：假 P-CSCF 首次只授予 1 秒，客户端按 80% 时点发送续 REGISTER，保持 Call-ID、递增 CSeq，并采纳新的 3600 秒有效期；全方案 608 项测试通过。在线运营商会话从本次注册起仍保持 PPP/路由和 `registered`，但尚未到其约 48 分钟的首次计划续注册时点；真实 401/AKA/ESP 续期与长时间承载稳定性继续待证实。

随后发现真实上行语音风险：`WaveInEvent` 的 `DataAvailable` 缓冲长度可变，而旧 `RtpSession.SendAudioPcm` 每次把整个缓冲交给只读取 160 采样的 AMR 编码器；缓冲大于 160 时丢掉后续样本，不足 160 时本次不发语音。现在统一跨回调拼装 160 采样/20 ms 帧，PCMA/PCMU 也按 SDP `ptime:20` 发送；每帧序号与时间戳在麦克风和舒适静音并发时串行分配，媒体重新配置会舍弃旧配置留下的半帧。变量长度 PCMA/AMR 测试核对帧数、内容和 RTP 时间戳；全方案 610 项测试与 WPF 构建通过。这是源码修复，在线进程仍运行上一版以观察首次运营商续注册，因此不能声称实网语音问题已解决。

同日首次真实续注册已观测成功：原会话由 05:25:42 UTC 的 `RegisteredAt`/06:25:42 UTC 的到期时间，按 80% 时点在 06:13:42 UTC 自动更新为新的 `RegisteredAt`/07:13:42 UTC 到期时间；卡槽报告 `registered`，PPP 租约和 P-CSCF 精确路由再次核验有效。这证明至少一次真实运营商续期已成功，不能推断后续无限期稳定或通话成立。随后在无活动通话时解除一次性自动接听，收到运营商注销 200，停止 PPP 并只读确认模组 `ims=1,1`。部署含媒体统计与 PCM 分帧的新版 CLI 后，自动发现新的 CID 5 PPP 地址与 P-CSCF，于 06:18:37 UTC 再次收到受保护 REGISTER 200；`host-ims media-status` 空闲时正确报告无活动媒体，PPP 路由核验通过。新版已为指定主叫短时布防，但截至部署后仍没有实网来电；音频效果不可据本地测试认定已修好。

### 2026-09-30 实网双向语音接通

手机卡 1 无线 ADB 重连后，带新 QoS 流程的首通实测收到 INVITE，并发出可靠 183，但没有收到 PRACK；网络只重传 INVITE，35 秒后本端回 580。排查发现 `SipTransport` 对受保护来电响应沿用收到请求的反向 UDP 端口对：UE 保护服务端口 → P-CSCF 保护客户端口。[3GPP TS 33.203](https://www.etsi.org/deliver/etsi_ts/133200_133299/133203/19.01.00_60/ts_133203v190100p.pdf) 对 UDP 明确规定 UE 的请求和响应都应从其保护客户端口发向 P-CSCF 保护服务端口；修正这一出站路由，同时保持来电仍在 UE 服务端口接收。传输层原有可靠 1xx 重传，移除了呼叫管理器新增的重复重传。另为 `Require: 100rel` 的 INVITE 补上可靠 180、连续 RSeq 与 PRACK 处理；当前运营商 INVITE 是 `Supported: 100rel` 而非 `Require`，后者是协议兼容修复，不是本次故障根因。

受控注销、释放 PPP、恢复模组 IMS、重新部署后，Windows 再次自动取得 IMS PPP/P-CSCF 并完成 QMI USIM AKA、受保护 REGISTER 200。之后由手机卡 1 连续两次主叫模组：Windows 两次均在实网观察到 INVITE→100→183→PRACK→200(PRACK)→UPDATE→200(UPDATE)→180→200(INVITE)→ACK，通话进入 Active。第一通 Windows RTP 发送至少 1456 包、收到并解码 507 包，Realtek 麦克风采集/交付计数持续增长；用户明确确认手机已接通且双向都能听到。第一通由手机挂断，Windows 响应其 BYE。第二通未重注册即再次接通，Windows RTP 发送 521 包、收到并解码 262 包，发送错误 0，麦克风 521 个缓冲全部交付；由 Windows 挂断，手机 Telecom 记录 `REMOTE` 正常断开。通话结束后 Host IMS 仍为 `registered`，P-CSCF 精确路由检查通过。没有发短信。修复后的 VoSharp 全量 499 项测试通过。

前两通证明当前 Windows/模组/SIM/运营商组合的 Host IMS 注册、实网来电、双向 AMR 语音以及双向挂断可行；当时尚未复测续注册后的通话。其他网络互通、专用 QoS 承载、WPF 操作路径、短信或 Internet+IMS QMAP 双 PDN 并存也未因此得到验证。CLI 自动保存了本机通话录音；文件含真实语音，不应提交到仓库或公开日志。

同一 Windows PPP/Host IMS 会话经历两次通话后，于 08:01:22 UTC 自动完成运营商 REGISTER 续期，获得新的 3600 秒有效期，未重启 CLI 或模组。手机卡 1 随后再次主叫：真实 SIP 完成 INVITE→100→183→PRACK→UPDATE→180→200→ACK，Windows 与手机 Telecom 均显示接通；AMR RTP 已发送 570 包、收到并解码 203 包、发送错误 0，Realtek 麦克风 570 个缓冲均交付，用户再次确认双向可听。约 44 秒后 Windows 主动挂断，媒体回到空闲，一次性自动接听关闭，Host IMS 仍为 `registered`，原 P-CSCF /128 路由仍在 PPP 接口 65。于是当前组合的“自动续期后仍可来电并双向传声”也得到实证，但单次续期和三通电话不足以证明长期稳定、漫游或其他设备/运营商互通。

长时耐久审查发现，旧租约会将每通电话的 RTP/RTCP /128 路由保留至整个 PPP 结束；当不同媒体目的地累计到八个时，即使旧通话早已结束，新通话仍会被上限拒绝。源码现在由 RTP 会话结束触发租约私有 `ROUTE REMOVE`，仅移除守护进程记录为本租约媒体的路由；P-CSCF 路由不在删除集合。删除必须收到同地址确认，确认异常时结束受控租约并触发独立恢复守护。伪租约连续十二个不同媒体地址的增删、坏确认故障、以及呼叫挂断触发清理的回归已通过；下面记录随后完成的真实 PPP 验证。

原会话在经历三次来电后，于 08:49:23 UTC 完成连续第二次运营商自动 REGISTER 续期。随后在无活动通话时正常注销获 200、释放 PPP，独立读回模组 `ims=1,1`；新版 CLI 重新自动取得 CID 5 PPP/P-CSCF、受保护 REGISTER 200。手机卡 1 在新版上连续两次来电均完成接通，媒体目的地址加入时 `MediaRouteCount=1`，Windows 正常挂断后清为 0；外部 Windows 路由表确认没有媒体 /128 残留，两条 P-CSCF /128 仍在，PPP 无 IPv6 默认路由，Host IMS 仍 `registered`。第二通用户确认从接通起双向可听；首通用户先报告电脑听不到手机，随后说明电脑播放音量太低，调高后双向恢复，因此该听感不构成网络下行完全中断的证据。截至 09:12 UTC，新版租约心跳已返回三次续租确认，媒体路由保持 0，Host IMS 仍注册。当前只实测正常挂断后的回收；异常进程退出、路由命令失败和多日耐久仍需进一步验证。

为区分“RTP 已解码但播放设备不可用”和 Windows 混音器音量过低，源码现提供 WinMM `waveOutOpen` 返回值、当前通话的 PCM 帧排队/写入/丢弃及写入错误计数，在 CLI `host-ims media-status` 与 WPF Host IMS 媒体状态显示。这些计数不能测量扬声器实际音量或代替人的双向听感确认；诊断已部署到新版 CLI，但尚未用真实来电验证新计数。

重复通话的录音路径另修复了一个竞态：挂断时先从共用 Windows 播放设备取走该通 PCM 快照，再异步写 WAV/AMR；下一通开始清空播放队列或录音缓存时，不会清掉上一通尚未写盘的 PCM。关闭录音时不再累积下行 PCM/AMR 帧；AMR 录音序号改为跨 16 位回绕扩展，避免约 22 分钟后覆盖早期帧。对应单元回归及 VoSharp 全量 503 项测试、CLI 与 WPF Release 构建通过；真实连续通话仍需新版实测。

该旧在线会话随后于 09:43:05 UTC 再次自动收到运营商 REGISTER 200，新的到期时间为 10:43:05 UTC；同期 PPP 已收到 9 次心跳确认，媒体路由为 0，两条 P-CSCF /128 保留、无默认路由。无活动通话后执行正常注销获 200、停止 PPP，独立确认无 RAS 连接/旧路由/旧 CLI 进程，AT 读回 `ims=1,1`。部署含上述播放诊断和录音修复的新版 CLI 后，Windows 自动取得新的 CID 5 IPv6/P-CSCF，并于 09:48:11 UTC 再获受保护 REGISTER 200；PPP 路由控制核验通过。第一次新版实网拨号尝试被主叫手机 PIN 锁屏拦住，Windows 没有收到 INVITE，不计入通话验证。

### 2026-10-08 整机重启后的 IMS 恢复

Windows 后来整机重启。重新检查时无 VoSharp CLI、RAS 连接或 IMS 路由，但 AT 报告模组 `ims=2,0` 且 CID 5 未激活；上次交接的进程守护日志只有 `WATCHING`，与它随操作系统重启一起消失一致。先在无活动 PPP/通话时执行 `AT+QCFG="ims",1` 和 `AT+CFUN=1,1`，只读确认 `ims=1,1`、SIM Ready、CID 5 活动。这证明旧恢复设计未覆盖整机重启，不能只用“Windows 路由已清理”判定模组已恢复。

交接脚本现于修改模组前安装受 ACL 限制的 SYSTEM 开机任务，脚本复制到 ProgramData 的管理员/SYSTEM 专用目录。任务绑定当次模组 IMEI 的 SHA-256、AT 口和原 owner PID/启动时间；如果 owner 仍在运行、PPP 仍在线、IMEI 不匹配或 IMS 状态不在 `2,0`/`1,0`，不会改模组。正常恢复 `ims=1,1` 后撤销任务；意外整机重启后由任务自行恢复并撤销。实测短时 PPP 交接完成了任务注册、owner 在线时跳过、正常恢复后撤销；另在无 PPP/通话时模拟 `ims=2,0` 并手动触发 SYSTEM 任务，日志证明身份核对、恢复重启、`ims=1,1` 确认与任务自撤销，独立 AT 还确认 CID 5 重新激活。错误 IMEI 哈希测试被拒绝。发布脚本包已包含这一路径。仍缺一轮真正的 Windows 整机重启触发验收；手动触发不能替代这一证据。

### 2026-10-08 外拨、双向媒体与短信基线

Windows 重启恢复脚本加入后，新的管理员 CLI 再次自动取得真实 CID 5 PPP/P-CSCF，并以 C# 用户态 ESP 收到运营商受保护 REGISTER 200。一次旧版外拨收到两次 `180 Ringing`，但 INVITE 的固定 30 秒等待到期后清除了呼叫，随后到来的 UPDATE 被回 481。这暴露了外拨早期对话的本端问题，不能把 180 误报成接通。源码现将首次响应超时与临时响应后的最长等待分开（后者 180 秒），保存早期 To/Contact/Record-Route 与 SDP 回答，并处理匹配外拨早期对话的 UPDATE；CLI 外拨不再阻塞同一控制台的挂断命令。超时若已收到临时响应，会尝试发 CANCEL，而不是只清理本地状态；这最后一处超时清理补丁是在实网通话之后加入，仅完成本地测试，极端迟到 200 的竞态仍需实网验证。若早期 INVITE offer 尚未被 SDP 回答，带 offer 的 UPDATE 会回 491，避免错误接受重叠 offer。本地回归覆盖延长等待、早期 UPDATE 与无 SDP 的最终 200；全量 505 项通过。此轮真实网络没有观察到早期 UPDATE，相关分支仍需实网验证。

新版在同一 Windows IMS 会话里向用户授权号码连续两次外拨，均收到 `180`、最终 `200 OK` 并发送 ACK；用户确认两通均由手机人工接听。第一通未启用电脑麦克风，因此用户听不到电脑端声音，不能作为双向失败或成功证据。第二通接通后开启 Realtek 麦克风，Windows 媒体状态看到 AMR 收包/解码、麦克风缓冲上行及发送错误 0；用户确认双方都能听到。正常挂断后媒体路由数回 0、麦克风关闭；Windows 主动注销获 200，PPP 停止后独立 AT 读回模组 `ims=1,1`、内部 CID 5 活动，RAS 无连接。一次测试只能证明当前卡、当前网络和该次呼叫，不证明长期/通用稳定。

在 Windows 接管 IMS、模组 IMS 被暂停时，明确走蜂窝 AT 基带的测试短信返回失败，收件手机确认未收到。恢复模组 IMS 后再次通过蜂窝 AT 提交另一条短信，基带返回 `+CMGS: 27 / OK`，用户确认实际收到。两个结果只验证模组路径在这两个状态下的表现，尚不能证明最初失败的唯一原因。Host IMS 仍未宣告或处理 SIP `MESSAGE`，**没有发送 Windows IMS 短信**；不能以基带测试代替该功能验收。下一步应在严格隔离基带与 Host 路径的前提下实现、测试 SMS over IMS，并避免接管期间静默丢失短信。

### 2026-10-08 Windows Host IMS 首条短信

随后新增显式 `sms send <号码> <文本> --host-ims --no-receipt`：只在当前卡槽 Windows Host IMS 注册有效时，从 SIM 查询 SMSC，将 TPDU 封装为 RP-DATA，经该注册的受保护 SIP 传输发送 `application/vnd.3gpp.sms` 二进制 `MESSAGE`。发送端使用注册后网络关联的公开身份及 Service-Route，并按 `In-Reply-To`/RP 引用匹配另一路 `MESSAGE` 中的 RP-ACK/RP-ERROR。SIP 2xx 只代表 MESSAGE 被接受；收到 RP-ACK 才标记提交成功，仍不代表最终手机送达。无最终 SIP 响应或 RP 回执时不自动重发，以免重复短信。此入口不回退到模组 AT 或 VoWiFi，`--no-receipt` 暂为必填；Host 注册继续不宣告 MT SMS 的 `+g.3gpp.smsip`，收到非提交回执的 RP-DATA 不会假装已处理。

管理员实测再次自动取得 CID 5 IMS PPP/P-CSCF，Windows 使用 QMI USIM AKA 和 C# 用户态 ESP 取得运营商 `REGISTER 200 OK`。仅发一条用户授权的短短信：SIP 返回 `202`，随后收到并匹配 RP-ACK，用户确认目标手机实际收到。未使用模组 AT 发送这条短信。测试后注销得到 200，PPP 租约正常停止，独立 AT 读回模组 `ims=1,1`、内部 CID 5 活动，`rasdial` 无连接。这证明当前 SIM/网络的 Windows MO SMS 路径可用；MT 短信、状态报告、多段短信、丢包/续注册下的发送、WPF 入口、长期与跨运营商稳定性尚未验收。实现依据 [3GPP TS 24.341 §5.3.1.2](https://www.etsi.org/deliver/etsi_TS/124300_124399/124341/19.00.00_60/ts_124341v190000p.pdf) 的 MO SMS SIP MESSAGE/RP 流程；未完成 §5.3.2.2 的 MT 接收前不宣告 MT 能力。

### 2026-10-08 Windows Host IMS 下行短信

后来补上显式接收路径。CLI 使用 `host-ims register --userspace-esp --sms-receive` 才在 Contact 宣告 `+g.3gpp.smsip`；WPF 状态页新增默认不勾选的相同能力。对受保护的下行 `application/vnd.3gpp.sms` SIP `MESSAGE`，Windows 先确认 SIP 传输，解析 RP-MT-DATA 和 TPDU、重组分片，并通过卡槽短信事件交给内核/UI；另发带原 Call-ID 的 `In-Reply-To` 的 SIP `MESSAGE`，以 RP-ACK 或 RP-ERROR 回告 IP-SM-GW。相同短信事务重传不重复投递，但重新发送 RP 回执；相同 Call-ID/CSeq 若载荷或网关身份不同则拒绝。实现依照 [3GPP TS 24.341 §5.3.2.2–5.3.2.4](https://www.etsi.org/deliver/etsi_TS/124300_124399/124341/19.00.00_60/ts_124341v190000p.pdf)。

本地假 P-CSCF 测试覆盖接收一次、RP-ACK、重传、冲突事务与畸形 TPDU 的 RP-ERROR；VoSharp 全量 507 项和 WPF Release 构建通过。随后在真实 Windows PPP + C# ESP 上带 `--sms-receive` 注册获运营商 200。用户手机向模组号码发送一条固定内容短信，Windows CLI 显示正确发送方与内容，内核收件箱只有一条；用户确认手机端显示已发送。注销获 200、PPP 正常停止，AT 读回模组 `ims=1,1`、CID 5 活动，RAS 与恢复任务无残留。本轮未单独记录 RP-ACK 外发的最终网络 SIP 响应；随后源码增加只记录 SIP 状态码、不记录短信内容的回执确认日志，待下次实网使用。CLI 内核收件箱为内存态，退出后不保留。WPF 使用已有短信数据库保存事件的代码路径，但新增选项尚未实机点按验收。长时接收、服务重启时消息持久性、状态报告及跨运营商仍待验证。

## 下一阶段

当前插卡组合已在 PPP bearer 上完成实网双向通话；这不等于所有 bearer 和运营商均可用。无卡时探针仍只做硬件/驱动预检，不能验证 IMS 注册能力。后续重点：

1. 在现有 PPP 卡槽租约上做更长时间的注册/承载耐久测试，并重复多次续注册后的来电与双向语音；继续验证 RTCP、专用承载/QoS 和错误恢复，同时研究 QMI QMAP 多数据流以保持 Internet 与 IMS 两个 context 并存；
2. 继续将来电、呼叫保持、短信及媒体安全策略统一到接入无关的 IMS UA；当前 Host 注册与普通 SIP 通话已接入卡槽编排；
3. 在真实 `HostRoutable` bearer 上验证不要求 `ipsec-3gpp` 的网络的 UDP SIP 注册；
4. 对要求 `ipsec-3gpp` 的网络优先验证已接线的 C# 用户态 ESP + Windows 原始包入口，或继续研究 WFP SA / QMI raw-IP 备选；
5. 继续实测外拨、专用承载/QoS、MT SMS 的长时/重启行为与送达报告、Internet PDN 并存及 WPF 端到端操作；其中 REGISTER、一次通话后的自动续期、真实来电/双向语音，以及单条 Windows MO 与 MT SMS 已在当前组合验证。

在探针确认 `HostRoutable` 前，不应将 Host VoLTE 标为已实现。
