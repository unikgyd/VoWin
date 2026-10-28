# Windows SIP 短信持久防重

2026-10-24：将 Linux Go／Rust 的“收费发送先记录未知、不要自动重放”原则接回
Windows 的实际桌面入口。使用既有 SQLite 依赖，不增加运行时、驱动或服务。
这是提交准入保护，不是运营商恰好一次、手机送达或实卡验收。

## 实际接线

桌面每次启动 SIP 服务都会把 `PreferenceDatabaseService` 注入
`SipGatewayOptions.SmsOperationStore`，无需用户再配置开关或路径。
收费短信开启但数据库不提供该能力时拒绝启动；不会临时换成内存防重。
独立分机 MESSAGE 仍不经过 SIM，也不要求收费短信存储。

核心库的普通 `ISipGatewayMessageController` 必须配套
`ISipSmsOperationStore`，否则外发手机短信返回 503，且不调用传输。
已有 `ISipGatewayIdempotentMessageController` 的旧发送入口可以继续自行实现持久准入；
实现接口本身不等于已经兑现该保证。桌面默认控制器由上述实际数据库保护。
仅调用 `VoKernel.StartSipGatewayAsync` 的其他宿主也必须显式注入存储，不能沿用
先前仅内存去重的假设。`SmsOperationStore` 不序列化，启动时重新注入。

## 操作身份与事务

- 操作 ID 沿用 `contracts/sip-sms-operation-v1.json`：规范化 Realm／认证账号、
  大小写敏感 Call-ID、规范十进制 CSeq。网络域名和 Via 分支不改变操作身份。
- V2 内容指纹额外绑定认证角色／权限身份（本地或固定 peer、端点和凭据上下文），
  操作 ID 不变。后续 V3 又绑定原 SIM 摘要；新 bound-send 入口必须有持久存储，不能
  因控制器同时实现旧 idempotent 接口而绕过。 同名本地账号与 peer 不能借用另一角色的付费发送准入；旧 V1／V2 指纹
  重放会拒绝，不会分配新 operation ID 再发送。
- 指纹同时绑定已解析的原线路、最终号码和正文。重启后修改默认线路／补号规则或正文，
  不会把旧请求变成另一条收费发送；同一操作内容不符返回 400。
- `SipSmsOperations` 在 SQLite 写事务中检查、占位并提交 `Unknown`，之后才调用
  短信控制器。两个使用同一数据库的实例不能同时获得同一未过期操作的发送权。
  使用 `synchronous=FULL`，不把后台尚未落盘的内存记录当准入确认。
- 发送后只记录提交结果：已提交、部分提交、失败或未知。重复请求返回既有结果，
  不再次调用控制器，也不重复发布 `SmsSubmitted` 历史事件。
- 传输抛异常／取消、完成结果写入失败时保持未知，返回“结果未知，不要重试”。
  存储损坏、忙、容量满或无法写入则拒绝发送，不当成空库。
- 每次新占位有独立 token，完成写入必须同时匹配 token；旧任务不能完成已过期后
  新建的同 ID／同内容占位。结果写入不延长原保留期限。

表中仅保存操作摘要、内容指纹、结果、到期时间及随机 token，不保存正文、号码、
Call-ID、SIM 身份或 SIP 凭据。数据库仍需按原本的短信隐私要求保护；摘要不等于
磁盘加密。诊断只记失败类别，不导出这些业务内容。

日志连接使用私有 cache，不改变原数据库的 journal 模式或应用的其他连接。
这是为了避免共享 schema 锁耦合；SQLite 官方也不建议新实现使用
[共享缓存模式](https://www.sqlite.org/sharedcache.html)。并发测试中保留了一次旧共享
缓存下的 provider 语句准备异常，不将该堆栈直接归因为已证实的上游缺陷。
内存数据库不能充当持久日志，会拒绝收费准入。

## 明确边界

防重窗口为按系统 UTC 时间计算的 24 小时，最多 2,048 条记录。过期记录可回收，
不是永久 exactly-once；系统时间大幅前跳、删除／更换数据库、恢复不含新记录的旧
备份或客户端主动创建新操作 ID，均不能据此承诺防重。不要通过清空未知记录来
自动重试收费短信。SQLite 锁等待有两秒忙超时，但初始化／同步提交并非即时可取消。

202 表示提交接受，不是手机送达。原阶段不含 SIM 级回复绑定；后续已接入
[原卡回复与来源快照](SIP_SMS_SIM_BINDING.md)，并另接入 Windows
[MT 收件持久转发](SIP_SMS_FORWARDING.md)。两者不新增运营商送达报告能力或不同宿主
之间的物理模组独占；不改变既有选线和权限。
真实固件、收费行为和手机端展示需另行验收。

源码入口：`SipGateway.Messages.cs`、`ISipSmsOperationStore.cs`、
`PreferenceDatabaseService.SipSmsOperations.cs`、`VoKernelService.StartSipGatewayAsync`。
本轮回归和保留的失败证据见
[验证记录](../../publish/sip-durable-cancel-20261024/README.md)。
