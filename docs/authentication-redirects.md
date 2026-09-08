# MoiCalendar 生产域名与认证回调

MoiCalendar 的计划生产入口是 `https://app.moicalendar.com`。域名只通过部署配置提供；应用、日历服务和同步引擎不依赖该主机名，也不依赖 Azure Static Web Apps 的默认域名。

## 浏览器生产配置

在 GitHub repository variable 中设置：

```text
MOICALENDAR_PUBLIC_BASE_URL=https://app.moicalendar.com/
```

生产配置生成器会把公开基址规范化为带尾斜杠的绝对 HTTPS URL。该值会进入浏览器可下载的 `appsettings.Production.json`，因此不得包含用户名、密码、令牌或其他秘密。

## Supabase Auth

在托管 Supabase 的 **Authentication → URL Configuration** 中配置：

```text
Site URL
https://app.moicalendar.com

Redirect URLs
https://app.moicalendar.com/settings
http://localhost:5262/settings
https://localhost:7104/settings
```

注册验证和密码恢复请求都把配置后的 `PublicBaseUrl` 与 `settings` 路由组合为 `redirect_to`。Supabase 返回 `/settings` 后，Blazor 设置页通过账户服务恢复回调会话；URL fragment 中的认证参数在读取后立即从地址栏移除。`/settings` 是客户端路由，Azure Static Web Apps 的 SPA fallback 会在直接打开或刷新时返回 `index.html`。

自托管 Supabase 应在其 Auth 服务的 Site URL 与额外 redirect allow-list 中使用同一组生产值；API 网关地址和 Realtime 路径仍分别由 `CloudBackend.BaseUrl` 与 `CloudBackend:Supabase:RealtimePath` 配置。无需修改 UI、领域模型或同步协议。

## Microsoft Entra 与 OneDrive

在 Microsoft Entra 应用注册的 **Authentication → Single-page application** 中登记以下 Redirect URI：

```text
https://app.moicalendar.com/authentication/login-callback
http://localhost:5262/authentication/login-callback
https://localhost:7104/authentication/login-callback
```

生产运行时会根据 `PublicBaseUrl` 和公开配置项 `MicrosoftAuthentication.RedirectPath` 显式生成 OAuth `redirect_uri`。当前路径为 `authentication/login-callback`，并由 Blazor 的 `RemoteAuthenticatorView` 处理。不要把该 URI 登记为 Web 平台回调，也不要为独立 Blazor WebAssembly 创建或交付 Microsoft client secret。

OneDrive 当前仍临时请求 `Files.ReadWrite` 以兼容个人 OneDrive App Folder 初始化问题；它不是回调配置的一部分。Microsoft 修复后应撤销该权限，仅保留 `Files.ReadWrite.AppFolder`。

## 本地开发

未配置 `MoiCalendar:PublicBaseUrl` 时，应用使用浏览器实际加载的站点基址，因此两个现有 launch profile 都继续可用：

- HTTP：`http://localhost:5262`
- HTTPS：`https://localhost:7104`

Supabase 本地 CLI 的 `site_url` 保持 `http://localhost:5262`，并允许上述两个 `/settings` 回调。使用托管 Supabase 做本地开发时，也要把两个 localhost `/settings` URL 加入该项目的 Redirect URLs。Microsoft Entra SPA 平台则保留两个 localhost `authentication/login-callback` URI。

## 不应配置的地址或秘密

- 不需要把任何 `azurestaticapps.net` 地址硬编码进应用；若暂时通过 Azure 默认域名访问，应将它作为环境专用 allow-list 值管理，而不是提交到业务代码。
- 不要把 Supabase service-role/secret key、数据库密码、Microsoft client secret、WebDAV 密码或用户令牌放入浏览器配置。
- Supabase Publishable/anon key 是唯一允许交付给浏览器的 Supabase key；权限边界仍由用户会话、数据库 grants 和 RLS 提供。
