# MoiCalendar 仓库协作规则

本文件适用于整个仓库。所有开发者和编码代理在修改仓库前都必须阅读并遵守本文件。

## 项目定位

- 项目名称：`MoiCalendar`。
- 项目使用语言：简体中文。用户界面、面向用户的提示和项目文档默认使用简体中文。
- 产品目标：个人使用的、本地优先的日历 PWA。
- 目标平台：Windows、iPad 和 Android。
- 技术栈：.NET 10、C#、独立 Blazor WebAssembly、PWA、IndexedDB。
- 应用必须在断网时仍能打开，并能查看和修改本地日历数据。
- IndexedDB 是本地日历数据的事实来源；远端存储将来只作为同步介质，不是应用运行所依赖的主数据库。

## 当前里程碑

当前里程碑是：**仅实现本地日历**。

当前阶段应专注于：

- 日历领域模型和应用服务。
- 通过接口隔离的本地持久化。
- IndexedDB 数据库、版本迁移和事务。
- 本地日历的创建、读取、修改和删除。
- 离线启动和离线使用。
- 与当前功能直接相关的测试。

当前阶段不得实现：

- Microsoft Graph 集成。
- Microsoft 登录、令牌获取或 OneDrive App Folder 访问。
- WebDAV 网络请求、认证或服务器兼容逻辑。
- 云端同步流程或后台同步。
- 与本地日历里程碑无关的功能。

可以为未来边界保留清晰的接口和数据契约，但不要提前实现云同步提供程序。

## 推荐解决方案结构

保持架构简单，初始解决方案使用以下项目：

```text
src/
  MoiCalendar.App/
  MoiCalendar.Core/
  MoiCalendar.Storage/
  MoiCalendar.Sync/

tests/
  MoiCalendar.Tests/
```

项目职责：

- `MoiCalendar.App`：Blazor WebAssembly PWA、Razor 页面与组件、UI 状态、PWA 静态资源以及依赖注入组合入口。
- `MoiCalendar.Core`：领域模型、应用服务、持久化接口及与基础设施无关的业务规则。
- `MoiCalendar.Storage`：当前提供内存仓储实现，未来在相同接口后增加 IndexedDB 和 JavaScript interop 实现。
- `MoiCalendar.Sync`：未来的同步抽象、同步引擎以及 OneDrive 和 WebDAV 适配器；当前里程碑不实现同步。
- `MoiCalendar.Tests`：当前解决方案的自动化测试。

依赖方向：

```text
MoiCalendar.Core    -> 不依赖其他项目
MoiCalendar.Storage -> MoiCalendar.Core
MoiCalendar.Sync    -> MoiCalendar.Core
MoiCalendar.App     -> MoiCalendar.Core；将来仅在组合入口引用 Storage 和 Sync
MoiCalendar.Tests   -> 被测试的项目
```

`MoiCalendar.App` 对 Storage 或 Sync 的引用只能用于应用启动和依赖注入注册。Razor 页面与组件不得直接使用其中的具体实现。

推荐调用路径：

```text
Razor UI
  -> 应用服务
  -> 本地持久化接口
  -> IndexedDB 实现

Sync Engine
  -> ISyncStorageProvider
  -> OneDriveSyncStorageProvider 或 WebDavSyncStorageProvider
```

## 永久架构规则

以下规则是永久约束，不得为了方便而绕过：

1. MoiCalendar 是本地优先日历；本地读写不得依赖网络可用性。
2. UI 不得直接访问 IndexedDB。
3. UI 不得直接访问 Microsoft Graph。
4. UI 不得直接实现或包含 WebDAV 逻辑。
5. 所有持久化功能必须隐藏在接口之后。
6. 所有同步功能必须隐藏在接口之后。
7. Sync Engine 只能依赖 `ISyncStorageProvider`，不得依赖任何具体提供程序。
8. OneDrive 专用逻辑必须完全保留在 `OneDriveSyncStorageProvider` 内部。
9. WebDAV 专用逻辑必须完全保留在 `WebDavSyncStorageProvider` 内部。
10. OneDrive 和 WebDAV 提供程序必须使用完全相同的远端同步数据格式。
11. 永远不要直接同步 IndexedDB、SQLite 或其他数据库文件；只同步带版本的、提供程序无关的数据文档或记录。
12. 永远不要把密码、访问令牌、刷新令牌、客户端密钥、私钥或其他秘密提交到源代码管理。
13. 不要实现与当前任务或当前里程碑无关的功能。
14. 不要执行与当前任务无关的大规模重构、重命名或格式化。
15. 重要改动后必须运行相关测试。
16. 完成任务前必须构建解决方案。

## 本地持久化边界

- UI 应调用 Core 中的应用服务，而不是调用 IndexedDB 包装器。
- Core 中定义仓储接口；Storage 提供 IndexedDB 实现。
- IndexedDB 的 JavaScript interop、object store 名称、索引和迁移细节不得泄漏到 UI 或领域模型。
- 业务数据更新和对应的本地变更记录应尽可能在同一个 IndexedDB 事务中完成。
- 删除需要保留可供未来同步使用的删除标记，不能默认立即彻底移除所有痕迹。
- Service Worker Cache Storage 只缓存应用资源，不得作为日历业务数据的事实来源。
- 必须独立管理本地数据库 schema 版本和未来远端同步格式版本。

## 同步边界（供未来使用）

- 当前里程碑不得实现云同步；本节定义未来实现时必须遵守的边界。
- `ISyncStorageProvider` 应表达提供程序无关的远端读取、条件写入和版本令牌能力。
- 接口不得暴露 Graph `DriveItem`、OneDrive drive ID、WebDAV URL、WebDAV 方法、MSAL token 或提供程序专用 HTTP 类型。
- Sync Engine 负责统一同步格式的序列化、反序列化、校验、迁移、合并和冲突处理。
- Provider 只负责认证和传输，并把提供程序专用错误转换为通用结果。
- 远端同步数据不得包含凭据或认证令牌。
- 远端写入必须支持并发冲突检测；不得无条件覆盖较新的远端数据。

## 工作范围与变更纪律

- 在动手前先检查现有实现、测试和仓库状态。
- 优先完成满足任务所需的最小改动，保持类和项目数量适度。
- 不要仅为未来可能的需求引入额外框架、后端服务或复杂抽象。
- 不要修改无关文件；发现用户已有改动时必须保留它们。
- 新增依赖前应说明必要性，并优先使用 .NET 和浏览器平台已有能力。
- 涉及日历时间时，必须明确区分定时事件和全天事件，并考虑时区及夏令时；不要把全天日期简单当作午夜 UTC。

## 验证要求

当解决方案和测试项目已经存在时：

1. 运行与改动相关的测试。
2. 运行完整的 `dotnet test`（如果测试规模允许）。
3. 最后运行 `dotnet build`。
4. 如果因为环境、缺少 SDK 或外部服务而无法验证，必须在任务总结中明确说明，不得声称验证成功。

涉及 PWA 或 IndexedDB 的重要改动还应在发布构建中验证，因为开发模式不能完整代表 Service Worker 的离线行为。
