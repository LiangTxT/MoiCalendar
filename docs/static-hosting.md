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
- 根据部署环境设置域名、DNS、HTTPS 证书、安全响应头、缓存头和 SPA 回退规则。

这些规则属于托管配置，不应进入 Core、Storage、Sync 或 Razor 组件。为某个平台添加配置时，应放在独立的部署目录或部署流程中。

## 路由与子路径

应用当前以站点根路径 `/` 为默认部署位置，`index.html` 中的 `<base href="/" />` 与此一致。客户端内导航由 Blazor Router 处理，但服务器仍必须把深层路由回退到 `index.html`。

如果将来部署到 `/calendar/` 等子路径，部署流程需要把发布产物中 `index.html` 的 base href 设置为带尾部斜杠的 `/calendar/`，并让静态服务器在同一子路径提供所有文件和 SPA 回退。这个路径取决于最终主机，因此不应写死在组件或领域代码中。Service Worker 会根据自身注册 scope 推导缓存根路径。

## 公开运行时配置

`wwwroot/appsettings.json` 提供平台无关的公开配置入口：

- `MoiCalendar:PublicBaseUrl`：应用公开绝对 URL；为空时使用浏览器实际加载地址。
- `MoiCalendar:MicrosoftAuthentication`：为未来 Microsoft 登录保留公开参数边界，目前未启用。
- `MoiCalendar:Synchronization:Provider`：为未来同步提供者选择保留配置边界，目前未启用。

静态站点中的配置文件可以被任何访问者下载，因此这里只能保存公开值。密码、访问令牌、刷新令牌、客户端密钥和其他秘密绝不能放入该文件或任何前端发布产物。
