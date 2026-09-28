# MoiCalendar V4 前端重构审计

状态：R0 已完成审计，等待 R1 设计系统阶段  
最后更新：2026-09-28  
适用范围：V4 前端重构 R0–R9

## 1. 决策与优先级

V4 的固定优先级是：

1. 正确
2. 顺滑
3. 简洁
4. 帅

现有视觉前端不是兼容目标。旧主题、字体偏好、卡片、工具栏、侧栏、设置布局、弹层样式、事件样式和页面布局均可在对应阶段删除或替换。R0 只冻结功能边界、记录迁移计划和补充自动化护栏，不进行完整视觉重写。

仓库 `AGENTS.md` 规定当前里程碑只实现本地日历。仓库中已经存在云同步、Realtime、OneDrive 和 WebDAV 代码；V4 必须保护这些既有能力，但本轮不得扩展其功能、协议或提供程序。R7 重新接入这些入口前，需要再次核对里程碑范围。

## 2. 不可破坏的边界

```text
V4 Razor UI
  -> App 层页面状态 / 意图分发器
  -> Core 应用服务
  -> Core 持久化接口
  -> Storage 的 IndexedDB 实现

Sync Engine
  -> ISyncStorageProvider
  -> 具体远端提供程序
```

- V4 组件可以调用应用层或 Core 的应用服务。
- V4 组件不得直接访问 IndexedDB、Storage 具体类型、SyncOutbox、Supabase 或 Realtime 内部实现。
- IndexedDB 继续是本地日历事实来源；网络失败不得阻断本地读取和编辑。
- JavaScript 只负责浏览器测量、高频指针预览、滚动和焦点辅助，不拥有业务状态。
- 全天事件使用日期语义；不得把全天日期当作午夜 UTC。
- 业务数据更新与本地变更记录继续由应用服务和存储边界协调。

## 3. 能力清单

| 能力 | 现有入口/实现 | R0 结论 | V4 后续 |
| --- | --- | --- | --- |
| 事件 CRUD | `CalendarEventService`、`IEventRepository` | KEEP | 所有视图共用，不把 CRUD 复制到组件 |
| 月/周/日/日程查询 | `CalendarEventService` 的视图查询 | KEEP | 通过统一页面状态加载 |
| 重复事件展开 | `RecurrenceExpansionService` | KEEP | UI 只呈现结果；修改范围由统一交互模型处理 |
| 全天、跨日、时区、夏令时 | Core 领域与布局模型 | KEEP | 所有 V4 视图必须保持语义 |
| 月视图布局 | `CalendarLayoutEngine`、Month 组件 | REUSE | R3 重写表现，保留纯算法 |
| 周/日时间网格 | `CalendarTimeGrid` 与纯布局算法 | REUSE/REWRITE | R4 共用一个 TimeGrid，重写表现和状态边界 |
| 拖动/缩放 intent | `CalendarInteractionService`、`calendarInteraction.js` | REUSE/REWRITE | R5 保留意图和几何算法，改为高频 JS 预览、单次提交 |
| 本地 IndexedDB | Storage 项目及 JS interop | KEEP | 不向 V4 UI 泄漏 object store 或 schema |
| 本地变更记录 / Outbox | Core 接口与 Storage 实现 | KEEP | 只能在应用服务/存储事务边界使用 |
| 离线 PWA | service worker、manifest、静态资源 | KEEP | Shell 改造后做发布构建与离线验收 |
| 搜索/筛选 | 现有页面能力与查询状态 | KEEP/REWRITE | R2/R7 以新入口接回，不复用旧表现 |
| 导入/导出、本地备份 | Core 应用服务、浏览器下载适配器 | KEEP | R7 重写入口 |
| 云同步、冲突、Realtime | Sync 项目与应用服务接口 | KEEP | 当前不扩展；R7 只重写表现 |
| 账户、设备、删除账户 | Sync 应用服务及设置页 | KEEP/REWRITE | R7 重写页面，不把提供程序细节带入日历画布 |
| OneDrive / WebDAV | `ISyncStorageProvider` 后的实现 | KEEP | 当前不新增网络逻辑；R7 作为外部备份入口 |
| 诊断 | Core/Sync 诊断服务 | KEEP/REWRITE | 技术细节仅在 Diagnostics 展示 |
| 外观主题和字体选择 | `AppearanceService`、Core 偏好模型、旧 CSS | REMOVE/REWRITE | R1 只保留一个 V4 语义 token 系统与 Light/Dark |

## 4. 旧组件审计

### KEEP — 与表现无关

- `MoiCalendar.Core` 的领域实体、草稿、仓储接口和应用服务。
- `CalendarEventService`、`CalendarInteractionService`、重复事件、导入导出、本地备份服务。
- `MoiCalendar.Storage` 的 IndexedDB 实现、schema、事务和迁移。
- `MoiCalendar.Sync` 的既有应用服务、传输边界和提供程序实现；当前不扩展。
- PWA manifest、service worker 注册和发布缓存机制。

