# qmiSharp

qmiSharp 是面向 Windows/.NET 10 的 Qualcomm QMI 协议库。协议定义以同级目录
`references/libqmi` 为核对基准，当前提供 QMUX/QRTR 虚拟帧、TLV、事务/CID 管理、
本地 TCP 代理，以及一组常用 QMI 服务的手写高层 API。

## 支持边界

- `Generated/QmiProtocolCatalog.cs` 包含 377 条 libqmi 消息/指示元数据。它是元数据目录，
  不代表 377 条消息都已有完整的强类型编解码 API。
- `Services/` 有 26 个服务类；每个类只实现其中列出的高层方法，不能视为 libqmi 的完整移植。
- Windows `qcusbwwan`/Mobile Broadband 驱动持有 QMI CTL 通道；AT COM 口不能接收
  QMUX 数据。当前 Quectel 驱动另在网卡注册表发布私有控制文件，允许按服务申请
  设备文件与 CID。`QualcommWwanTransport` 使用该协议，驱动负责 CTL 和 CID 生命周期。
- `AT+QCFG="usbnet"` 返回 `0` 只说明模组使用 RmNet/QMI USB 网络模式，**不等于**
  Windows 驱动向用户态开放了可读写原始 QMI 帧的设备路径。自动识别 AT 口、USB
  父设备和网卡是可行的；`FindControlPathForAtPort` 按 USB ContainerId 将指定 AT 口
  绑定到同一物理模组的唯一 Quectel 网卡，并校验控制文件与 NetCfgInstanceId 一致。
  配对缺失或歧义时回退 AT，不取枚举顺序中的第一个网卡。
  不能把 NDIS 网卡接口、DM/NMEA/AT 串口或 ADB 接口猜作 QMI 端点。
- VoWin 现通过 `VoSharp.Modem` 引用本库。默认按 AT 口自动发现其物理模组的 Quectel 驱动控制文件；
  也可对某个 AT 口设置
  `VOWIN_QMI_ENDPOINT_COM6=device:\\.\<raw-qmi-device>` 或
  `VOWIN_QMI_ENDPOINT_COM6=tcp://127.0.0.1:4765` 后，附着时先核对 QMI 与 AT
  报告的是同一 IMEI；再将该卡槽的 SIM 在位、IMEI、IMSI、ICCID、NAS 信号与驻网
  查询优先走 QMI。端点不可用、身份不符、服务失败或字段不可信时回退 AT。
  TCP 代理只接受回环地址，且 COM6 只是此例的 AT 口名，不是 QMI 设备路径。
  Host IMS 探针也优先尝试 WDS 已连接 context 的 APN、IP 地址和 P-CSCF；无 SIM 时可只读枚举 WDS Profile List/Settings 中配置的 `ims` APN，
  仅在确认 `ims` APN、Windows 网卡持有该地址且到 P-CSCF 的路由正确时给出
  Host 候选。QMI 信息不完整时回退 AT。实验性独立 WDS IPv6 bearer 已实机成功建立并
  自动停止；尚未映射到 Windows 网卡，也未取得 IPv6 P-CSCF，不能用于 Host IMS。
- CLI 使用 `--auto` 打开唯一的 Quectel 私有 QMI 控制文件；另可显式使用
  `Win32DeviceTransport` 指向真实 raw-QMI 设备路径，或通过 `SocketQmiTransport`
  连接保留 QMI 帧边界的代理。自动发现不会把 AT COM 口当成 QMI 设备。
- `QrtrVirtualHeader` 与 libqmi 的内存格式一致（marker + 5 字节头）；真正的 QRTR socket
  transport 应在发送前剥离该虚拟头。本仓库目前没有 Windows QRTR socket 实现。

## 构建与测试

```powershell
dotnet build qmi/qmiSharp.slnx --no-restore
dotnet test qmi/qmiSharp.slnx --no-restore
```

