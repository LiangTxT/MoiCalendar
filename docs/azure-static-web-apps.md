# MoiCalendar Azure Static Web Apps 生产部署

MoiCalendar 是独立 Blazor WebAssembly PWA。Azure Static Web Apps 只托管 `dotnet publish` 产生的静态文件；应用不使用 Azure Functions、Static Web Apps 身份验证或其他 Azure 运行时 API。IndexedDB 仍是客户端事实来源，Supabase 是主云同步后端，OneDrive 和 WebDAV 保持可选备份边界。

## 现有部署资源与流程

仓库沿用现有工作流 `.github/workflows/azure-static-web-apps-polite-rock-09eddaf00.yml` 和对应的 Azure Static Web Apps 资源，没有创建第二种托管架构。

旧版 OneDrive 部署已经具备：

- `main` push 和 pull request preview 生命周期。
- GitHub secret `AZURE_STATIC_WEB_APPS_API_TOKEN_POLITE_ROCK_09EDDAF00` 作为 Azure 部署凭据。
- .NET 10 setup、Release 测试与发布。
- `staticwebapp.config.json` 的 Blazor SPA fallback。
- `appsettings.json` 中公开的 Microsoft authority、client ID 和回调路径；OneDrive 令牌仍由浏览器登录流程取得，不进入生产配置文件。

Module 21 保留上述资源和 OneDrive 行为，但让工作流只上传显式 `dotnet publish` 的输出，避免 Azure Action 再次从源码构建另一份产物。

Azure 的正式部署分支是 `main`。功能分支（例如本地当前使用的 `feature/cloud-sync-v2`）不会因普通 push 部署到生产；合并进入 `main` 后，现有工作流才会执行生产上传。Pull request 仍沿用现有 Azure preview 生命周期。

## GitHub Variables

在 GitHub 仓库的 **Settings → Secrets and variables → Actions → Variables** 中配置：

| Variable | 必需性 | 浏览器可见 | 内容 |
| --- | --- | --- | --- |
| `MOICALENDAR_PUBLIC_BASE_URL` | 必需 | 是 | 生产站点 HTTPS 根 URL；当前应为 `https://app.moicalendar.com/` |
| `MOICALENDAR_CLOUD_ENABLED` | 必需 | 是 | `true` 启用 Supabase；`false` 保持完整本地模式 |
| `MOICALENDAR_CLOUD_BASE_URL` | 云启用时必需 | 是 | 托管或自托管 Supabase HTTPS 网关 URL |
| `MOICALENDAR_CLOUD_PUBLIC_KEY` | 云启用时必需 | 是 | Supabase Publishable key 或旧版 anon key |
| `MOICALENDAR_SUPABASE_REALTIME_PATH` | 可选 | 是 | 默认为 `/realtime/v1/websocket`；兼容网关可覆盖 |

Publishable/anon key 的设计用途就是交付给浏览器，它不是授权边界；数据访问安全依赖登录会话、数据库 grants 和 RLS。即使组织策略把它保存为 GitHub Secret，它仍会出现在最终静态 JSON 中，不能被视为秘密。

以下值不得配置到上述变量、`wwwroot` 或发布产物：

- Supabase `service_role`、Secret key、数据库密码和 JWT signing secret。
- Microsoft client secret。
- WebDAV 密码。
- 用户访问令牌或刷新令牌。

`AZURE_STATIC_WEB_APPS_API_TOKEN_POLITE_ROCK_09EDDAF00` 必须继续作为 GitHub Actions Secret 保存。它只由部署 Action 使用，不会写入 Blazor 发布产物。

Azure Static Web Apps 门户中的 Application settings 面向服务端/API 场景，不能假设它们会自动变成独立 Blazor WebAssembly 的客户端配置。本仓库在 `dotnet publish` 前显式生成 `wwwroot/appsettings.Production.json`，再上传已发布目录。

## 生产构建数据流

```text
GitHub repository variables
  → cloud:configure:production
  → wwwroot/appsettings.Production.json
  → dotnet publish（Production 环境）
  → deploy:validate（拒绝 localhost、错误环境文件或不完整 PWA 产物）
  → bin/Release/net10.0/publish/wwwroot
  → 现有 Azure Static Web Apps 资源
  → 浏览器公开读取配置
```

生成脚本会：

- 严格解析 `MOICALENDAR_CLOUD_ENABLED`。
- 要求生产 PublicBaseUrl 和已启用的云后端都使用 HTTPS。
- 拒绝已知 service-role、secret 和管理员 key/JWT。
- 云关闭时把 BaseUrl/PublicKey 写为 `null`，继续提供本地日历。
- 不打印 PublicKey。

`appsettings.Production.json` 被 Git 忽略。Release 项目配置仍排除开发者的 `appsettings.Local.json` 和 `appsettings.Managed.json`，但会包含工作流刚生成的 Production 文件。

工作流使用的精确路径为：

- 构建输入：`src/MoiCalendar.App/MoiCalendar.App.csproj`。
- `dotnet publish` 默认输出根：`src/MoiCalendar.App/bin/Release/net10.0/publish/`。
- Azure `app_location`：`src/MoiCalendar.App/bin/Release/net10.0/publish/wwwroot`。
- Azure `output_location`：空字符串；`skip_app_build: true`，避免 Azure 再构建一次源码。

上传前验证要求生产云同步已启用、BaseUrl 为非回环 HTTPS 地址、Production 配置已进入 Service Worker 资源清单，并确认 Local/Managed 配置没有混入产物。任何一项不满足都会在 Azure 上传前失败。

## 外部控制台配置

这些项目不是 PostgreSQL migration，也不能由静态站点配置代替：

1. Supabase Auth：Site URL 设置为 `https://app.moicalendar.com`，Redirect URLs 至少包含 `https://app.moicalendar.com/settings`。
2. Microsoft Entra：将 `https://app.moicalendar.com/authentication/login-callback` 注册为 SPA redirect URI；当前临时 `Files.ReadWrite` 权限应在 Microsoft 修复 App Folder 问题后移除，只保留 `Files.ReadWrite.AppFolder`。
3. Azure Static Web Apps：生产域名和 HTTPS 证书必须有效。

生产与 localhost 的完整回调矩阵见 [生产域名与认证回调](authentication-redirects.md)。

## 路由与 PWA

`staticwebapp.config.json` 将不存在的客户端路由回退到 `/index.html`，因此 `/settings`、`/sync-status` 和 `/authentication/...` 可直接打开或刷新。框架、组件、样式和带静态扩展名的缺失资源排除在 fallback 之外，避免错误地以 HTML 响应资源请求。

Service Worker 会把 Production 配置纳入发布资源清单；新部署改变文件 hash 后会生成新的缓存版本。离线启动仍使用已缓存应用和 IndexedDB，Supabase 不可用不会阻止本地日历读写。

## 本地复现生产发布

仅使用测试用公开值：

```powershell
$env:MOICALENDAR_PUBLIC_BASE_URL = "https://calendar.example.test/"
$env:MOICALENDAR_CLOUD_ENABLED = "true"
$env:MOICALENDAR_CLOUD_BASE_URL = "https://backend.example.test/"
$env:MOICALENDAR_CLOUD_PUBLIC_KEY = "sb_publishable_example"
npm run cloud:configure:production
dotnet publish .\src\MoiCalendar.App\MoiCalendar.App.csproj -c Release
```

检查发布目录后删除本机生成的 `appsettings.Production.json`。不要使用真实生产 key 做本地验证。

本地 Supabase 开发继续使用 `cloud:configure:local` 和 `local-supabase` launch profile，详见 [Supabase 开发与可移植性](supabase-development.md)。
