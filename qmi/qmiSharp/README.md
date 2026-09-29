# qmiSharp

qmiSharp 是面向 Windows/.NET 10 的 Qualcomm QMI 协议库。协议定义以同级目录
`references/libqmi` 为核对基准，当前提供 QMUX/QRTR 虚拟帧、TLV、事务/CID 管理、
本地 TCP 代理，以及一组常用 QMI 服务的手写高层 API。

## 支持边界

- `Generated/QmiProtocolCatalog.cs` 包含 377 条 libqmi 消息/指示元数据。它是元数据目录，
  不代表 377 条消息都已有完整的强类型编解码 API。
- `Services/` 有 26 个服务类；每个类只实现其中列出的高层方法，不能视为 libqmi 的完整移植。
- 标准 Windows `qcusbwwan`/Mobile Broadband 驱动通常在内核中持有 QMI 通道，只暴露
  NDIS/MBN 接口和 AT 串口。AT COM 口不能接收 QMUX 数据。
- VoWin 现通过 `VoSharp.Modem` 引用本库。对某个 AT 口设置
  `VOWIN_QMI_ENDPOINT_COM6=device:\\.\<raw-qmi-device>` 或
  `VOWIN_QMI_ENDPOINT_COM6=tcp://127.0.0.1:4765` 后，附着时先核对 QMI 与 AT
  报告的是同一 IMEI；再将该卡槽的 SIM 在位、IMEI、IMSI、ICCID、NAS 信号与驻网
  查询优先走 QMI。端点不可用、身份不符、服务失败或字段不可信时回退 AT。
  TCP 代理只接受回环地址，且 COM6 只是此例的 AT 口名，不是 QMI 设备路径。
  Host IMS 探针也优先尝试 WDS 已连接 context 的 APN、IPv4 地址和 P-CSCF；无 SIM 时可只读枚举 WDS Profile List/Settings 中配置的 `ims` APN，
  仅在确认 `ims` APN、Windows 网卡持有该地址且到 P-CSCF 的路由正确时给出
  Host 候选。QMI 信息不完整时回退 AT。当前没有自动发现 raw-QMI 端点，
  没有通过 QMI 激活 IMS bearer，也未支持 WDS IPv6 数据面。
- 原始 QMI 调用必须显式使用 `Win32DeviceTransport` 指向真实 raw-QMI 设备路径，或通过
  `SocketQmiTransport` 连接保留 QMI 帧边界的代理。CLI 不会再把自动发现的 AT COM 口
  当成 QMI 设备。
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
- WDS/WDA/UIM/VOICE 的已实现字段布局按本地 libqmi JSON 定义编码和解析。UIM 卡状态遍历全部卡与应用，按 GW primary 索引判定选中的 SIM/USIM；索引无效、未选应用或响应截断时不把其他卡的状态误报为当前卡。单卡、物理槽映射明确且 ISIM AID 可读时，Host IMS 的 ISIM 身份与 AKA 可优先走 QMI UIM 逻辑通道；无 ISIM 的 USIM AKA 也会在网络挑战前预检 QMI 通道。预检或读卡失败才选 AT，QMI AKA 开始后不跨通道重放挑战。APDU 响应长度必须严格匹配，不会截断后当作成功。多物理槽 QMI APDU 路径尚未实机验证，当前保守回退 AT。

真实网络操作（数据会话、拨号、短信、SIM PIN、PDC 激活）会改变模块或运营商状态，
不属于默认测试流程，应由调用者显式授权并自行承担影响。