测试不使用模拟调制解调器。它验证 libqmi 原始 QMUX/QRTR 黄金字节向量、严格帧长度和
结果 TLV，并逐条比较本地 libqmi JSON 与生成目录的消息身份。

## Windows 真实模块只读探测

```powershell
dotnet run --project qmi/qmiSharp.Cli/qmiSharp.Cli.csproj -- --hardware-info
# 或指定 AT 口
dotnet run --project qmi/qmiSharp.Cli/qmiSharp.Cli.csproj -- --hardware-info --at-port=COM9
```

该命令只读取 `ATI`、IMEI、SIM、运营商、信号、USB 网络模式和 IMS 设置，并显示
`netsh mbn show interfaces`。它不会拨号、发送短信、修改 SIM/PDC/MBN 或切换配置。

## 原始 QMI CLI

```powershell
# 自动发现唯一的 Quectel qcusbwwan 私有控制通道（只读查询示例）
dotnet run --project qmi/qmiSharp.Cli/qmiSharp.Cli.csproj -- \
  --auto --dms-get-model --nas-get-signal-info

# 只读检查独立 WDS CID 和 USB 数据格式
dotnet run --project qmi/qmiSharp.Cli/qmiSharp.Cli.csproj -- \
  --auto --wds-probe-cid-isolation --wda-get-data-format

# TCP QMI 代理
dotnet run --project qmi/qmiSharp.Cli/qmiSharp.Cli.csproj -- \
  --tcp=127.0.0.1:4765 --dms-get-model

# 驱动明确暴露的 raw-QMI Win32 设备路径
dotnet run --project qmi/qmiSharp.Cli/qmiSharp.Cli.csproj -- \
  --device='\\.\<raw-qmi-device>' --nas-get-signal-info
```

`QmiProxyServer` 会重组帧、重映射上游 transaction ID、按请求路由响应，并按 CID
所有权投递服务指示，避免把响应广播给其他客户端。

## 关键实现说明

- 响应按 service、CID、transaction ID 和 message ID 四元组关联；只有 response 能完成请求。
- CID 有引用计数；16 位 QRTR 服务使用 CTL `0xFF22`/`0xFF23` 和 16 位 service TLV。
- WDS 可申请独立 CID，不与常规只读 WDS 客户端共享会话；请求和释放均使用该 CID。`StartNetwork` 使用独立的 90 秒响应超时，取消/超时会按 WDS 协议发送 Abort；若 Abort 未获确认会报告 bearer 状态不确定，并拒绝释放该 CID。服务会追踪成功返回的连接句柄，停止成功后才清除；释放服务时先用独立的 30 秒超时停止连接，再释放 CID，失败则保留句柄供重试。它是独立 IMS PDN 的控制面前提，**并不等于已完成 IMS bearer 激活或 Windows raw-IP/QMAP 网卡映射**。
- PDC `Get/Set/List/Activate` 使用 token 对应异步 indication；Activate 消息为 `0x0027`。
- `CheckResult()` 要求合法的结果 TLV；缺失或截断会失败，而不是被当作成功。
- WDS/WDA/UIM/VOICE 的已实现字段布局按本地 libqmi JSON 定义编码和解析。UIM 卡状态遍历全部卡与应用，按 GW primary 索引判定选中的 SIM/USIM；索引无效、未选应用或响应截断时不把其他卡的状态误报为当前卡。主卡在物理槽 1 且其他槽明确为空、未激活时允许 QMI UIM APDU；其他槽有卡/激活或槽状态响应截断时拒绝，避免误选卡。Host IMS 的 ISIM 身份与 AKA 可优先走 QMI UIM 逻辑通道；无 ISIM 的 USIM AKA 也会在网络挑战前预检 QMI 通道。Host IMS 的 QMI ISIM/USIM 预检和 AKA 入口先核对 QMI 与已打开 AT 口的 IMEI；预检或读卡失败才选 AT，QMI AKA 开始后不跨通道重放挑战。APDU 响应长度必须严格匹配，不会截断后当作成功。当前实机槽 2 为空，QMI USIM 逻辑通道预检已通过，但尚未执行 AKA。

