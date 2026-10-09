# MoiCalendar 通用静态托管说明

MoiCalendar 是独立 Blazor WebAssembly PWA。应用发布后只需要静态文件服务器，不依赖 Azure、Cloudflare、Netlify 或任何指定平台的运行时服务。

## 发布与上传

在仓库根目录运行：

```powershell
dotnet publish .\src\MoiCalendar.App\MoiCalendar.App.csproj -c Release
```

发布结果位于：

```text
src/MoiCalendar.App/bin/Release/net10.0/publish/wwwroot/
```

把该 `wwwroot` 目录中的全部内容上传到静态站点的发布根目录。不要只选择其中的 HTML 文件，也不要遗漏 `_framework`、`_content`、Service Worker、清单、图标或压缩资源。

## 静态服务器要求

静态托管平台需要负责：

- 使用 HTTPS 对公网提供服务，localhost 本地开发除外。
- 把 `index.html` 设为默认文档。
- 对不存在的非文件路径回退到 `index.html`，使 Blazor 客户端路由可以处理直接访问和刷新。
- 正确提供 `.wasm`、`.js`、`.json`、`.webmanifest`、字体和压缩文件的 MIME 类型及内容编码。
- 允许 `service-worker.js` 及时重新验证，避免长期缓存旧 Service Worker。
- 对应用入口、Worker、资源清单与非指纹静态文件设置 `Cache-Control: no-cache`（允许缓存，但每次联网使用前重新验证）。Azure 配置已包含该头；其他主机需要在自身响应头配置中提供等价设置。Cache Storage 离线资源不因此被清除。
- 根据部署环境设置域名、DNS、HTTPS 证书、安全响应头、缓存头和 SPA 回退规则。

这些规则属于托管配置，不应进入 Core、Storage、Sync 或 Razor 组件。为某个平台添加配置时，应放在独立的部署目录或部署流程中。

## 发布后的应用更新

页面在打开、恢复前台、恢复联网以及前台每分钟检查 Service Worker 新版；资源清单中的整个版本通过完整性预缓存后才进入更新等待状态。没有新版本或没有网络时保持当前版本与离线能力。

等待中的 Worker 会询问同一注册 scope 内的所有浏览器标签页与已安装 PWA。每个页面确认启动完成、没有编辑弹窗或未保存表单、没有进行中的交互后，会短暂阻止新输入。所有窗口都回应安全，而且已有本地数据操作锁空闲时，Worker 才激活并接管；页面收到控制器切换后只刷新一次。

新打开的窗口通过共享启动门槛与升级协调。提交激活后，即使切换延迟也不能按超时恢复旧版写入；保护持续至真正激活。各窗口重新加载确认后才回收旧应用缓存，缓存安装和回收互斥，避免连续部署时误删下一版本。上述流程均不读取或清理 IndexedDB。

任一窗口正在编辑、保存，或者不支持更新协议时，更新延期并恢复页面操作。提示出现后，请在各窗口保存并关闭编辑界面；设置表单保存后返回日历。如果某个旧窗口没有回应，可保存后关闭它。更新检查失败不会导致清理缓存、注销登录、删除 IndexedDB 或丢失本地日程。

**首次从未包含更新协议的旧版本迁移：** 旧页面无法接收新的安全确认消息，不能为了更新强行打断可能未保存的输入。部署成功、等待新 Worker 完成安装后，先保存各端输入，关闭本站所有标签页与已安装 PWA 窗口，再重新打开一次。关闭窗口不会删除本地数据；此后发布才会自动切换。不要把“清除网站数据”当成更新步骤，它会删除本地日历数据库。

发布必须是同一构建的完整输出，包含 `pwaUpdates.js`、`pwaUpdateWorker.js`、对应样式、Worker 和 manifest；不能把新旧文件混合上传。部署中完整性校验失败时保留旧版，待全量上传完成后重试。

本地 Windows 发布同样必须运行安全头生成脚本。脚本按 HTML 解析规则统一内联脚本的换行符后计算 CSP 哈希，但不修改已纳入完整性清单的 HTML 字节；仍不允许 `script-src unsafe-inline`。

## 路由与子路径

应用当前以站点根路径 `/` 为默认部署位置，`index.html` 中的 `<base href="/" />` 与此一致。客户端内导航由 Blazor Router 处理，但服务器仍必须把深层路由回退到 `index.html`。

如果将来部署到 `/calendar/` 等子路径，部署流程需要把发布产物中 `index.html` 的 base href 设置为带尾部斜杠的 `/calendar/`，并让静态服务器在同一子路径提供所有文件和 SPA 回退。这个路径取决于最终主机，因此不应写死在组件或领域代码中。Service Worker 会根据自身注册 scope 推导缓存根路径。

## 公开运行时配置

`wwwroot/appsettings.json` 提供平台无关的公开配置入口：

- `MoiCalendar:PublicBaseUrl`：应用公开绝对 URL；为空时使用浏览器实际加载地址。当前生产值为 `https://polite-rock-09eddaf00.7.azurestaticapps.net/`，认证回调清单见 [生产域名与认证回调](authentication-redirects.md)。
- `MoiCalendar:MicrosoftAuthentication`：为未来 Microsoft 登录保留公开参数边界，目前未启用。
- `MoiCalendar:Synchronization:Provider`：仅为未来保留的未启用字段，修改它不会改变实际提供者。当前外部备份提供者由设置页的本机选择决定；WebDAV 配置界面尚未开放。
- `MoiCalendar:CloudBackend:Enabled`：是否启用云账户与增量同步。
- `MoiCalendar:CloudBackend:BaseUrl`：兼容 Supabase API 网关的公开 URL。
- `MoiCalendar:CloudBackend:PublicKey`：浏览器可见的 Publishable/anon key。
- `MoiCalendar:CloudBackend:Supabase:RealtimePath`：提供程序专用的 Realtime WebSocket 路径。

静态站点中的配置文件可以被任何访问者下载，因此这里只能保存公开值。`sb_secret_...`、service-role key、密码、访问令牌、刷新令牌、客户端密钥和其他秘密绝不能放入该文件或任何前端发布产物。详细开发与切换流程见 [Supabase 开发与可移植性](supabase-development.md)。

生产 Azure Static Web Apps 工作流不会从门户 Application settings 隐式读取 Blazor 客户端配置，而是在发布前根据 GitHub repository variables 生成公开的 `appsettings.Production.json`。完整变量清单、构建数据流和路由配置见 [Azure Static Web Apps 生产部署](azure-static-web-apps.md)。