### REUSE — 成熟算法或纯模型

- `CalendarLayoutEngine` 中的可见范围、月格、全天、时间坐标和重叠布局算法。
- `CalendarMonthEventView`、`CalendarWeekEventView` 等只读展示投影中仍保持语义纯净的部分。
- `CalendarInteractionGeometry` 的 15 分钟吸附、指针到日期/分钟转换和移动阈值。
- `calendarInteraction.js` 中纯 pointer capture、bounds 和 scroll 测量能力。
- 现有时区、夏令时、重复事件和跨午夜测试数据。

复用算法不等于复用现有 HTML、CSS、组件状态或视觉结构。

### REWRITE — 功能有价值，UI 架构不适合 V4

| 当前代码 | 问题 | 目标阶段 |
| --- | --- | --- |
| `Pages/Home.razor` | 同时拥有路由、查询、视图、选择、多个 overlay、CRUD、同步入口和错误状态 | R2/R6 收敛为页面组合入口和唯一意图分发器 |
| `CalendarToolbar.razor` | 旧工具栏结构和表现 | R2 替换为 `CommandHeader` |
| `CalendarSidebar.razor` | 旧品牌区、图标和链接布局 | R2 替换为 `NavigationPane` |
| `CalendarTimeGrid.razor` | 方向正确，但仍同时负责 DOM 会话、预览、组件状态和视图表现 | R4/R5 拆清共享网格与浏览器交互模块 |
| Month 系列组件 | 可复用布局输入，但表现仍属于旧系统 | R3 从零重写 Month 表现 |
| `EventDetailPopover.razor` | 详情依赖 modal/popover，桌面没有常驻 Inspector | R2/R6 替换为 `ContextInspector` |
| `EventQuickCreate.razor` | 表单能力可保留，overlay 与焦点架构需统一 | R6 接入唯一 `OverlayHost` |
| `Pages/Settings.razor` | 超大页面，直接组合多个云/提供程序服务，视觉为旧设置系统 | R7 以安静的分级设置重写 |
| `Pages/SyncStatusPage.razor` | 正常状态暴露过多实现细节 | R7 将实现术语移入 Diagnostics |
| `Layout/MainLayout.razor` | 旧全局导航和 footer 与目标四区 Shell 重叠 | R2 重写 Shell 后删除旧结构 |
| `App.razor` 的 Realtime 初始化 | 根组件直接承担运行时基础设施生命周期 | 后续低风险迁移到明确的 App 启动协调器；不得进入视觉组件 |

### REMOVE — V4 不保留的旧视觉资产

以下内容只在其替代实现通过对应截图门槛后删除，R0 不提前删除：

- `AppearanceService` 驱动的多色主题和字体主题选择。
- `AppearancePreference` 中仅服务旧视觉的色板/字体目录，以及对应 IndexedDB 偏好迁移代码；删除前先确认不影响其他设置。
- `wwwroot/css/typography.css` 的多字体切换规则和不再使用的本地字体文件。
- `app.css` 中旧 Shell、toolbar、sidebar、card、popover、modal、event pill 和 settings dashboard 规则。
- 被 `CommandHeader`、`NavigationPane`、`ContextInspector` 和 `OverlayHost` 替代的旧组件。
- 并存的详情弹层、快速创建弹层、完整编辑器和确认框宿主。
- 旧 UI 专用且无调用方的状态字段、JS 入口和 CSS selector。

禁止为了回退而长期保留 V3/V4 两套视觉系统。每次删除前必须证明新路径功能完整，并运行相关测试。

## 5. V4 所需应用 API

现有可直接使用：

- `CalendarEventService.CreateAsync / UpdateAsync / DeleteAsync / GetByIdAsync`
- `CalendarEventService.GetMonthViewAsync / GetWeekViewAsync / GetAgendaViewAsync`
- `CalendarInteractionService.ExecuteAsync`
- `ICalendarViewPreferenceStore`
- 导入、导出、本地备份、恢复和诊断应用服务接口
- 既有 provider-neutral 同步/账户应用服务接口（仅 R7 使用）

R2–R6 需要的 App 层状态/API，不应放入领域实体：

- `CalendarPageState`：活动视图、anchor date、selected date/event、可见日历和每视图滚动快照的唯一所有者。
- `CalendarOverlayState`：`None / QuickCreate / Editor / Confirmation / MobileInspector / Menu` 的单一判别状态。
- `CalendarInteractionSession`：一次 pointer 会话的临时浏览器状态；提交后只产生一个 Core intent。
- `CalendarCommandDispatcher`（名称可调整）：统一处理创建、编辑、删除、移动、缩放、选择和视图命令，成功后重新查询事实来源。
- 面向 Inspector 的只读事件详情投影，避免 Inspector 自己查询仓储或复制业务规则。

