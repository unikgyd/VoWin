# 可选 Asterisk 分机管理页

独立的 Go HTTP 管理服务与静态网页，不是 FreePBX，不替代 VoWin／VoCat 的内部 SIP Server。
面向 `packaging/asterisk/configure.py` 生成的 Docker PBX。服务器需要 Docker CLI 与
Python 3；Go 仅构建时需要，前端无外部运行时或 CDN 依赖。v2 私密草稿存储要求原生 Linux，
不是 Windows ACL 存储实现，也不需要 WSL。

可添加／编辑／移除分机，配置启停、名称、随机密码、终端数量、外线群振、运营商外呼与
消息路由权限；查看真实 Contact 状态、显式获取软电话连接信息。
分机号为 2–8 位数字，不得占用服务号码。非数字分机部署不能直接用本管理器接管。

## 先选择管理模式

| 启动方式 | 编辑对象 | 保存行为 |
| --- | --- | --- |
| 不带 `-v2-store` | 原有旧版部署及凭据 | 原有备份、安装和热加载流程；拒绝 v2 文件 |
| 显式带 `-v2-store` | 单独初始化的 v2 私密草稿 | 仅保存草稿，`canApply: false`，不安装、不应用、不重载 PBX |
| 再显式开启 `-v2-application` | 同一草稿＋固定 PBX 目标 | 保存仍只写草稿；独立预检／应用／恢复需要另外确认 |

两种模式不自动转换，也不从旧共享密码、PJSIP 文件或已有账号猜测 v2 `SecretRef`。
网页按服务器明确返回的模式使用对应 API，不因 v2 草稿故障回落到旧版写入。
本轮实现及验收边界见[管理 v2 阶段记录](../../../publish/asterisk-admin-v2-20261024/README.md)。

## v2：初始化独立私密草稿

准备完整的公开 v2 部署 JSON，以及独立的 `SecretRef → 密码` JSON map；map 的键必须
精确覆盖全部已声明引用，包括停用角色，不能使用账号名代替引用。密码为 12–128 位
ASCII 字母、数字、`_` 或 `-`。公开模型不嵌入密码，但含部署信息，也应妥善保管。
旧 `deployment.example.json` 不是 v2 初始化模板；模型格式见 [deployment_v2.py](../deployment_v2.py)
及[生成器说明](../../../publish/asterisk-render-v2-20261024/README.md)。

以下路径均为示例，请改为自己的绝对路径，不要复制真实凭据到命令行或日志：

```sh
python3 /opt/vowin-tools/asterisk/admin_deployment_v2.py \
  --action init \
  --store /var/lib/vowin-pbx-admin/draft-v2 \
  --public /root/vowin-private/deployment-v2.public.json \
  --secrets /root/vowin-private/deployment-v2.secrets.json < /dev/null
```

运行前须自行准备受控父目录与输入文件：`--store` 的父目录已经存在，但目标
`draft-v2` **必须不存在**，已有目录（即使为空）不会被覆盖。使用私有独立目录，其祖先应由
root 或运行账号拥有，不可被组／其他用户写入；路径各组件不能是符号链接。秘密输入必须
是当前有效用户拥有、权限恰为 `0600` 的普通文件；不要使用 FIFO、设备或链接。
`< /dev/null` 表示初始化没有标准输入请求正文。

初始化原子发布新的私密 store，目录 `0700`、文件 `0600`，保留完整模型和秘密 map；
失败可能留下私有初始化暂存目录供检查，不会自动接管已有目录。其权限保护不是加密，
有效用户／root 仍能读取秘密。不要把 store、旧 revision、暂存目录或输入秘密文件提交源码、
加入公开镜像／压缩包，或作为普通诊断日志上传。

## v2：启动管理服务

在本 `admin/` 目录构建程序，将完整的配套 Python 源文件部署到可信工具目录。至少确保
`admin_deployment_v2.py`、`admin_v2_store.py`、`deployment_v2.py` 与 `configure.py` 同目录；
Go 通过 `-generator` 的父目录定位 helper，不能只更新一个 `configure.py`。

