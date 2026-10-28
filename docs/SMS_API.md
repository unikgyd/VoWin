# 短信与验证码 API

VoWin 提供带 Bearer Token 鉴权的只读 HTTP 接口，供自己的脚本或服务器查询最近收到的短信与验证码。
接口查询本机已保存的收件箱，不访问模组、不发送短信、不删除内容，也不将短信标记为已读。VoWin 必须保持运行。

## 在设置中启用

打开“设置 → 短信与验证码 API”，生成或填写 Token，打开开关后点击“保存并应用 API”。
默认地址为 `http://127.0.0.1:8787/`，仅供本机访问。生成新 Token 不会立即生效；保存后旧 Token 失效，调用端须同步更新。
启用状态会在下次启动时恢复，关闭并保存则停止监听。

Token 为 32–128 位字母、数字、下划线或连字符；“生成新 Token”使用 32 字节随机数。
Token 通过 Windows 当前用户 DPAPI 加密保存于 `%LOCALAPPDATA%\VoWin\sms-api.json`，不与通知机器人共用。
持有此 Token 可以查询所有线路的收件短信，包括验证码；`slotId` 是筛选条件，不是权限边界。不要向不受信任的调用者分发此 Token。

## 调用方式

所有请求使用 `GET`，必须包含请求头：

```http
Authorization: Bearer <在设置中保存的Token>
```

不要将 Token 放到 URL、查询参数、Issue 或截图中。以下 PowerShell 示例假设调用端已在环境变量
`VOWIN_SMS_API_TOKEN` 中配置自己的 Token；示例没有内置凭据。

查询最近短信，并筛选正文关键词：

```powershell
curl.exe -G "http://127.0.0.1:8787/api/v1/sms" `
  -H "Authorization: Bearer $env:VOWIN_SMS_API_TOKEN" `
  --data-urlencode "keyword=登录" `
  --data-urlencode "limit=20"
```

取最近 5 分钟匹配的最新验证码：

```powershell
curl.exe -G "http://127.0.0.1:8787/api/v1/otp" `
  -H "Authorization: Bearer $env:VOWIN_SMS_API_TOKEN" `
  --data-urlencode "keyword=登录" `
  --data-urlencode "maxAgeSeconds=300"
```

## 参数

所有筛选条件同时生效，结果按短信时间从新到旧排序。关键词按正文包含匹配，忽略大小写，不是正则或 SQL；`%`、`_` 等字符按原文匹配。
中文、空格、发件人号码的 `+` 号和含时区的时间参数应做 URL 编码。参数不能重复，未知参数返回 400。

| 参数 | 含义与范围 |
| --- | --- |
| `keyword` | 正文包含的关键词，最多 200 字符 |
| `sender` | 完整发件人精确匹配，最多 100 字符；例如 URL 编码后的 `+12345` |
| `slotId` | 线路 ID 精确匹配，最多 128 字符；省略则查询全部线路，历史未知归属短信也可能出现 |
| `maxAgeSeconds` | 最近多少秒，1–604800；短信列表默认 86400，验证码默认 300 |
| `since` | 含 `Z` 或 `±HH:MM` 时区的 ISO 8601 时间，只可进一步缩小时间范围，不超过服务器当前时间 |
| `limit` | 短信列表返回条数，1–100，默认 20；验证码接口固定为 1 |
| `otpOnly` | 列表传 `true` 时只查识别出验证码的短信；验证码接口始终为 `true` |

时间筛选使用已保存的短信时间，不是请求时间或另行采集的收信时间。最多查询最近 7 天，较早历史请在 VoWin 收件箱查看。

## 返回值

`GET /api/v1/sms` 返回 `items` 数组、`count`、`scanLimited` 和 `generatedAtUtc`。
每条短信只包含 `id`、`slotId`、`sender`、`text`、`timestampUtc`、`code`、`textTruncated`，不返回原始 PDU、IMSI、ICCID 或其他配置。
正文最多输出 4096 字符，超过时 `textTruncated=true`；所有时间以 UTC 返回。

