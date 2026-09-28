# MoiCalendar 日历前端架构

状态：已采纳（Module 33）  
适用范围：月、周、日、日程视图，以及后续日历交互模块  
最后更新：2026-09-23

## 1. 目的与约束

本文定义 MoiCalendar 日历前端的统一边界。后续功能应沿着这里的状态、渲染、布局、交互和持久化链路演进，不再让单个 Razor 页面同时承担所有职责。

不可破坏的约束：

- `CalendarEvent` 是领域实体，不承载像素、百分比、列号或 DOM 信息。
- IndexedDB 是本地事实来源；界面只通过 Core 应用服务读写。
- 日历组件不得直接调用 IndexedDB、SyncOutbox、Supabase、Microsoft Graph 或 WebDAV。
- 周视图和日视图共享同一套时间网格、布局和交互模型。
- 全天日期和定时事件保持不同语义；跨时区和夏令时转换由应用服务处理。
- JavaScript 只帮助完成浏览器高频交互，不保存业务状态，也不决定业务规则。

本次参考了 TUI Calendar 的公开行为和文档，但没有安装、嵌入或复制其 Preact 实现，也没有改编其源代码。TUI Calendar 使用 MIT License；未来如果实际改编其源代码，必须在改动附近和第三方声明中保留适用的版权及许可信息。

官方参考：

