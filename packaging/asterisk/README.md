# VoWin 与 Asterisk 私网部署

Windows 的[部署 v2 导入与独立权限](../../voSharp/docs/SIP_GATEWAY.md#部署-v2-导入与独立权限)
已接入 reader、设置与运行时，软件验证见[阶段记录](../../publish/windows-deployment-v2-20261024/README.md)。
新版 `configure.py` 已接入公开 v2 模型与独立秘密解析，生成 Windows／Go／Asterisk 的私密配置包。
原生 Linux 使用 `--config public.json --secrets private.json --output new-directory`，拒绝覆盖已有目录。
详细能力、安全边界和验证见[生成器阶段](../../publish/asterisk-render-v2-20261024/README.md)。
管理网页现有独立的 v2 草稿模式：显式初始化私密 store，并以 `-v2-store` 启动，
`canApply: false`，只保存草稿、不安装／应用／重载。不带该参数仍使用旧热加载模式，
且拒绝 v2 文件，避免丢失独立权限／秘密引用；旧共享凭据不能猜测升级。
另有默认关闭的网页应用入口，显式配置 `-v2-application`、固定镜像与接管确认后，
才能独立预检／确认应用／恢复；[使用方式](admin/README.md#v2可选网页预检应用与恢复)与
[阶段证据](../../publish/asterisk-web-apply-v2-20261024/README.md)列出任务持久化和未验收边界。
以下未注明 v2 的命令仍是旧生成器说明，不代表生产迁移已经完成。

新增可选的[分机管理网页](admin/README.md)：账号、密码、启停、群振、权限与真实注册状态。
独立运行，不强制替换 VoWin 内置 SIP Server。旧模式导入旧分机和中继、保存前备份并热加载；
v2 模式管理单独的私密草稿，保存与应用严格分开。

## v2 私密草稿管理入口

完整步骤见[管理页说明](admin/README.md)，
实现及验证边界见[管理 v2 阶段记录](../../publish/asterisk-admin-v2-20261024/README.md)。
以下示例仅初始化草稿，不生成安装动作；在原生 Linux 上运行，使用自己的绝对路径：

```sh
python3 /opt/vowin-tools/asterisk/admin_deployment_v2.py \
  --action init --store /var/lib/vowin-pbx-admin/draft-v2 \
  --public /root/vowin-private/deployment-v2.public.json \
  --secrets /root/vowin-private/deployment-v2.secrets.json < /dev/null

./pbx-admin -base /opt/vowin-asterisk \
  -generator /opt/vowin-tools/asterisk/configure.py \
  -container vowin-asterisk -listen 127.0.0.1:9088 \
  -v2-store /var/lib/vowin-pbx-admin/draft-v2
```

公开输入是**完整 v2 模型**，秘密输入是精确覆盖其全部 `SecretRef` 的独立 map，不是旧版
`credentials.json`，也不是 `gateway-client.json`。`--store` 目标必须尚不存在，父目录须已准备好；
store 与 `-base` 不能相同或互为子目录。使用受控私有目录，祖先不可被组／其他用户写入、
不使用符号链接；秘密输入须由同一有效用户拥有且权限为 `0600`。store 内目录／文件分别为
`0700`／`0600`，含历史秘密，不能公开上传。配套 Python helper 必须与 `-generator` 指定的
`configure.py` 同目录。构建、私网绑定与密钥管理要求详见管理页说明。

v2 的运营商外呼、内部文本、手机短信发送与接收四项权限独立；新分机账号、群振和权限
默认全关，终端上限 `4`，已保存号码只读，允许零个账号。密码可保留、替换或显式选择服务端生成。
成功保存只产生 `saved-not-applied` 草稿，不修改当前 PBX；草稿连接信息也不代表已应用。
真实 Contact 状态单独显示，不能用草稿启停推断实际注册。

`409` 保留本地草稿，重新加载需确认；`503` 提交结果可能不确定，先重新读取再决定，不能
自动重存或声称已回滚。`401`／退出会清除网页密码草稿和私密连接弹窗。默认没有应用、
恢复按钮；显式开启后的独立任务不随退出管理取消。不会自动迁移旧共享密码。
独立维护 CLI 见下一节；接管后的目标不能再走旧应用入口。

## v2 独立受控应用 CLI

新增 [`admin_apply_v2.py`](admin_apply_v2.py)，只在原生 Linux 以 root 对明确选中的现有 Docker
PBX 执行受控维护；不改变草稿 API 的 `canApply: false`。完整动作示例及故障处理见
[管理说明](admin/README.md)，实际能力与未验收边界见
[应用阶段记录](../../publish/asterisk-apply-v2-20261024/README.md)。

| 动作 | 必需参数（除 `--action`） | 意义 |
| --- | --- | --- |
| `status` | `--base` | 只读 journal，不是 live health |
| `overview` | `--base` | 非阻塞只读应用／installed 指纹记录，不是 live health |
| `prepare` | `--base --store --container --image --revision --confirm-v2-management` | 固定快照、实际隔离预检、登记 v2-only 管理 |
| `apply` | `--base --store --container --image --revision --operation --confirm-restart` | 优雅停机、完整安装、启动验证 |
| `recover` | `--base --store --container --image --operation --confirm-restart` | 显式恢复该操作的原始 PBX |

三个修改动作可显式加 `--restart-policy no` 或 `--restart-policy unless-stopped`（默认后者），
并在整个操作中保留同一声明。每条命令须以 `< /dev/null` 等方式提供 EOF；参数严格校验。
43 位 revision 从既有 store 的读取输出 `value.revision` 取得，operation 与待应用 revision
从成功 prepare 的 `value.operation`／`value.revision` 取得，不自行编造。镜像须是审核过的本地
不可变 `sha256:…` ID，不能使用 tag；不自动拉取。应完整部署 engine、Docker adapter、store、
model、bundle writer、各 renderer 等 Python 支持模块，不能只复制 CLI 单个文件。

目标 `--base` 是 root 私有 `0700` 目录；其固定 `config/` 由 `10001:10001` 拥有，模式为
`0700` 或合法旧配置的 `0750`，已有受管 `.conf` 为同一所有者且 `0600`／`0640`，根目录
导出文件为 root／`0600`。现有容器必须采用用户 `10001:10001`、只读根、cap-drop ALL、
no-new-privileges、非 privileged、无设备或 Docker socket，network 为 host 或隔离 none。
唯一 `/etc/asterisk` 只读 bind 精确指向该 `config/`，无其父／子挂载覆盖或未支持的额外挂载。

第一次 prepare 会留下永久 v2 guard，阻止旧版四文件应用；后续预检失败也不会自动解除或
退回旧模式，不能删 guard 绕过。预检使用自有 network-none 容器、独立私密配置／tmpfs，
不复用目标可写卷、不接外网或模组。应用保持容器身份、配置目录 inode，以逐文件方式安装
完整 20 个产物（17 个配置＋3 个导出），不是热加载两个文件。

停止前检查空闲，临时将 restart policy 设为 no，使用 graceful shutdown 阻止新呼叫并等待退出，
不超时强杀。正常失败路径恢复全部原始文件、存在／缺失状态及权限，再启动并验证；不确定或待恢复
状态必须先读 journal、显式 recover，不能自动重放。第三种外部修改会拒绝覆盖；候选损坏不妨碍
以可信原快照恢复，但原快照或 journal 损坏会拒绝，不猜测修复。

成功只表示**该次 PBX 应用已验证**，status 仍只是 journal。安装导出文件不等于导入网关或加载
nft：`gatewayApplied`、`firewallApplied` 仍为 false；网关导出未变不强制重复导入，导入状态未知
也不能说已应用。生成的网关 profile 默认停用，仍需它自己的受控导入／启用及真实电话、短信验收。

VoWin 可以独立作为私网 SIP Server，管理分机注册、分机互拨、来电群振和 SIP MESSAGE。
同一局域网或 VPN 内不必部署 Asterisk；需要集中管理远端软电话时，可选择 Asterisk 固定中继。
两种模式都不要求关闭本机通话、Host IMS 或 VoWiFi。不要把 UDP SIP 直接暴露到公网。

## VoWin 独立 SIP Server

1. 打开「设置 → SIP 对接」，关闭「使用 SIP 服务器中继」。填写 Windows 已分配的
   局域网 / VPN 地址、UDP 端口、Realm、主分机账号和密码。地址不能填写服务器地址或
   `0.0.0.0`。所有部署参数均可修改，不绑定某个内网、国家、分机号或 COM 口。
2. 在「独立 SIP Server → 附加分机」添加其他账号，每个账号使用独立强密码。
   附加分机的 SIM 外拨、发短信权限默认关闭，可单独指定默认模组。
3. 点击「启用 SIP 对接」。软电话填写 Windows 地址和端口、对应分机账号与密码，选择
   UDP 和 G.711 PCMA / PCMU。刷新 VoWin 状态，可查看注册终端地址及到期时间。
4. 先拨另一个分机号。分机互拨不经过 SIM，没有模组也能使用；某个分机账号注册多台
   终端时同时振铃，先接听的终端获得通话，其他终端停止振铃。
5. 需要运营商通话时，再配置「内部 VoIP → 每个模组独立配置」和全局 SIM 外拨开关。
   各模组沿用自己的 Host IMS / VoWiFi / 桥接接入，离线或权限不足时失败，不自动换卡。

Windows 防火墙需要允许所选 VoWin 程序的 UDP 入站，来源仅限你的 LAN / VPN 网段或
固定 PBX 地址。RTP 使用动态本地端口，不能只放行 SIP 端口。移动 exe 后应核对程序路径。
不提供手机后台推送；需要接听的软电话应保持注册和网络可达。

## 短信与文本消息

开启「SIP 文本消息」后，直连分机可用 `text/plain` UTF-8 MESSAGE 互发文本，不经过 SIM。
不支持 CPIM / IMDN、附件、彩信或 RCS。SIP MESSAGE 是独立事务，不需要先接通电话。
协议参见 [RFC 3428](https://www.rfc-editor.org/rfc/rfc3428.html)。

收到 SIM 短信后转发，需要同时开启全局「收到 SIM 短信时转发到 SIP」和对应模组的
「转发收到的短信」。短信目标与来电目标独立：直连时留空发给所有在线分机，填写账号则
只发该分机；中继时留空沿用全局目标。转发不删除原短信，也不改变本机短信通知。
验证码等敏感内容会出现在这些终端，请只加入可信设备。

经 SIM 发短信需要全局允许、模组允许及对应主账号／附加账号／peer 的独立权限。使用明确的默认线路、
号码规则或 `X-VoWin-Line`，不按当前页面选中的模组发短信。目标用含国家码的手机号，
或使用你配置的补号规则。短信按模组串行提交，另一模组不被阻塞。

VoWin 对逻辑 MESSAGE 事务去重，限制同一账号的消息速率，不自动重发部分成功的短信。
入站 SIP 正文上限 1024 UTF-8 字节；较长的转发短信按 Unicode 字符边界拆为带序号的
短消息，每个出站 UDP MESSAGE 不超过 1300 字节。桌面 MT 已接入有界持久离线转发，
原计划与实际 Inbox 同事务保存；Unknown 不自动重发，不提供手机后台推送。只有未接持久
存储的旧库回调路径不积压离线目标。更改设置前须先关闭 SIP，再启用以应用新规则。

转发消息显示原发送方号码，SIP 回复地址额外携带原模组的稳定标识。分机点「回复」时
固定回到原线路；该线路消失或权限关闭会拒绝，不使用全局默认卡。请保留这条回复地址，
不要手动改写 `sms2-…` 部分；自定义 PBX 也需要把此类 MESSAGE 原样交回 VoWin。
原 SIM 绑定会校验，旧不完整别名保持只读；即使回复的手机号恰好同本地账号，也不会绕去内部聊天。

直连模式的 SIP 202 表示 VoWin 已完成短信提交，不代表对方手机已收到；提交状态会写入
VoWin 短信历史。经 Asterisk 时，软电话先收到的是 **PBX 接收确认**，更不能把它当成
运营商送达回执；请以 VoWin 短信历史、运营商回执和实际手机接收为准。

## 自定义号码规则

VoWin 的规则按显示顺序取第一条匹配，可设置号码前缀、总长度、是否删除前缀、补充内容
及固定模组。长度 `0` 表示不限。分机号先在内网解析；离线分机不会转为 SIM 外拨。
选线优先级为：显式 `X-VoWin-Line`、分机专属默认线路、兼容号码规则、模组最长选线前缀、全局默认线路。
显式指定线路时，号码本身应已符合该线路的拨号格式。

例如，要保留两个自选前缀，可自行配置：

| 匹配前缀 | 总长度 | 删除前缀 | 补充 | 固定模组 |
| --- | --- | --- | --- | --- |
| `81` | `0` | 是 | `+` | 你的第一条线路 |
| `82` | `0` | 是 | `+` | 你的第二条线路 |

这些不是内置规则。国内补号也由配置决定，不假定每位使用者都有中国卡或固定 COM 口。
不要设置不必要的任意号码兜底；短信规则同样可能产生费用。

## Asterisk 配置生成器

`configure.py` 改为显式 JSON 输入，不再使用固定的两分机、两张卡或 `--cn-line` / `--ph-line`。
先复制 [deployment.example.json](deployment.example.json) 为 `deployment.local.json`，填写
自己的地址、端口、认证账号、分机、目标、模组 ID、拨号规则和可信网段。示例地址仅作格式演示。

```sh
sudo python3 configure.py --config deployment.local.json --output generated
```

生成器只生成文件，不联系 Windows、不重启服务、不安装防火墙，也不拨号 / 发短信：

- `generated/config/`：最小模块清单、PJSIP、语音及可选 MESSAGE 路由。
- `generated/credentials.json`：随机密码；同一目录重新生成时保留已有密码，为新增账号生成密码。
- `generated/vowin-client.json`：导入 VoWin 的含密码部署文件，须私密传递；导入不会立即启用 SIP。
- `generated/vowin-sip-guard.nft`：按 `TrustedNetworks` 生成的限制来源防火墙文件，需人工检查后安装。

`ServerIp` 必须是 VoWin 能访问的具体私网地址；`BindAddress` 是 PBX 监听地址。
只有填写 `TrustedNetworks` 才能生成 `0.0.0.0` 监听配置，且必须先安装来源限制防火墙再启动。
`MessageDomain` 是**软电话能访问的 PBX IP 或 SIP 域名**，不带端口；省略时使用 `ServerIp`，
消息 URI 使用 `SipPort`。LAN 与 VPN 地址不同的部署应明确填写，或使用在两侧均可解析、
可达的域名。`Realm` 是认证标识，不必是可路由域名，不再自动作为消息的回复地址。
转发会保留原发送方显示名和 SIM 回复标识，仅将回复主机改为该配置；每个接收端使用
自己的 To 用户名。旧会话仍可能缓存旧地址，请用升级后新收到的消息验证直接回复，
不必删除已有消息记录。
生成器固定使用示例镜像的 UID / GID `10001` 和 RTP `10000–10099`；这两项是容器约定，
不代表当前使用者的网卡、SIM 或线路。IPv6 PBX 部署需自行扩展模板，Windows 直连可使用私网 IPv6。

`DialRules` 使用受限 Asterisk 数字模式及 `Strip` / `Prepend` / `LineId`。例如你明确需要
中国卡补号时，可自己添加 `_1[3-9]XXXXXXXXX`、`Strip: 0`、`Prepend: "+86"`，并填写实际
中国卡线路 ID。没有配置的号码不进入运营商线路。`MessagesEnabled` 默认关闭；启用时
加载 PJSIP 消息模块，并为每个认证分机设置独立消息上下文，避免客户端伪造消息来显。

## 运行容器与更新

新部署可将本目录的 Dockerfile 和 compose.yaml 复制到 `generated/`，随后运行该目录的
Compose。先确认防火墙文件中的网段包含 Windows / VPN 端，且不包含任意公网来源：

```sh
sudo nft --check -f generated/vowin-sip-guard.nft
sudo nft -f generated/vowin-sip-guard.nft
cp Dockerfile compose.yaml generated/
docker compose -f generated/compose.yaml up -d --build
```

已有服务更新前先备份配置和凭据，确认无活动通话，再比对生成文件。不要直接覆盖已维护的
生产配置；PJSIP 地址 / 端口变化可能需要重新加载或重启服务，软电话也可能需要重新注册。
MESSAGE 开关不自动打开 VoWin 的收短信转发或 SIM 发短信权限，须在软件内分别确认。

Asterisk 来电目标使用所有配置分机的全部可用联系人，首接取消其余分支；语音与消息
上下文相互隔离，消息不会触发 `Dial`。短信转发用
[MessageSend](https://docs.asterisk.org/Asterisk_22_Documentation/API_Documentation/Dialplan_Applications/MessageSend/)，
并通过 [MESSAGE_DATA](https://docs.asterisk.org/Asterisk_22_Documentation/API_Documentation/Dialplan_Functions/MESSAGE_DATA/)
保留选定模组。HTTP / AMI 关闭，容器以非 root、只读文件系统运行。

分机互拨也使用 `PJSIP_DIAL_CONTACTS` 展开该分机的注册联系人，不只尝试第一条旧地址。
没有联系人时直接返回不可用，不回落到 SIM。精简模块清单必须加载 `res_pjsip_nat.so`，
否则仅填写 `rewrite_contact=yes` / `force_rport=yes` 不能完成这些模块负责的地址处理。

## 跨网段地址与公网映射

请求到达同一台 PBX 后，配置中的分机号用于查找注册联系人，不用来电 URI 的主机部分
定位被叫。软电话应将该 PBX 配成固定出站代理，并让呼叫、消息和通话中的 ACK / BYE
都经它路由。仅向同一台服务器 REGISTER，不保证后续请求也经过该服务器；旧会话里的
`sip:分机@旧地址` 仍可能被客户端直接发送到旧地址。不要全局改写 Call-ID、对话标签或
Route 来“忽略地址”，这些字段用于匹配当前通话，而不是查找分机。

当前生成器和防火墙模板面向 LAN / VPN，不自动提供公网 NAT 配置或开放公网来源。
若另行部署公网入口，只有 SIP 端口映射不够。例如外部 UDP `65433` 转入 PBX `5060`，
还需要可达的 UDP RTP `10000–10099`，通常保持端口一一对应。转发目标必须是同一台
承载媒体的 Asterisk，不是 VoWin 的 SIP 端口。

Asterisk transport 还需要按实际拓扑设置 `local_net`、`external_signaling_address`、
`external_signaling_port`（上述例子为 `65433`）和 `external_media_address`，并保持
`direct_media=no`，才能向公网终端宣告可达的信令和媒体地址。`local_net` 只应包括真正
直达的网络；若公网转发器通过 VPN 且做了源地址转换，PBX 可能把公网流量误判为本地，
这时需独立的公网 transport 或能处理 SIP / RTP 的边缘代理，不能盲目套用普通 DNAT 配置。
修改 transport 通常需要在无通话时重启 Asterisk，本机 VoWin 无需因此更换程序。
参见 [Asterisk NAT 配置](https://docs.asterisk.org/Configuration/Channel-Drivers/SIP/Configuring-res_pjsip/Configuring-res_pjsip-to-work-through-NAT/)。

跨网段验收应分别核对主被叫挂断是否传播、两条 RTP 流是否到达、丢包与抖动，不能用
“已注册”或“已接通”代替语音和挂断测试。公网直接暴露 UDP SIP 有扫描和盗打风险；
优先使用 VPN，公网入口应另行配置来源限制、强密码和限速，而非直接放开本模板防火墙。

Linphone 的“正在输入”提示可能使用 `application/im-iscomposing+xml`，Asterisk 对此返回
415 不代表随后独立的 `text/plain` 正文也失败。应核对正文请求的响应。若正文使用 CPIM，
需在客户端关闭该封装，使用基本纯文本消息；当前网关不把这些辅助协议转换成短信。
字段解析及收发行为见 [PJSIP_PARSE_URI](https://docs.asterisk.org/Asterisk_22_Documentation/API_Documentation/Dialplan_Functions/PJSIP_PARSE_URI/)
和 [Asterisk 消息实现](https://github.com/asterisk/asterisk/blob/22/res/res_pjsip_messaging.c)。

## 验证与源码发布

```sh
python3 test_ring_group.py
sudo python3 test_ring_group.py --isolated
```

普通测试检查生成器与不安全输入；隔离测试仅在 Linux Docker 主机使用已存在的本地镜像，
创建 `--network=none` 容器，在其 loopback 网络命名空间中验证语音、来显、群振、选线及
MESSAGE，不接触真实 PBX、VoWin、SIM 或手机。测试中的分机号 / 线路 ID 是合成样例。
隔离测试还覆盖不同 URI 主机、错误自报 Contact 的注册终端、分机多联系人首接取消、
双向 RTP、主被叫分别挂断和 BYE 响应丢失后的重传；它不验证真实公网映射或手机后台行为。

运行前需 Windows .NET 10 Desktop Runtime；客户端未新增 Python、PowerShell 或外置 SIP
服务器依赖。Python 只用于可选 Asterisk 的部署生成与测试。手机号实收、手机后台通知、
运营商计费、跨运营商兼容及多 SIM 实机并发需要在各自环境单独验收。

发布源码不要加入 `credentials.json`、`vowin-client.json`、`deployment.local.json`、生成目录、
DPAPI 文件、数据库、录音及诊断日志。仓库已有相应忽略项；它们不能清除过去提交过的秘密，
推送前仍应检查暂存内容与 Git 历史。本轮未推送源码或发布任何教程到外部网站。