真实网络操作（数据会话、拨号、短信、SIM PIN、PDC 激活）会改变模块或运营商状态，
不属于默认测试流程，应由调用者显式授权并自行承担影响。

### 当前 Windows 实机接口核查（2026-09-29）

插入的 `VID_2C7C&PID_0125` 模组在 Windows 上绑定 `qcusbwwan` 网卡驱动；PNP 接口
枚举未显示通用 raw-QMI 设备，但网卡驱动注册表有 `QCDeviceControlFile`。基于
[Qualcomm 开源 Windows USB 驱动](https://github.com/qualcomm/qcom-usb-kernel-drivers)
的控制文件协议，实现 `QMI_READY`、`GET_SERVICE_FILE` 与服务文件 CID 分配；驱动封装
外层 QMUX，本库只向服务文件发送/读取服务消息体。实机 `--auto` 已读取 DMS 厂商/型号、
NAS 信号、UIM 卡状态和 WDS 包服务状态，QMI 与 AT IMEI 一致。COM6 当时被占用，AT
核验改用独立的 COM9 Modem 口。`CPIN` 为 READY、`CEREG` 为已驻网、`usbnet` 为 0。

IMSA 状态读取现按 [libqmi 的 IMSA 消息定义](https://github.com/linux-mobile-broadband/libqmi/blob/main/data/qmi-service-imsa.json)
修正注册/服务状态的 TLV 编号、32 位枚举和异步指示 ID，不再把状态值 `2`
误读为未注册或把服务值 `2` 误读为不可用。只读命令
`--auto --imsa-get-ims-registration-status --imsa-get-ims-services-status`
在当前模组上返回基带 IMS **已通过 WWAN 注册**、语音服务 **available**；
这说明当前 SIM/运营商在模组内部 IMS 路径具备 VoLTE 前提，不能据此认定
Windows Host IMS 已注册、ESP 已通或语音已迁移。查询未发送 SIP、电话或短信。

控制面现能报告 WDS `StartNetwork` 的基本和详细拒绝原因，并解析 IPv6 地址、前缀、
网关和 DNS。两条独立 WDS CID 已实机验证，WDA 返回 RawIP + QMAP v1。保留模组内
IMS 时，外部 IPv4/IPv6 WDS `ims` 分别被拒绝为内部原因 208（IPv4 不允许）和 241
（接口已有相同配置）；按[移远 IMS 配置说明](https://quectel.com/content/uploads/2024/02/Quectel_EC2xEG2xEG9xEM05_Series_QCFG_AT_Commands_Manual_V1.0.pdf)
短暂禁用模组内 IMS 并重启后，独立 IPv6 `ims` WDS 会话建立成功，随后已 Stop 并释放
CID、恢复原配置及内部 IMS 活动状态。此时 Windows 网卡仍未持有独立 bearer 地址，
QMI 设置也没有返回 P-CSCF，故不能宣称 Host IMS 可用。

取得管理员令牌后，已运行 [Additional PDP 探针](../tools/Probe-WindowsImsPdp.ps1)。
Windows 对现有 `ims` Profile 回报“接口不支持多个 PDP 上下文”；已安装的 Quectel
`qcusbwwan` 20.0.72.21 向 MBN 上报最大激活上下文数 1，同系开源驱动源码也将
`MaxActivatedContexts` 固定为 1。此限制来自当前 Windows 驱动/接口能力上报，
**不代表模组只能建立一个 WDS CID 或一个 PDP**。

进一步逐行核对[高通开源 Windows NDIS 驱动的设备能力响应](https://github.com/qualcomm/qcom-usb-kernel-drivers/blob/a42c05276c1aa45eab231760502665c8ccef6b14/src/windows/ndis/MPQMI.c#L681-L709)和[驱动自己的 WDS Bind Mux Data Port 实现](https://github.com/qualcomm/qcom-usb-kernel-drivers/blob/a42c05276c1aa45eab231760502665c8ccef6b14/src/windows/ndis/MPQMUX.c#L26060-L26165)：前者固定向 Windows 宣告一个活动 context，后者由 miniport 给自身适配器分配 mux ID 并绑定 USB 数据接口。当前安装的是 Quectel 驱动，不把这份源码等同于其二进制实现；但实机同样上报一个 context，且只读网卡属性未显示第二条可配置数据接口。因而在本库额外发送 WDS Bind Mux 请求**不会自动生成 Windows 虚拟网卡**，还可能与驱动已分配的 mux ID 冲突；QMAP 方向需要驱动级多 context/虚拟接口支持或另一个明确拥有数据流的传输后端。现阶段 PPP 是已实测可被 Windows 持有的 IMS 数据面。

又用 [临时主 IMS Profile 探针](../tools/Probe-WindowsPrimaryImsPdp.ps1) 和
[自动恢复的内部 IMS 交接脚本](../tools/Probe-WindowsPrimaryImsHandoff.ps1) 验证单
上下文路径。Profile 只在临时文件中生成，自动发现会在重启后变化的 IMS Profile 名称；
交接脚本要求原始 `QCFG ims=1,1`，短时禁用内部 IMS、重启并等待驻网，试验后断开并
恢复原设置。临时 IPv6 主 Profile 能到达 WWAN 驱动，但系统返回
`WWAN_STATUS_FAILURE`；事件日志短暂出现 `Activated`，随即由 WWAN 下发
`Deactivate`。90 秒连续观察中 Windows 网卡始终未连接、未取得全局/专用 IPv6。
实验后已核验模组 `ims=1,1`、内部 CID 5 激活、Windows MBN 断开。没有发送短信或拨打语音电话。

另用 [MBIM USB 组成探针](../tools/Probe-WindowsMbimComposition.ps1) 临时测试
`usbnet=2`：模组接受并保留 AT/USB Audio，但 Windows 未出现 MBN/MBIM 接口；
已恢复 `usbnet=0`。COM9 的 `ATD*99***5#` 可进入 PPP 数据模式。
[Windows RAS 探针](../tools/RasImsPppProbe.cpp) 的默认协商收到 CCP 协议拒绝，
`--no-ccp` 消除此拒绝，但内部 IMS 占用 CID 5 时仍在网络层协商前断链。
使用[交接脚本](../tools/Probe-WindowsImsPppHandoff.ps1) 暂停内部 IMS、等待
RAS 设备重枚举后，Windows PPP 已连续两次成功连接 CID 5；IPv6CP 协商成功，
Windows PPP 接口取得 IMS 前缀下的全局 IPv6 `/64`，并拥有经该接口的默认
IPv6 路由。模组 `CGCONTRDP=5` 同时报告活动 `ims` PDP。末尾两项实际是
IPv6 P-CSCF，并非 DNS。修订后的 Host IMS 探针在该 PPP 连接期间实机报告
`HostRoutable`、`CanAttemptWindowsIms=true`，两条 P-CSCF 路由都匹配 Windows
PPP 接口和 IMS 源地址。Windows PPP DNS 仍是占位地址，但 P-CSCF 可由 AT
直接取得。QMI WDS 独立 CID 对模组内部 IMS 查询 runtime settings 返回
`OutOfCall`；本库另补齐 TLV `0x2E` 的 IPv6 P-CSCF 解析，供自己建立的 QMI
bearer 使用。路由隔离和 IMS IPsec 尚未完成，运营商 SIP/通话未验收。连接已挂断，
`ims=1,1` 与内部 CID 5 已恢复。交接脚本在第一次恢复只到 `ims=1,0` 时会重申
设置并最多再重启一次。QMI RawIP/QMAP 仍是双 PDN、高吞吐方向；USB Audio
呼叫/语音桥接继续保留。

随后在活动 PPP 上复测到接口 IPv6 metric 9999，但 PPP 是系统唯一的 IPv6
默认路由，高 metric 不能阻止普通 IPv6 流量进入 IMS APN。新增的
[定向路由隔离诊断](../tools/Test-WindowsImsPppRouteIsolation.ps1)使用当次
`CGCONTRDP=5` 的两条 P-CSCF 地址，临时添加仅目标 `/128` 路由、移除 PPP
`::/0`，实机确认两条 P-CSCF 仍以 PPP 的 IMS 地址为源；`finally` 恢复默认
路由并删除测试路由，事后核对无测试路由残留。单独运行该脚本需传入 P-CSCF，
也不构成可供产品持有的 PPP bearer；运营商 REGISTER 和加密实包未执行。
`Probe-WindowsImsPppHandoff.ps1 -IntegratedRouteProbe -HoldSeconds 90`
现已将当次 P-CSCF 自动检测、IMS `/64` 交叉核对、路由隔离和应用只读
`host-ims probe` 串在同一 PPP 保持期。实机返回 `HostRoutable` 与
`CanAttemptWindowsIms=true`；PPP 挂断后 IMS 恢复 `1,1`，测试路由和临时
日志均未残留。该流程仍不发送运营商 SIP REGISTER，也未解决应用内长期
bearer 生命周期与 IMS IPsec。
RAS 原生探针另支持 `--no-ccp --until-stop --max-seconds=N`，要求父进程 stdin
管道；`STOP`、父端关闭、RAS 掉线或最长时限都会结束持有并显式挂断。
交接脚本现用此模式：读只读探针后发 `STOP`，实机正常退出；模拟父端关闭
管道也触发自动挂断。两次均恢复 `ims=1,1`，没有残留 PPP 接口或目标路由。
它为应用内租约提供了原生持有机制，但还没接入 VoSharp 卡槽生命周期。
上述“父端关闭”仅指 RAS 子进程的控制管道，外层交接脚本仍在运行并执行
`finally`；单靠这一试验不能证明外层崩溃恢复，也不会默认取代 USB Audio。
交接脚本现在先启动[独立恢复守护](../tools/Restore-WindowsImsOnOwnerExit.ps1)
并收到监视确认，才会关闭模组内部 IMS。守护绑定原模组 IMEI；外层退出后
只对同一模组的 `ims=2,0`/`1,0` 做受控恢复。实机在拨 PPP 前故意让外层
进程跳过 `finally` 退出，守护将 `ims=2,0` 恢复为 `1,1`，独立 AT 查询复核；
正常路径则在恢复后解除守护。当时 PPP 在线时外层崩溃和机器重启后的恢复
尚未验收。后续又在 PPP 在线且 P-CSCF 定向路由生效时
故意终止外层进程：RAS 子进程随控制管道关闭而挂断，PPP/目标路由消失，
IMEI 绑定守护把 `ims=2,0` 恢复为 `1,1`，独立 AT 复核。两个故意崩溃开关
现在都要求显式环境变量 `VOWIN_IMS_ALLOW_CRASH_PROBE=1`；系统断电及守护
自身丢失仍未验收。
外层交接脚本现在支持 `-ManagedLease -IntegratedRouteProbe`，在连接、当次
P-CSCF 自动发现和定向路由完成后输出 `VOWIN_IMS_BEARER_READY`，持续
等待父端 `STOP`/EOF，同时核对 PPP 和路由仍有效；正常退出时按顺序恢复路由、
挂断 RAS、恢复内部 IMS。[VoSharp 托管持有器](../../voSharp/src/VoSharp.Modem/WindowsHostImsPppBearerLease.cs)
通过重定向 stdin/stdout 持有此流程。实机的 opt-in 测试在租约中让
VoSharp 自行探测出 `HostRoutable`，释放后独立复核 `ims=1,1` 且无 PPP/路由
残留。该类尚未接入卡槽和 UI，也未尝试运营商 REGISTER。
