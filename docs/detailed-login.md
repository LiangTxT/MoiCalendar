# 云账户登录错误细分

登录使用 `functions/v1/account-login`；注册、恢复密码及令牌刷新仍使用 Auth 原接口。此功能按用户要求有意暴露邮箱是否注册，不是零账户枚举设计。

## 防护边界

- 函数先校验公开项目 key、来源、内容类型和 8 KiB 流式请求上限。
- 先调用服务端专用数据库限流：每邮箱最多 10 次/15 分钟；全站最多 30 次/分钟、300 次/小时。成功及失败均计数，所有实例共享计数。
- 邮箱限流键为服务端 secret 的 HMAC-SHA256，不保存明文邮箱；旧计数一天后清理。限流不是内存 Map，不可通过实例扩容绕过。
- 不信任客户端转发 IP，不把 Origin 白名单或公开 key 当作防攻击认证；无 Origin 的调用仍受邮箱及全站限流。高流量部署应在可信网关增加 IP 限流、机器人验证，不能仅修改上述额度。
- Auth 真正验证密码。只有 `invalid_credentials` 才调用服务端专用 RPC 细分为未注册、密码错误、账户未设置密码；其余状态不猜测。
- 限流、数据库或认证服务异常时拒绝继续，不回退直连登录或不受限邮箱查询。不记录密码、邮箱、令牌或原始提供程序错误。
- RPC 显式撤销 public/anon/authenticated 权限，仅 service_role 可执行；管理员密钥不进入浏览器。
- 限流可以被攻击者耗尽，邮箱存在性仍可能被慢速探测，这是选择细分提示后的剩余风险。密码恢复继续保持不泄露邮箱存在性的提示。

## 部署前置条件

必须先部署 `20261006000000_detailed_login.sql` 及 `account-login` 函数，再发布新客户端；否则登录入口会不可用。不要对有用户数据的数据库执行 reset。

函数服务端环境变量：

- `SUPABASE_URL`、`SUPABASE_ANON_KEY`、`SUPABASE_SERVICE_ROLE_KEY`：由环境安全管理。
- `MOICALENDAR_ALLOWED_ORIGINS`：逗号分隔的精确应用 origin，HTTPS 或本地 HTTP，不带路径及查询参数。
- `MOICALENDAR_LOGIN_RATE_SECRET`：至少 32 字符的随机 secret，仅服务端保管。
- `MOICALENDAR_LOGIN_PUBLIC_KEY`：可选；浏览器若使用新的 publishable key，设置为同一公开 key；否则默认匹配 `SUPABASE_ANON_KEY`。

登录前无用户 JWT，`supabase/config.toml` 的 `functions.account-login.verify_jwt=false` 仅适用于此入口；不得修改其他函数的认证要求。部署操作另行确认，不在本次代码实现中自动执行。

验证：`node --test scripts/tests/account-login.test.mjs`、`dotnet test`、`dotnet build`。本地/预生产环境额外验证 SQL 迁移、匿名 RPC 禁止访问、并发 11 次同邮箱请求第 11 次被拒绝、不同邮箱全站限流、注册/未注册/错误密码/未验证邮箱及成功登录。使用专门的测试账户，不操作用户真实账户。