不要为了这些状态新增后端、状态管理框架或网络依赖。

## 6. 已发现的耦合问题

1. `Home.razor` 是事实上的巨型协调器，页面级状态和业务命令没有稳定边界。
2. Quick Create、详情、编辑器、overflow 和错误提示由多个 nullable 字段/布尔值组合，可能同时激活。
3. 月视图和 TimeGrid 的指针会话分散在不同组件；R5 前必须统一提交规则。
4. `Home.razor` 同时显示主日历与外部备份操作，使 Calendar Canvas 失去唯一主角地位。
5. `Settings.razor` 直接组合多个 Sync/OneDrive 应用接口；这些是遗留页面耦合，不能复制到 V4 Shell 或日历组件。
6. `App.razor` 直接初始化 Realtime；虽然没有泄漏到日历组件，仍让根视觉组件承担基础设施生命周期。
7. 旧主题模型进入 Core，视觉偏好与领域/应用边界混在一起；R1 需要单一 V4 token 系统并规划兼容数据的退场。
8. `MainLayout`、日历 toolbar/sidebar 与页面内 utility row 形成多重导航层级。
9. 旧 CSS 是单一大文件，新旧 selector 容易共存；后续阶段必须以组件替换为单位删除旧规则。
10. 当前仓库实现范围已经超出 `AGENTS.md` 的“仅本地日历”里程碑；V4 不应借 UI 重构继续扩张云功能。

## 7. 目标组件树

```text
App
└─ V4ApplicationShell
   ├─ CommandHeader
   ├─ NavigationPane
   │  ├─ MiniCalendar
   │  ├─ CalendarVisibilityList
   │  └─ NavigationActions
   ├─ CalendarCanvas
   │  ├─ MonthView
   │  ├─ TimeGrid (Week)
   │  ├─ TimeGrid (Day, one-column)
   │  └─ AgendaView
   ├─ ContextInspector
   └─ OverlayHost
      ├─ QuickCreate
      ├─ EventEditor
      ├─ Confirmation
      ├─ MobileInspector
      └─ Menu
```

桌面只允许四个主要区域：`CommandHeader`、`NavigationPane`、`CalendarCanvas`、`ContextInspector`。Overlay 是上下文层，不是第五个常驻主区域。

## 8. 分阶段删除顺序

1. R1 建立 V4 token，但不批量替换页面。
2. R2 新 Shell 可用并通过桌面/平板/手机截图后，删除旧 `MainLayout`、toolbar、sidebar 和 utility row 表现。
3. R3/R4 分别替换 Month 和共享 TimeGrid；每个视图通过数据密度和边界测试后删除旧标记/CSS。
4. R5 完成交互管线后删除旧 pointermove/drag/resize 会话代码。
5. R6 唯一 Inspector/OverlayHost 可用后删除重复 dialog/popover 宿主和状态字段。
6. R7 功能入口重新接入后删除旧 Settings/Sync 页面表现。
7. R8 只加入受控身份与动效，不改变应用架构。
8. R9 搜索并删除所有无调用方的 V3 组件、主题、CSS、JS 和状态服务。

## 9. R0 自动化护栏

`CalendarFrontendArchitectureTests` 负责验证：

- Core 不引用 App、Storage 或 Sync 项目。
- Storage 和 Sync 只能引用 Core。
- 日历 Razor 组件和 `Home.razor` 不引用 IndexedDB、Storage 具体类型、Supabase、Realtime 内部类型、OneDrive/WebDAV 实现或 SyncOutbox。
- 指针组件只发出 intent，不直接调用应用服务提交数据。
- 周视图和日视图共享一个 TimeGrid 基础。
- 日历 JavaScript 不访问持久化或网络。

这些是最低边界，不代表后续视觉或交互阶段已经完成。

## 10. R0 验收

| 门槛 | 状态 | 证据 |
| --- | --- | --- |
| `CalendarEventService` 可独立于旧 UI 使用 | PASS | 位于 Core；Core 项目无项目引用；由接口注入仓储 |
| Sync 不依赖旧组件 | PASS | Sync 只引用 Core，不引用 App/Razor |
| Event CRUD 不依赖旧视觉结构 | PASS | CRUD 位于 Core，Razor 只通过应用服务/intent 调用 |
| 明确列出要删除的旧 UI | PASS | 本文第 4、8 节 |
| 新 V4 UI 不需要知道 Supabase 实现 | PASS（边界已建立） | 日历组件静态护栏禁止提供程序实现引用；V4 尚未开始渲染 |
| 完整测试和构建通过 | PASS | 2026-09-28：`dotnet test MoiCalendar.slnx --no-restore`，502/502 通过；`dotnet build MoiCalendar.slnx --no-restore` 成功，0 警告、0 错误 |

R0 不包含截图门槛，因为没有进行视觉重写。R2 起必须在 `docs/ui-audit/` 保存固定视口截图，并在进入下一阶段前人工验收。