```sh
go build -trimpath -ldflags='-s -w' -o pbx-admin .
./pbx-admin -base /opt/vowin-asterisk \
  -generator /opt/vowin-tools/asterisk/configure.py \
  -container vowin-asterisk -listen 127.0.0.1:9088 \
  -v2-store /var/lib/vowin-pbx-admin/draft-v2
```

`-base` 仍是现有部署目录，用于管理密钥与真实 PBX 状态；`-v2-store` 是另一个已经显式初始化的
绝对目录，不能与 `-base` 相同，也不能互为子目录。启动会校验草稿可读，但不会安装、应用或
重载。管理服务应以初始化 store 的同一有效用户运行。Go 标准 flag 同样接受 `--v2-store`；
这里统一使用单横杠写法。下方私网绑定、管理密钥与登录限制同样适用于 v2。

### v2 分机与保存边界

- 新分机的账号启用、群振和四项权限全部默认关闭，终端上限为 `4`；没有预置密码。
  可保留零个分机。已保存的分机号只读，暂不支持改号。
- 四项权限分别是**运营商外呼、分机内部文本消息、发送手机短信、接收手机短信**，互不代替。
  全局开关关闭时仍可编辑相应草稿权限，但不会因此打开全局能力；网页会显示未生效提示。
- 密码处理明确选择「保留」「填写新密码」「保存时由服务器生成」。新分机默认等待填写密码，
  必须填写或显式选择服务端生成；不会在浏览器自动生成。保留只适用于已有凭据的原账号。
  成功保存后清除填写的明文，普通列表不返回密码、秘密引用或哈希。
- 「保存草稿」只提交私密原子 revision，不改正在运行的 Asterisk、网关配置、模组、线路或
  现用凭据。草稿 API 始终为 `saved-not-applied`，`canApply: false`；可选应用入口使用独立的能力与记录。
  已保存不代表已经部署，不能据此声称生产迁移完成。
- 在线状态来自真实 Contact 查询，不由草稿的启用／停用推断。选中已有账号后可显式获取
  **上次保存草稿**的连接信息；其中含密码，但连接信息本身不是独立应用记录，不能当作已生效
  或注册成功的证明。
  未保存的新密码不包含在该连接信息中。
- 并发冲突返回 `409`，保留本地草稿，确认后才重新加载。`503` 可能表示提交结果不确定，
  **不要自动重试或直接重复保存**；先重新读取已保存 revision，再核对和决定后续操作。
  中断后不自动提升旧 revision、不伪称回滚。会话失效 `401` 或退出管理时，网页清除密码草稿
  和私密连接弹窗；它不能清除已经复制到系统剪贴板或交给其他终端的密码。

## v2：可选网页预检、应用与恢复

默认关闭。仅有 `-v2-store` 时不会启动应用器。完成下方 CLI 的目标权限／镜像审核后，
可在原启动命令后显式追加：

```sh
  -v2-application \
  -v2-image "$IMAGE_ID" \
  -v2-restart-policy unless-stopped \
  -confirm-v2-management
```

`IMAGE_ID` 是管理员实际审核的本地完整 `sha256:…` 镜像 ID，不是 tag。这些选项不接收网页
提供的路径、容器、镜像或命令。服务须以 root 运行；完整 helper 与目标要求仍见下方。
`-confirm-v2-management` 允许预检建立永久 v2-only 标记，**不代替网页每次应用的重启确认**。

- 保存、预检、应用、恢复分别操作；预检不重启目标，但会实际启动隔离候选容器。
- 应用／恢复会提示中断通话与短暂重启。关闭页面或退出管理不会取消已经接受的后台任务。
- 请求先以随机 request ID 持久排队，再返回 `202`。同一 ID／同一请求只附着原任务，
  同一 ID 的其他内容返回 `409`。丢失提交响应时先刷新查询，不自动重发。