- [TUI Calendar 仓库与功能概览](https://github.com/nhn/tui.calendar)
- [Calendar API 与实例事件](https://github.com/nhn/tui.calendar/blob/main/docs/en/apis/calendar.md)
- [EventObject](https://github.com/nhn/tui.calendar/blob/main/docs/en/apis/event-object.md)
- [Theme](https://github.com/nhn/tui.calendar/blob/main/docs/en/apis/theme.md)
- [Template 与 More Events](https://github.com/nhn/tui.calendar/blob/main/docs/en/apis/template.md)
- [MIT License](https://github.com/nhn/tui.calendar/blob/main/LICENSE)

## 2. 现有前端审计

### 2.1 组件和页面

| 当前实现 | 现有职责 | 审计结论 |
| --- | --- | --- |
| `Pages/Home.razor` | 路由、活动视图、日期导航、数据加载、月视图渲染、弹层、快捷键、月视图拖拽、创建/编辑/删除 | 当前事实上的页面协调器，但职责过多；应逐步收敛为唯一页面状态所有者和意图分发器 |
| `CalendarToolbar.razor` | 日期导航、视图切换、新建入口 | 保持无业务状态，由页面传入状态和回调 |
| `CalendarSidebar.razor` | 迷你月历、可见日历、设置入口 | 主日历状态来自页面；迷你月历自己的浏览月份只属于迷你月历游标，不等同于主视图可见范围 |
| `CalendarTimeGrid.razor` | 周/日共享时间网格、全天区、定位、重叠布局、当前时间、指针交互 | 共享方向正确；当前仍持有拖拽会话和滚动行为，后续应接入统一交互会话 |
| `EventQuickCreate.razor` | 快速创建表单 | 只编辑草稿并发出回调，不直接持久化，边界正确 |
| `EventDetailPopover.razor` | 详情、编辑/删除入口 | 只显示数据并发出回调，边界正确 |

月视图目前仍直接写在 `Home.razor` 中；周/日已复用 `CalendarTimeGrid`。`+N 更多`、快速创建、详情和完整编辑器由页面以多个字段组合管理。

### 2.2 渲染和布局

- `CalendarMonthEventView`、`CalendarWeekEventView` 是应用服务生成的只读展示投影。
- `CalendarEventListItem`、`CalendarWeekAllDayEvent`、`CalendarWeekTimedEvent` 已与 `CalendarEvent` 分离，因此领域实体没有 `PixelTop`、`WidthPercent` 或 `ColumnIndex`。
- `CalendarPresentationLayout` 原先集中月格裁剪、时间定位、重叠分列和跨日分段。本模块将其拆成明确的布局引擎，并保留旧门面兼容现有组件。
- 当前百分比位置存在于周视图展示模型中，不存在于领域实体中；这是允许的过渡状态。

### 2.3 主题与排版

- `ThemeColorCatalog` 和 `AppearanceService` 负责语义色板与用户偏好。
- `app.css` 使用 `--color-*`、`--surface-*`、`--text-*`、间距和圆角变量表达视觉语义。
- `typography.css` 通过 `--font-display`、`--font-ui`、`--font-body`、`--font-calendar` 切换排版。
- 后续布局模型不得携带颜色、字体或 CSS 类名；这些由 Razor 组件和语义 token 决定。

### 2.4 数据、持久化和重复事件

当前主要读取链路：

```text
Home / CalendarTimeGrid
  -> CalendarEventService
  -> IEventRepository
  -> IndexedDbEventRepository
  -> IndexedDB
```

当前本地变更链路：

```text
CalendarInteractionService 或 CalendarEventService
  -> ILocalEventChangeRepository
  -> 业务数据与本地变更记录的同一存储边界
  -> 后续同步读取本地变更
```

`RecurrenceExpansionService` 在应用层按查询范围展开重复事件；视图只消费展开结果。拖拽重复事件时，`CalendarInteractionService` 返回 `RecurrenceScopeRequired`，不会静默修改整个系列。

## 3. TUI 行为映射

| TUI Calendar 概念 | MoiCalendar 对应设计 | 采用方式 |
| --- | --- | --- |
| Calendar instance / view name | 页面级 `CalendarViewState` 与 Blazor 视图组件 | 只借鉴单一实例状态，不引入 JavaScript Calendar 实例 |
| `selectDateTime` | `SelectTimeRangeIntent` / `SelectDateRangeIntent` | 组件发出语义范围，不传 DOM 元素到业务层，也不自动创建事件 |
| `beforeCreateEvent` | 创建意图 -> `CalendarEventService.CreateAsync` | 先验证再提交本地事务 |
| `beforeUpdateEvent` | 移动/缩放意图 -> `CalendarInteractionService` | 保留原始时间用于并发检查；不让组件直接更新实体 |
| `beforeDeleteEvent` | 删除意图 -> 应用服务 | 使用现有删除标记和本地变更记录语义 |
| `clickEvent` | 选择事件并打开详情 overlay | 页面状态决定唯一活动弹层 |
| `clickMoreEventsBtn` | 月格 overflow 意图 | 打开同一 overlay 系统中的日期事件列表 |
| `EventObject` | 领域 `CalendarEvent` + 展示投影 + 布局结果 | 不复制 TUI 把数据、颜色和 `customStyle` 混在同一对象的做法 |
| Month / Week / Day | `MonthCalendarView`（待提取）与共享 `CalendarTimeGrid` | 周/日共享结构；月视图保持稳定格高和 overflow |
| Grid selection | 临时选择状态 + 创建意图 | DOM 坐标只在交互边界转换为日期/分钟 |
| Theme | MoiCalendar 语义 CSS token | 不复制 TUI theme key；保留 Sage 和现有主题系统 |
| Template | Razor 小组件与 RenderFragment | 编译期组件组合，不返回 HTML 字符串 |
| More events popup | `MoreEventsOverlayState` | 数据来自同一月视图投影，并通过统一 overlay host 转入事件详情 |
| Drag/update lifecycle | Begin -> Preview -> Commit/Cancel -> Intent -> Service | 预览无持久化；只有 Commit 可以触发应用服务 |

TUI 的实例事件明确区分“用户发生了什么”和“调用方如何提交数据”。MoiCalendar 采用这个边界，但由强类型 C# intent、应用服务和本地事务实现。

## 4. 目标组件树

```text
Home.razor（路由与组合入口）
└─ CalendarPageState（唯一页面状态所有者，待提取）
   ├─ CalendarToolbar
   ├─ CalendarSidebar
   │  └─ MiniCalendar（只拥有自己的浏览游标）
   ├─ CalendarSurface
   │  ├─ MonthCalendarView（待从 Home 提取）
   │  ├─ CalendarTimeGrid（Week）
   │  ├─ CalendarTimeGrid（Day）
   │  └─ CalendarAgendaView（待从 Home 提取）
   └─ CalendarOverlayHost（待提取）
      ├─ EventQuickCreate
      ├─ EventDetailPopover
      ├─ DayOverflowPopover
      └─ EventEditor
```

组件规则：

- 视图组件接收不可变展示模型、页面状态快照和回调。
- 组件可以计算纯展示派生值，但不能复制页面级状态。
- `CalendarOverlayHost` 同一时间只渲染一个 overlay。
- 页面协调器接收组件 intent，调用应用服务，然后重新加载当前范围的投影。

## 5. 状态所有权

目标状态容器暂定名为 `CalendarPageState`，位于 App 层；它不是领域模型，也不持久化业务数据。

| 状态 | 唯一所有者 | 说明 |
| --- | --- | --- |
| Active view | `CalendarPageState` | Month / Week / Day / Agenda；偏好存储只是恢复来源，不是运行时所有者 |
| Visible date range | `CalendarPageState` 派生值 | 由 active view + anchor date 计算，不单独双向修改 |
| Selected date | `CalendarPageState` | 工具栏、侧栏和主视图都通过回调修改同一值 |
| Selected event | `CalendarPageState`，只保存事件 ID | 详情数据按 ID 加载；组件不保留另一份选择 |
| Temporary selection | `CalendarInteractionSession` | 只在拖选期间存在，提交后变为 intent，取消后清空 |
| Active overlay | `CalendarOverlayState` | 判别联合：None / QuickCreate / Detail / DayOverflow / Editor |
| Drag state | `CalendarInteractionSession` | 同一时刻只有一个 pointer session；月格和时间网格不各自保留可同时生效的状态 |
| Current scroll context | `CalendarPageState` 的每视图 scroll snapshot | JS 只读写 DOM scrollTop；C# 状态决定何时恢复 |

允许的组件本地状态：输入控件临时值、动画阶段、测量缓存、计时器、迷你月历浏览游标。它们不得成为活动主视图、选择、overlay 或持久化事件的第二事实来源。

## 6. 渲染模型设计

### 6.1 分层

```text
CalendarEvent（领域事实）
  -> CalendarEventService（范围查询、时区转换、重复展开）
  -> CalendarRenderEvent（语义展示事件，目标模型）
  -> CalendarRenderSegment（按可见日期范围切分）
  -> Layout Engine
  -> MonthLayoutItem / TimeGridLayoutItem（纯布局结果）
  -> Razor + CSS token
```

目标 `CalendarRenderEvent` 可包含：事件 ID、标题、位置、日历 ID/颜色槽、显示时区中的开始/结束、全天标志、重复标志和只读标志。它不得包含像素、DOM、CSS 类、弹层状态或仓储对象。

目标 `CalendarRenderSegment` 可包含：逻辑事件 ID、可见日期、段开始/结束、`Single/Start/Middle/End`、是否在视图边界前后继续。它不是新的 `CalendarEvent`，也不得写回仓储。

当前过渡映射：

- `CalendarEventListItem`：月/日程语义展示项。
- `CalendarWeekAllDayEvent`：全天 segment。
- `CalendarWeekTimedEvent`：时间网格 segment；当前还带百分比，后续将语义时间与布局结果分开。
- `MonthDayLayout`：稳定月格内的可见项和 overflow 数量。
- `TimedEventPosition`：时间网格纵向结果。
- `TimedEventLayout`：重叠组列号和列数。

### 6.2 布局引擎

本模块确立以下无状态纯函数引擎：

- `MonthLayoutEngine`：月格可见数量与 overflow。
- `TimeGridLayoutEngine`：分钟到纵向百分比，应用最小可读高度。
- `OverlapLayoutEngine`：构造重叠组并确定稳定列号/列数。
- `MultiDayLayoutEngine`：确定跨日 segment 位置。
- `CalendarMetrics`：跨引擎共享的时间和默认展示指标。

`CalendarPresentationLayout` 暂时保留为兼容门面；新代码直接依赖专用引擎。布局引擎不访问仓储、时钟、JS 或同步服务。

## 7. 交互意图与提交规则

Module 37 已将交互拆成明确的不可变意图：

- `MoveEventIntent`：月格按来源日与目标日的日期差移动；时间网格携带新的开始/结束时间；
- `ResizeEventIntent`：只改变定时事件结束时间；
- `SelectDateRangeIntent`：表达半开日期范围，不创建事件；
- `SelectTimeRangeIntent`：表达显示时区内的时间范围，不创建事件；
- 后续补入：`CreateEvent`、`UpdateEvent`、`DeleteEvent`、`OpenOverlay`、`CloseOverlay`、`SetView`（最后三项不持久化）

唯一允许的 mutation 链路：

```text
Razor gesture / command
  -> CalendarInteractionIntent
  -> CalendarInteractionService
  -> CalendarEventService
  -> ILocalEventChangeRepository
  -> IndexedDB 业务记录 + 本地变更记录
  -> 后续同步流程
```

阶段规则：

1. Begin：捕获 pointer ID、原始事件版本和起点，不写数据。
2. Preview：把坐标转换为日期/分钟并运行纯布局，不写数据。
3. Commit：产生不可变 intent。
4. Validate：应用服务检查时区、最小时长、原始版本和重复范围。
5. Persist：本地数据和变更记录在存储边界中提交。
6. Reload：重新查询当前可见范围；不要把预览对象当作事实。
7. Cancel/Fail：清除临时状态；失败时保留用户表单输入并显示错误。

## 8. Overlay 状态

Module 38 已实现单一 `CalendarOverlayState`：

```text
None
QuickCreate(draft)
EventDetails(event)
MoreEvents(date)
RecurrenceScope(event, action, pendingIntent?)
FullEditor(draft)
```

活动状态实际由 `None`、`QuickCreateOverlayState`、`EventDetailsOverlayState`、`MoreEventsOverlayState`、`RecurrenceScopeOverlayState`、`FullEditorOverlayState` 六种互斥记录表达。`CalendarOverlayHost` 是唯一 backdrop/shell，统一处理外部点击、Escape、Tab 焦点循环和关闭后的焦点恢复。

桌面端由同一 host 呈现 popover；窄屏由 CSS 把同一内容切换为 bottom sheet，不复制业务组件。More Events 选择事件会直接转换为 Event Details，快速创建的 More Details 会把同一个 draft 实例转换到完整编辑器，因此不会丢失输入。

## 9. JS interop 边界

日历高频交互 JS 模块只允许：

- pointer capture / release；
- pointer 坐标和 DOM bounds；
- 读取、设置 scroll position；
- ResizeObserver 或等价尺寸观察；
- 必要的焦点记录与恢复。

JS 返回原始浏览器测量值，C# 的 `CalendarGeometry`/`CalendarInteractionGeometry` 负责转换为日期、分钟和 intent。

严禁 JS：

- 创建、更新或删除 `CalendarEvent`；
- 读写 IndexedDB 业务数据；
- 操作 SyncOutbox；
- 调用 Supabase、Graph 或 WebDAV；
- 处理登录、令牌或重复规则；
- 决定事件是否允许移动、删除或修改系列。

`calendarInteraction.js` 是高频浏览器交互的独立边界，只提供 pointer capture/release 和元素 bounds/scroll offset。`calendarUi.js` 继续负责外观、焦点、快捷键和滚动位置；两者都不接触业务数据。

## 10. 依赖规则

```text
Razor components
  -> App page state / intent dispatcher
  -> Core render projections + layout engines + application services
  -> Core repository interfaces
  -> Storage implementations

Sync
  -> Core contracts
  -> provider-neutral storage provider
```

- Domain：不知道 Razor、CSS、DOM、IndexedDB 和云提供商。
- Layout：只依赖输入模型和基础值类型。
- App：可依赖 Core；只在启动组合入口引用 Storage/Sync 具体注册。
- Storage：实现 Core 接口，不向 App 泄漏 object store、schema 或 JS identifier。
- Sync：不能成为本地 UI 读写的前置条件。
- 主题和模板：属于 App 呈现层，不进入领域或持久化 payload。

## 11. Month / Week / Day 迁移计划

### 阶段 A：本模块

- 固化本文档和依赖规则。
- 拆出四个纯布局引擎与共享 `CalendarMetrics`。
- 保留 `CalendarPresentationLayout` 兼容门面，不改变视觉和同步行为。

Module 34 已在此基础上补齐统一渲染输入、可见范围、跨日片段、全天布局、时间坐标转换和月格 overflow；算法说明见 [`calendar-layout-engine.md`](calendar-layout-engine.md)。

### 阶段 B：统一页面状态

- 引入 App 层 `CalendarPageState` 和单一 `CalendarOverlayState`。
- 把视图、日期、选择、overlay、drag session 和 scroll snapshot 从散落字段迁入状态容器。
- 每次只迁移一种状态，并补状态转换测试。

### 阶段 C：提取视图组件

- 从 `Home.razor` 提取 `MonthCalendarView` 和 `CalendarAgendaView`。
- 保持周/日继续共用 `CalendarTimeGrid`。
- 视图只发出 intent；页面协调器负责服务调用和重新加载。

### 阶段 D：规范渲染模型

- 引入统一 `CalendarRenderEvent` 和 `CalendarRenderSegment`。
- 从 `CalendarWeekTimedEvent` 移除百分比，将其放入 `TimeGridLayoutItem`。
- 把原始时间/版本信息放入独立 action reference，而不是视觉模型。

### 阶段 E：统一交互和 overlay（Modules 37-38 已完成基础实现）

- 月格和时间网格均发出显式 intent；其高频预览仍由各视图临时持有，后续可再提取共享 session。
- 创建、编辑和删除统一经过现有 `CalendarEventService`；移动、缩放和范围选择经过 `CalendarInteractionService`。
- `CalendarOverlayHost` 已统一 backdrop、移动端 sheet、Escape、外部点击、Tab 焦点循环和焦点恢复。

### 阶段 F：滚动与 JS 模块（指针边界已完成，滚动状态仍待收敛）

- 由页面状态保存每个视图的 scroll snapshot。
- 高频 `calendarInteraction.js` 已拆分；其 API 只返回测量值并管理 pointer capture。
- 使用契约测试确保 JS 模块不包含持久化或网络职责。

每个阶段都必须维持：相关测试通过、完整测试通过、最终解决方案构建成功，并且不改变同步数据格式或提交语义。

## 12. 已发现的技术债

1. `Home.razor` 是超大协调器，同时包含月视图标记、表单、overlay、状态和 mutation 流程。
2. 月格拖拽状态在页面中，时间网格拖拽状态在组件中，尚未使用统一 interaction session。
3. `CalendarWeekTimedEvent` 同时包含语义时间和布局百分比；后续应拆分。
4. `CalendarEventListItem`/周视图模型携带原始 UTC 时间和交互时区，展示与 mutation reference 尚未分离。
5. 快速创建、完整编辑和详情删除按现有服务边界直接调用 `CalendarEventService`；移动、缩放和范围选择经过 `CalendarInteractionService`。若未来要求所有命令都进入统一 dispatcher，需要在 Core 层补齐创建/更新/删除 intent，而不是在 UI 模拟。
6. 重复事件目前提供“整个系列”的显式确认；“仅本次”和“本次及以后”仍需要领域模型与持久化语义支持，不能只在 UI 添加选项。
7. `calendarUi.js` 仍同时负责外观、快捷键、焦点和滚动；指针测量已迁入独立 `calendarInteraction.js`，overlay 焦点循环已迁入 `calendarOverlay.js`。
8. 当前滚动位置主要由 DOM 自己持有，页面状态不能可靠恢复不同视图的滚动上下文。
9. 月视图和日程视图仍在 `Home.razor` 中，组件树不对称。
10. 当前仓库含云同步和多提供商实现，而仓库 `AGENTS.md` 声明当前里程碑仅实现本地日历；本模块不修改这些既有语义，后续范围规划需要显式处理这一偏差。
11. `CalendarPresentationLayout` 是历史聚合门面；待调用方迁移到专用引擎后再评估删除，不能为清理而一次性改写所有视图。

## 13. 验收护栏

后续日历前端改动应回答：

- 是否只存在一个活动视图、选择、overlay 和 drag session 所有者？
- 是否把领域事件与 render/layout 模型分开？
- 是否由组件发出 intent，而不是直接写仓储？
- 是否在提交失败时重新以本地事实来源为准？
- 是否保持全天、跨日、重复、时区和夏令时语义？
- JS 是否仍然只做浏览器测量和高频交互辅助？
- 新样式是否使用现有主题与排版 token？
- 是否避免改变同步格式、SyncOutbox 和提供商语义？
