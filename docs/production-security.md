# 生产安全基线

MoiCalendar 是独立 Blazor WebAssembly 应用，因此浏览器发布产物中的任何值都应视为公开信息。生产浏览器只接收站点基址、Microsoft 公共 Client ID、Supabase Publishable/anon key 和公开的端点路径；服务角色 key、数据库密码、Microsoft client secret、WebDAV 密码及用户 token 不得进入静态文件。

## 数据库边界

- 所有用户拥有或敏感的表均启用 RLS。
- 浏览器不再直接读取或写入 `profiles`、`devices`、`calendars`、`calendar_events` 和 mutation ledger；操作只能经过基于 `auth.uid()` 的 RPC。
- `sync_state` 只向 authenticated 角色授予 SELECT，用作经过 RLS 过滤的 Realtime 唤醒行。Realtime payload 不是同步事实来源。
- 所有 MoiCalendar `SECURITY DEFINER` 函数被固定到 `pg_catalog, pg_temp`，应用对象均使用 schema 限定名；authenticated、anon 和 PUBLIC 不能在 `public` schema 创建对象。
- 内部 helper、历史 mutation 函数和删除限流 RPC 不向浏览器角色开放。

## 静态站点与浏览器

Azure Static Web Apps 仍是唯一 Web 主机。GitHub Actions 在 `dotnet publish` 后为实际生成的内联 import map 和启动脚本计算 SHA-256，并将 CSP 与其他安全头写入发布目录的 `staticwebapp.config.json`。随后验证器确认 CSP、响应头、生产 HTTPS 配置和 PWA 资源完整后才允许上传。

CSP 不允许第三方脚本、对象嵌入或页面被 frame 嵌入。Blazor WebAssembly 运行时需要 `wasm-unsafe-eval`；日历周视图通过受控数值生成内联 CSS 变量，因此 `style-src` 保留 `unsafe-inline`。外部网络连接仅允许 HTTPS/WSS，以兼容 Supabase、Microsoft Graph 和可配置 WebDAV。

## 认证与删除

- Supabase 与 Microsoft redirect allow-list 必须使用文档列出的精确 URL，不使用通配符。
- Supabase 回调地址拒绝 URL 内嵌凭据、查询参数、片段和非本机 HTTP。
- 删除账户从 Auth 验证的 token 推导用户 ID，不读取客户端目标 ID；还要求最近登录和最近签发的当前 access token。
- 删除端点使用精确 CORS origin allow-list、请求大小限制和服务器端持久限流。
- 管理 key 只存在于 Edge Function 环境。

## 凭据与错误

- Supabase session 只保存在浏览器 `sessionStorage`，不进入 IndexedDB、备份、Outbox 或诊断。
- WebDAV 密码只存在于当前运行时设置对象和 Basic Authorization 请求头；URL 内嵌凭据被拒绝。
- OneDrive token 由 MSAL 提供，不进入备份或诊断。
- OneDrive/WebDAV 远端失败响应正文不再拼接到异常。用户界面和本地诊断只显示状态码、通用提示和安全分类。

浏览器中的日历数据仍是可读取的本地用户数据，不能用 CSP 或 RLS 替代设备本身的安全措施。共享设备用户应使用操作系统账户、磁盘加密和浏览器资料隔离，并在离开设备前退出云账户。