- 任务使用独立进程租约，Python 子进程继承租约；服务退出不取消正在修改 PBX 的子进程。
  服务重启后不重放遗留任务，只在读取到精确匹配的终态记录时核对结果；否则保留待确认，
  需要人工检查后显式恢复。其他进程仍持租约时只读启动，不把未验证草稿视为有效。
- `.v2-http-jobs/` 为私有任务账本，最多保留 256 个请求、总文件上限 1 MiB；不按 TTL 删除
  幂等绑定，容量满会拒绝新请求。不要删账本来重试未知任务，目前没有自动清理入口。
- 「最后验证的草稿」来自独立 installed 记录与现用 20 文件指纹核对，不是实时健康检查。
  预检新草稿不会清掉旧已安装记录；回滚保留原版本，外部修改显示红色并拒绝覆盖。
  终态与索引未对齐时显示待核对，只允许核对原操作，不用新预检掩盖它。
- 网关与防火墙仍不自动启用；网页的绿色 PBX 应用记录不代表 VoWin／VoCat 或运营商验收。

实现与软件／隔离验收证据见[网页应用阶段记录](../../../publish/asterisk-web-apply-v2-20261024/README.md)。
管理页只放可信内网／VPN；原认证、来源校验、CSRF 与私网访问限制同样适用，不应映射公网。

## v2：独立受控应用 CLI

[`admin_apply_v2.py`](../admin_apply_v2.py) 是独立的原生 Linux／root 维护入口；可选网页应用器
调用同一受控入口。草稿 API 的 `canApply: false` 不变，不把保存当作部署。
CLI 复用前面已经初始化的 store，不自动迁移旧共享密码。实现、实际验证与未验收
边界见[受控应用阶段记录](../../../publish/asterisk-apply-v2-20261024/README.md)。

应部署完整配套 Python 模块，不能只复制入口文件：包括 `admin_apply_v2.py`、
`admin_v2_apply.py`、`admin_v2_docker.py`、`admin_deployment_v2.py`、`admin_v2_store.py`、
`deployment_v2.py`、`deployment_bundle_v2.py` 和 `render_*_v2.py` 等支持模块，保持同目录。

### 接管前提与权限

- `--base` 为已有的独立私有部署目录，root 拥有、权限 `0700`，不得与 store 相同或嵌套。
  不通过符号链接访问，不让其他工具绕过固定锁同时写受管文件。
- `config/` 必须由 `10001:10001` 拥有，目录权限为 `0700` 或合法旧配方的 `0750`。
  已有受管配置文件若存在，应由 `10001:10001` 拥有，权限 `0600` 或 `0640`；三个根目录
  导出文件由 root 拥有、权限 `0600`。候选包含完整 17 个 `.conf` 加 3 个导出文件，共 20 个。
  原先缺失的受管文件也被记录，恢复时保持缺失；`pjsip.conf`／`modules.conf` 必须已有。
- 明确指定一个已存在的受控容器名称，以及管理员已经审核、**本地可用**的不可变
  `sha256:…` 镜像 ID，不接受镜像 tag 或自动拉取。目标必须是已知配方：用户
  `10001:10001`、非 privileged、只读根文件系统、`cap_drop: ALL`、no-new-privileges，
  network 为 `host` 或隔离测试使用的 `none`，无设备或 Docker socket。
- `/etc/asterisk` 必须唯一只读 bind 到准确的 `--base/config`，无配置父／子路径覆盖；
  除既定运行目录的 volume／tmpfs 外不接受额外挂载。当前模块路径针对 x86_64；
  不支持的模块路径或算法能力会拒绝，不能把此流程当作 ARM64 PBX 支持或算法降级。

