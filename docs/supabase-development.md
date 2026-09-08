# MoiCalendar Supabase 开发与可移植性

MoiCalendar 的日历始终以浏览器 IndexedDB 为本地事实来源。Supabase 提供账户、增量同步 RPC 和可选 Realtime 唤醒；Realtime 不承载权威数据，也不是应用离线运行的前置条件。

本文覆盖两种开发目标：

- Supabase 托管项目。
- Supabase CLI 在本机 Docker 中运行的开发栈。

Supabase CLI 本地栈不是生产自托管部署。它没有针对公网和生产负载进行安全加固，不得直接暴露到互联网。生产自托管应使用正式服务器部署，配置独立密钥、TLS、SMTP、防火墙、备份与灾难恢复、监控、升级和高可用策略。

## 浏览器配置与安全边界

应用读取 Blazor WebAssembly 标准配置：

- `wwwroot/appsettings.json`：平台无关默认值，云后端默认关闭。
- `wwwroot/appsettings.{Environment}.json`：环境覆盖值。

需要的公开值为：

| 配置键 | 说明 | 可进入浏览器 |
| --- | --- | --- |
| `MoiCalendar:CloudBackend:Enabled` | 是否启用云账户与同步 | 是 |
| `MoiCalendar:CloudBackend:BaseUrl` | Supabase API 网关的绝对 HTTP(S) URL | 是 |
| `MoiCalendar:CloudBackend:PublicKey` | `sb_publishable_...` 或旧版 `anon` key | 是 |
| `MoiCalendar:CloudBackend:Supabase:RealtimePath` | Supabase Realtime WebSocket 路径 | 是 |
| `MoiCalendar:PublicBaseUrl` | 注册和密码恢复的应用回调基址；留空时使用当前站点地址 | 是 |

远程/生产 BaseUrl 必须使用 HTTPS；HTTP 只允许 `localhost`、`127.0.0.1` 或 `::1` 这类本地回环开发地址。

`wwwroot`、WASM 程序集及环境配置都可被访问者下载和修改。因此 Publishable/anon key 不是秘密，安全性必须依赖登录用户身份、数据库 grants 和 RLS。

以下内容绝不能写入任何浏览器配置、源码、备份或前端发布产物：

- `sb_secret_...` key。
- `service_role` key/JWT。
- 数据库密码、JWT signing secret、SMTP 密码。
- 用户访问令牌或刷新令牌。
- Microsoft 或 WebDAV 凭据。

配置生成脚本会拒绝已知的 secret、service-role 和管理员 key。应用启动时还会再次验证公开 key。

## 本地 Supabase CLI 开发

### 前置条件

1. 安装并启动 Docker Desktop，或兼容 Docker API 的容器运行时。
2. 安装项目依赖：

   ```powershell
   npm install
   ```

仓库已经包含 `supabase/config.toml` 和全部迁移，不需要再次运行 `supabase init`。

### 启动和生成配置

在仓库根目录依次运行：

```powershell
npm run supabase:start
npm run supabase:status
npm run supabase:reset
npm run cloud:configure:local
npm run supabase:smoke
dotnet run --project .\src\MoiCalendar.App\MoiCalendar.App.csproj --launch-profile local-supabase
```

`cloud:configure:local` 只从 `supabase status --output json` 提取 `API_URL` 和 `PUBLISHABLE_KEY`（旧 CLI 回退到 `ANON_KEY`），生成被 Git 忽略的：

```text
src/MoiCalendar.App/wwwroot/appsettings.Local.json
```

脚本不会把 status 输出中的 secret/service-role、数据库 URL 或 JWT secret 写入应用配置，也不会在终端打印 key。

`supabase:smoke` 具有回环地址安全检查，只会连接本机 CLI 栈。它使用 Publishable key 和临时普通用户验证 Auth、幂等 mutation、结构化冲突、增量 Pull、tombstone、RLS、Realtime 唤醒与漏消息后的游标恢复；不会读取 service-role/secret key。测试数据可通过下一次 `supabase:reset` 清除。

本仓库默认端口由 `supabase/config.toml` 定义：

- API：`http://127.0.0.1:54321`
- Studio：`http://127.0.0.1:54323`
- Mailpit：`http://127.0.0.1:54324`

应以 `npm run supabase:status` 的实际输出为准。如果启用了邮件确认，在 Mailpit 中打开验证邮件；当前本地配置默认不要求注册邮件确认。

停止本地栈：

```powershell
npm run supabase:stop
```

普通 stop 会保留 Docker volume。不要使用删除 volume 的参数，除非确实要销毁本地数据。

### 完整本地验证步骤

