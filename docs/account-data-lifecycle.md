# 云账户数据导出与永久删除

MoiCalendar 的设置页通过提供程序无关的 `IAccountDataService` 提供“导出云端数据”和“删除云账户”。Razor UI、Core 和 IndexedDB 模型不依赖 Supabase SDK 类型。Azure Static Web Apps 仍只托管 Blazor WebAssembly 静态文件；需要管理权限的账户删除只在 Supabase Edge Function 中执行。

## 可移植数据导出

`moicalendar_export_account_data()` 从 `auth.uid()` 获取当前账户，只读取该 owner 的资料偏好、日历和日程。返回的版本化 `moicalendar-cloud-export` JSON 包含：

- `format`、`schemaVersion` 和导出时间；
- 用户可见的显示名称、时区；
- 日历及其创建、更新和删除标记；
- 使用现有 `CalendarEvent` 字段形状的日程。

应用把数据库响应反序列化到白名单数据契约，校验后再序列化下载。导出不包含邮箱认证内部信息、密码、访问令牌、刷新令牌、设备、mutation、同步游标、诊断、Microsoft 令牌、WebDAV 凭据或管理凭据。该文件是便携数据副本，不是登录会话，也不会被自动上传到 OneDrive 或 WebDAV。

## 永久删除流程

1. 用户必须已登录，并在设置页看到不可撤销警告。
2. 用户必须准确输入“永久删除我的云账户”；“同时删除此设备上的本地日历”是独立选项，默认不选。
3. Blazor 客户端只把当前 bearer session 发送到 `functions/v1/delete-account`，请求体不包含目标 user ID。
4. Supabase 网关按 `[functions.delete-account] verify_jwt = true` 校验 JWT。函数还调用 Auth `/user` 验证 token，并只从验证后的用户响应取得账户 ID。
5. 函数要求 `last_sign_in_at` 在 10 分钟内。过期时客户端要求退出并重新登录，不会尝试用密码或管理凭据在浏览器中重新认证。
6. 只有函数服务器环境中的 `SUPABASE_SERVICE_ROLE_KEY` 调用 Auth Admin 删除接口。客户端的 Publishable/anon key 不能执行该操作。
7. 删除 `auth.users` 行后，外键 `ON DELETE CASCADE` 在数据库事务中依次清理 `profiles`，再清理 `sync_state`、`devices`、`calendars`、`calendar_events` 和 `calendar_event_mutations`。其他账户的 owner 行不匹配，不会被删除。
8. 服务端成功后，客户端在一个 IndexedDB 事务中清除 cloud binding、sync cursor、entity revision 和全部 SyncOutbox，防止旧 mutation 在未来重新创建已删除的云数据，然后结束登录会话。
9. 默认保留本地日历。如果用户明确勾选本地删除选项，同一事务还清除日历、旧备份同步操作、同步日志和恢复安全快照。

服务端删除对验证后遇到的 Auth 404 按成功处理，以支持并发重试。服务端失败时，本地状态不改变，可以安全重试。云端删除成功后的本地清理是单个 IndexedDB 事务，不会留下“只清了一半”的 outbox/cursor 组合；若浏览器存储本身不可用，应用会退出已删除账户并显示明确错误，旧 token 无法再获得云端授权。

账户删除不会自动删除 OneDrive 或 WebDAV 上的备份，也不会删除用户已经下载到设备的 JSON/ICS 文件。这些文件不由 Supabase 账户唯一且明确地拥有，自动删除会有误删风险。

## 部署与服务端权限

数据库导出函数随 `supabase/migrations/20260910000000_account_data_lifecycle.sql` 重现。删除函数位于 `supabase/functions/delete-account/index.ts`，部署到当前 Supabase 项目：

```powershell
npx supabase functions deploy delete-account
```

托管 Supabase 会在 Edge Function 服务器环境提供项目 URL、anon key 和 service-role key。自托管 Supabase 必须在受信任的函数运行环境提供对应的 `SUPABASE_URL`、`SUPABASE_ANON_KEY` 和 `SUPABASE_SERVICE_ROLE_KEY`，并保持 Auth Admin HTTP API 路径可用。管理 key 只能存在于该服务器环境，绝不能写入 Azure/GitHub 的浏览器配置变量、`appsettings*.json`、Blazor 程序集或日志。

切换托管或自托管 Supabase 仍只需修改 `CloudBackend.BaseUrl` 和公开 key，并在目标后端应用 migrations/部署同一函数；UI、CalendarService、IndexedDB 和同步协议不变。