`prepare` 要求显式 `--confirm-v2-management`：进入 v2 接管后会保留永久的
`.admin-operation-lock/v2-managed.json`，禁止旧版四文件应用；**即使后续预检失败也不自动
回退旧模式**。不要按旧版“遗留锁”处理方式删除该 guard，或另起旧生成器覆盖现用配置。

预检会真正启动一个自有、network-none 的候选容器，使用独立私密配置与 tmpfs，不复用目标
可写卷、不发布端口、不拉镜像、不接触模组／真实电话／短信；完成后只清理已验证身份的自有容器。
这证明配置能被指定镜像加载，不证明实际网络、网关或运营商链路可用。

### 三个修改动作与两个只读入口

下面均为路径示例。每条命令必须以空标准输入／EOF 运行（例如 `< /dev/null`）；不向 stdin
发送秘密或请求 JSON。`status` **只接受** `--action status --base …`，不要附带其他动作的参数。
`overview` 同样只接受 `--action overview --base …`，非阻塞返回当前操作与 installed 指纹记录；
锁被占用时返回 busy／unconfirmed，不读取一半的日志、不补写状态，也不查询 Docker 健康。

先用已有 store 的读取入口取得当前 `value.revision`：

```sh
python3 /opt/vowin-tools/asterisk/admin_deployment_v2.py \
  --action read --store /var/lib/vowin-pbx-admin/draft-v2 < /dev/null
```

将输出中实际的 `value.revision` 原样记为 shell 变量 `REVISION`，将已审核的本地完整镜像 ID
记为 `IMAGE_ID`。下面使用这些变量而不提供伪造 token；不要填入样例字符或自行生成 revision。

```sh
python3 /opt/vowin-tools/asterisk/admin_apply_v2.py \
  --action prepare --base /opt/vowin-asterisk \
  --store /var/lib/vowin-pbx-admin/draft-v2 --container vowin-asterisk \
  --image "$IMAGE_ID" --restart-policy unless-stopped \
  --revision "$REVISION" --confirm-v2-management < /dev/null
```

仅在返回 `ok: true`、`value.state: "prepared"` 后继续。把**此输出**的 `value.operation` 与
`value.revision` 分别记为 `OPERATION`、`REVISION`；两者都是 43 位 URL-safe 标识，必须取自输出，
不能从路径、哈希或旧操作猜测。核对目标、`fileCount` 与 `changedFiles`，安排无通话维护窗口，
确认会短暂中断 PBX、终端可能需要重新注册，然后显式应用：

```sh
python3 /opt/vowin-tools/asterisk/admin_apply_v2.py \
  --action apply --base /opt/vowin-asterisk \
  --store /var/lib/vowin-pbx-admin/draft-v2 --container vowin-asterisk \
  --image "$IMAGE_ID" --restart-policy unless-stopped \
  --revision "$REVISION" --operation "$OPERATION" --confirm-restart < /dev/null
```

`--restart-policy` 只允许 `no` 或 `unless-stopped`，省略时默认后者；预检时必须与目标实际策略
匹配，后续 apply／recover 沿用同一个声明策略。停止期间临时变成 `no`，不是让你把恢复参数
改成 `no` 的理由。apply 不带 `--confirm-v2-management`，recover 不带 `--revision`；CLI 拒绝
多余、重复或不匹配动作的参数。

查询 journal，以及在明确需要时请求恢复：

```sh
python3 /opt/vowin-tools/asterisk/admin_apply_v2.py \
  --action status --base /opt/vowin-asterisk < /dev/null

python3 /opt/vowin-tools/asterisk/admin_apply_v2.py \
  --action recover --base /opt/vowin-asterisk \
  --store /var/lib/vowin-pbx-admin/draft-v2 --container vowin-asterisk \
  --image "$IMAGE_ID" --restart-policy unless-stopped \
  --operation "$OPERATION" --confirm-restart < /dev/null
```