1. 完成上述启动、reset、配置生成和应用启动。
2. 在设置页注册一个测试邮箱并登录。
3. 创建日历事件，点击“同步云端”。
4. 修改该事件并再次同步。
5. 删除该事件并同步，确认其他设备不会重新拉回已删除事件。
6. 使用另一浏览器、独立浏览器配置文件或另一设备访问同一应用 URL并登录同一账户；不要用共享同一 IndexedDB 的普通同源标签页模拟第二设备。
7. 在设备 A 修改事件，确认设备 B 收到 Realtime 唤醒并通过增量 Pull 更新。
8. 运行 `npm run supabase:stop`，在离线期间继续编辑本地事件，确认本地修改仍立即可见。
9. 运行 `npm run supabase:start`，让应用回到前台或手动同步，确认 Outbox 重试和游标 Pull 恢复停机期间的全部变化。
10. 检查设置页的冲突状态；陈旧 base revision 必须显示结构化冲突，不能静默覆盖。

## 托管 Supabase 开发

从 Supabase 项目的 Connect 对话框或 API Keys 设置取得：

- Project URL。
- Publishable key（推荐）或旧版 anon key。

不要复制 Secret key 或 service-role key。生成被 Git 忽略的托管开发配置：

```powershell
npm run cloud:configure:managed -- `
  --base-url https://your-project.supabase.co `
  --public-key sb_publishable_replace_me

dotnet run --project .\src\MoiCalendar.App\MoiCalendar.App.csproj --launch-profile managed-supabase
```

这会生成 `src/MoiCalendar.App/wwwroot/appsettings.Managed.json`，不修改通用业务代码或提交的生产默认配置。标准 Supabase 网关使用默认 Realtime 路径；如果兼容网关暴露其他路径，可额外传入：

```powershell
--realtime-path /socket/websocket
```

在托管项目的 Auth URL Configuration 中，将应用实际的 `/settings` URL 加入允许的 Redirect URLs。默认开发地址是 `http://localhost:5262/settings` 和 `https://localhost:7104/settings`；生产地址是 `https://app.moicalendar.com/settings`，生产 Site URL 是 `https://app.moicalendar.com`。完整配置矩阵见 [生产域名与认证回调](authentication-redirects.md)。Auth 的 Site URL/Redirect allow-list、邮件确认策略、SMTP 和密码策略，以及自托管环境中的网关、TLS 与服务密钥，都是服务部署配置，不能通过 PostgreSQL migration 表达；应由各环境的运维配置管理，不能写入浏览器配置或仓库。

应用启动后，重复本地验证中的注册、登录、创建、修改、删除、第二设备、Realtime 和停机恢复步骤。不要使用真实用户数据做开发验证。

### 静态生产产物配置

静态站点运行时仍需浏览器可见的公开配置。部署流程可在发布输出的 `wwwroot/appsettings.Production.json` 中提供 BaseUrl、Publishable key 和可选 RealtimePath；不要把任何 server-side secret 注入静态站点。项目在 Release 构建中排除开发者本机的 `appsettings.Local.json` 和 `appsettings.Managed.json`，防止它们被意外打包。托管平台只负责提供静态文件，不应成为日历、认证或同步逻辑依赖。

## 数据库重现与审计

运行：

```powershell
npm run supabase:reset
npx supabase db lint --local
```

`supabase/migrations/` 当前完整声明：

- `profiles`、`sync_state`、`devices`、`calendars`、`calendar_events`、`calendar_event_mutations` 表。
- UUID 主键、外键、检查约束和查询/修订索引。
- 所有用户表的 RLS、owner policies 和最小 grants。
- 单调 owner revision 分配、触发器和增量高水位。
- `moicalendar_apply_calendar_mutation` 幂等 mutation RPC、base revision 冲突检测和 tombstone 删除。
- `moicalendar_pull_calendar_changes` 基于服务端 revision 的增量 Pull RPC。
- `sync_state` 的 `supabase_realtime` publication 配置。

数据库对象不依赖 Dashboard 手工创建。迁移依赖 Supabase 提供的 Auth schema、`auth.uid()`、Data API 角色和 Realtime publication 约定；这些属于 Supabase 平台依赖，而不是托管 Supabase Cloud 专属依赖。

## 依赖边界

- 托管 Supabase Cloud 依赖：无。域名、project ref、区域和托管平台 API 均未进入业务逻辑。
- Supabase 平台依赖：Auth HTTP API、PostgREST RPC、`auth.uid()`/内置角色、Realtime Phoenix/Postgres Changes 协议及 `supabase_realtime` publication。
- PostgreSQL 依赖：表、UUID、JSONB、RLS、触发器、事务、publication 和 PL/pgSQL 函数。
- Provider 无关层：UI、Core、IndexedDB、Outbox、同步游标/冲突模型、`IAccountService`、`ICloudSyncService` 和 `IRealtimeNotifier`。

从托管 Supabase 切换到本地 Supabase 只需更换环境配置并重新登录，不需要修改 UI、领域模型、IndexedDB 或同步业务逻辑。不同后端账户的用户 ID 不同；现有 cloud binding 会按 Module 17–18 的账户保护规则阻止把一个账户的未确认队列静默绑定到另一个账户。