`GET /api/v1/otp` 返回示例（合成数据）：

```json
{
  "found": true,
  "code": "246810",
  "message": {
    "id": "example-message",
    "slotId": "example-line",
    "sender": "EXAMPLE",
    "text": "登录验证码 246810",
    "timestampUtc": "2026-10-19T06:00:00Z",
    "code": "246810",
    "textTruncated": false
  },
  "scanLimited": false,
  "generatedAtUtc": "2026-10-19T06:00:05Z",
  "codeExtraction": "heuristic"
}
```

无匹配时仍返回 200，`found=false`，`code` 和 `message` 为 `null`。重复查询不消费验证码。
验证码沿用本机的 4–8 位数字规则提取，可能有误识别或漏识别，请结合发件人、关键词和时间核对用途，不能把匹配结果视为身份验证成功。
源码自 2026-10-21 起与 SimAdmin 的上下文评分规则对齐，支持全角及分隔数字，降低订单号、金额误识别；裸数字不再自动识别为验证码。已安装的旧程序不因此自动升级。
Linux 主线 VoCat（Go）现也提供可选的独立 Token 服务，设置和安全边界见
[Go Linux 接口说明](../VoCat-sip/docs/SMS_API.md)。仅保存 Token 摘要，默认关闭，
不会自动开启现用实例。Go 与 Windows／Rust 共用查询和验证码输入输出样例，
但 Go 当前的启发式评分细节不保证与 Windows／Rust 对任意文本完全相同。
此前的 Rust 参考 fork 已实现同路径、字段和查询参数的只读服务；两端共用 `contracts/sms-api-query-v1.json`
验证参数边界及 Unicode 关键词匹配。两端源码的正文截断均不切开 emoji；Linux 启用及权限要求见
[Linux 接口说明](../SimAdmin-sip/docs/SMS_API.md)。这不代表 Linux 的 IMS、SIP 或语音媒体已完成。
只查验证码时最多检查 1000 条符合其他条件的候选；达到上限且未取足结果会返回 `scanLimited=true`，应收紧关键词、线路或时间范围后再查询。

错误返回包含 `error` 的 JSON。401 表示 Token 缺失或错误；403 表示访问来源、绑定地址或浏览器 Origin 不允许；
404 表示路径不存在；405 表示不是 GET；400 表示参数无效；429 表示限流；503 表示暂不可用。
单次监听全局每分钟最多 120 次请求（含鉴权失败），429 提供 `Retry-After`；最多并发处理 8 个请求，查询约 5 秒超时。
客户端应处理超时、错误与空结果，不要紧密循环轮询。

## 局域网和公网接入

局域网／VPN 使用具体的本机 IP，例如自己的 `10.x.x.x`、`192.168.x.x`、`172.16–31.x.x`、
CGNAT 或 IPv6 ULA 地址，并明确勾选“允许局域网／VPN 访问”。不接受 `0.0.0.0`、通配符或直接绑定公网 IP。
程序不自动修改防火墙、URL ACL 或端口映射。若监听失败，展开接口用法查看状态灯详情；程序通常由现有管理员清单启动。

公网客户端应先连可信 VPN，或经自行部署的 HTTPS 反向代理到此 HTTP 接口；不要直接将 HTTP 端口映射到公网。
反向代理须透传 `Authorization`，关闭此路径的缓存，并禁止记录该请求头、正文和含敏感关键词的完整 URL；
保留或设置符合具体 IP 监听的上游 Host，不使用任意客户端 Host 作为上游 Host。
接口只接收回环／私网来源，不信任 `X-Forwarded-For` 进行鉴权，也不提供 CORS，带 Origin 的浏览器请求会被拒绝。
需要网页集成时由自己的受信任后端调用，不把共享 Token 下发给网页。

响应携带 `Cache-Control: no-store, private` 与 `nosniff`。VoWin 不记录 API Token、查询关键词或返回的短信正文；
部署反向代理和系统日志的保密责任仍由部署方承担。