恢复使用该操作留存的原始快照，不使用最新草稿；即使新草稿已改变，或候选文件损坏，仍只依据
可信原快照恢复。原快照／journal 损坏则拒绝，不猜测原内容。若现用文件不再符合该操作记录的
原始或候选指纹，即第三种外部修改状态，也拒绝覆盖，需先人工检查。

### 应用、恢复和结果范围

应用保持现有容器身份与 `config/` 目录 inode，先检查空闲、临时关闭自动重启，再发出
`core stop gracefully` 阻止新呼叫并等待已有通话退出；不对目标使用超时强杀。
确认停止后逐文件安装全部 20 个产物，启动并验证实际配置、模块、认证算法、端点、transport
与 dialplan，再恢复声明的重启策略。它不是旧版四文件热加载，也不会删除／重建目标容器。

失败时若可安全恢复，会恢复全部原始受管文件及其存在／缺失状态、权限与所有者，重新启动并
验证原 PBX。未确认停机、异常中断或结果不确定时可能留下 `stopping`、`installing`、`starting`
或 `recovery-required` journal，
**不要重放 apply 或自动重试**；先读 `status`，核对记录后显式 recover。恢复本身失败也保持待恢复，
不把“尝试恢复”写成已成功。不要手工提升旧 revision、删除 journal／guard 或覆盖外部修改。

`statusSource: "journal"` 只说明记录来源，不是即时健康检查：`applied`／`pbxApplied: true`
记录该次 PBX 验证成功，不保证之后仍在线。`rolled-back` 仅表示该操作原始 PBX 已恢复并验证，
不是任意历史版本切换功能。journal 及其原始／候选快照均含秘密，应保持私密，不自动清理或公开上传。

其余三个文件即使已安装，也只是产物：`gateway-client.json` **没有被导入网关**，
`sip-guard.nft` **没有被加载到防火墙**，`deployment.normalized.json` 不是全系统生效证明。
结果中的 `gatewayApplied`、`firewallApplied` 仍为 `false`；没有外部证据就不能宣称网关或防火墙
已应用。网关导出内容没有变化时，不强制重复导入；变化时须走网关自身的受控导入与启用流程。
网关 profile 本来生成的是停用状态，PBX 成功不能代替真实手机通话、短信、网络和 SIM 的全链路验收。

下面的「旧模式」章节仅描述原有热加载工作流，不适用于 v2 草稿保存。

## 旧模式：启动

先备份部署，并将本版本 `configure.py` 更新到服务器。不要对任意手写 PBX 配置直接使用。

```sh
go build -trimpath -ldflags='-s -w' -o pbx-admin .
./pbx-admin -base /opt/vowin-asterisk -generator /opt/vowin-asterisk/configure.py \
  -container vowin-asterisk -listen 127.0.0.1:9088
```

两个路径需为绝对路径。部署目录应含 `deployment.local.json`、`credentials.json` 与现有
`config/`。启动不修改或重载 PBX。首次启动生成随机密钥 `admin-access.local.txt`，权限
0600，不打印到日志。本项目忽略该文件；不要提交、公开或打包。登录使用 HttpOnly／
SameSite Cookie、CSRF Token 与同源校验，密钥不进入 URL 或浏览器持久存储。

默认只监听回环；可以通过 SSH 隧道接入。systemd 示例在 `pbx-admin.service.example`，
可在服务器私有 `admin/listen.local.env` 中配置 `PBX_ADMIN_LISTEN=具体私网IP:端口`。
禁止通配或公网绑定；HTTP 没有链路加密，仅用于可信内网／VPN，不能直接映射公网。
需要公网管理时应另配受保护的 HTTPS 入口。本工具不自动改防火墙或远程开放 Docker socket。

当前示例以 root 运行，以访问私有配置和 Docker CLI，并安装 UID/GID 10001 的 PBX 文件。
10001 是本项目镜像约定，不是通用 PBX 假设。不能将这个管理 API 当作低权限公网站点。

## 旧模式：分机行为

- 旧账号导入时保留密码、原终端上限与已有权限。新账号没有预置密码；外呼、消息／短信
  默认关闭，须显式启用。账号启用、接收群振默认勾选；`Ring` 与短信收件权限独立。
- 停用时移除 Endpoint／Auth／AOR，但保留凭据供重启用；精准号码拒绝路由保留，避免
  掉进手机号码规则。移除分机同时删除现用凭据，私有备份仍可恢复。
- 群振使用分机全部 AOR Contact，首接后取消其他端。原 NAT、RTP 锚定和短信回复域规则
  沿用生成器；不会主动拨号、发送短信、切换模组或改 VoWin 现用文件。
- 普通列表不含密码，登录后显式点「连接信息」才返回。改号须设置新密码。私网 HTTP 下
  剪贴板可能不可用，此时支持手动选中复制。不要把连接信息交给不可信终端。

与 VoCat 协议栈独立，无跨许可协议代码复制或授权绕过；分机注册成功不证明运营商通话可用。

## 旧模式：保存和恢复

保存前检查版本、输入与活跃通话。候选配置先生成到私有目录，提交前再次检测外部编辑。
只安装 PJSIP、dialplan、凭据与部署 JSON，不改 RTP、防火墙、模块清单、HTTP／AMI。
使用 `module reload res_pjsip.so` 与 `dialplan reload` 热加载，不重启容器，并核对真实
Endpoint 的启用／停用／移除。精简镜像没有 `pjsip reload` 快捷别名。

每次操作保留 `admin-backup-*/original/` 四份原文件，父目录权限 0700。失败恢复文件并
重载；回滚也失败时明确报告，不伪称成功。备份含密码，不可公开上传，暂不自动删除。
这不是断电或磁盘故障下的数据库事务。不要用别的工具同时改受管理文件。异常中断留下
`.admin-operation-lock` 时，先检查操作目录、`outcome.txt` 和真实 PBX 状态，恢复并重载后
再解除锁，不应直接忽略不确定状态。**这仅指旧模式的异常锁，不适用于含 `v2-managed.json`
的永久 v2 guard；后者不能按此方法解除。** 停止管理服务本身不停止 PBX 或改分机。
轮换密钥需先停管理服务、安全替换密钥文件再启动；SIP 密码不变，旧管理会话失效。

## 验证

Go 目录运行 `go test ./...`、`go test -race ./...`；父目录运行：

```sh
python3 -m unittest test_ring_group.GeneratorTests test_probe_message test_admin_options -v
```

`TestLive*` 仅在显式设置 `PBX_ADMIN_LIVE=1`、`PBX_ADMIN_BASE`、`PBX_ADMIN_URL` 与
`PBX_ADMIN_ORIGINAL_BACKUP` 后运行。它创建无外呼／消息／群振权限的临时分机，验证
SIP REGISTER、状态、停用及清理；不发送 INVITE／MESSAGE。另可在独立 network-none
容器运行 `test_ring_group.py --isolated`，用合成终端验证通话／消息，不联系现网。
`node test/preview.mjs` 是合成 UI 预览，不连接 PBX；加 `--fail-save` 测试失败草稿。
v2 纯模型测试为 `node --test test/v2-model.test.mjs`；新隔离预览为
`node test/preview-v2.mjs --port 5189`，只绑定回环、使用合成账号，不连接真实 store／PBX／SIM。
可用 `?scenario=empty`、`all-off`、`conflict`、`invalid`、`expire-on-status` 等场景检查交互；
预览成功不代表真实配置已部署或通话、短信已验收。最终结果以阶段记录为准。

配置依据：[PJSIP 对象关系](https://docs.asterisk.org/Configuration/Channel-Drivers/SIP/Configuring-res_pjsip/PJSIP-Configuration-Sections-and-Relationships/)、
[全部 AOR Contact 拨号](https://docs.asterisk.org/Configuration/Channel-Drivers/SIP/Configuring-res_pjsip/Dialing-PJSIP-Channels/)。
