# MoiCalendar V4 产品质量预算

状态：R9 固化；违反预算视为回归。

## 交互预算

| 场景 | 预算 |
| --- | --- |
| Hover 反馈 | 感知延迟小于 100ms |
| Click 选择反馈 | 立即；单/双击判定窗口最多 180ms |
| Popover | 不超过 200ms |
| Inspector | 不超过 250ms |
| Drag / Resize | 指针移动时无装饰延迟；每帧最多一次 DOM 更新 |
| 持久化提交 | pointerup 后只向 .NET 提交一次 intent |

高频 pointermove 只能在 `calendarInteraction.js` 内处理坐标、镜像和边缘滚动，不得写 IndexedDB、调用网络或触发完整 CalendarCanvas 重渲染。未超过 6px 移动阈值的点击不得捕获指针或调用 .NET；触控拖动还需满足 350ms hold 阈值。

## 架构预算

| 项目 | 上限 |
| --- | --- |
| Calendar 页面状态所有者 | 1（`Home.razor`） |
| TimeGrid 实现 | 1（Week 与 Day 共用 `CalendarTimeGrid`） |
| OverlayHost | 1 |
| Interaction intent 模型 | 1 套 Core intent |
| Inspector 状态 | 1 个判别状态 |
| Design token 系统 | 1（`v4.css`） |
| 持久化事实来源 | 1（IndexedDB） |

UI 和浏览器交互脚本不得直接拥有持久化、同步或网络业务规则。

## 视觉预算

| 项目 | 上限 |
| --- | --- |
| 嵌套卡片 | 0 |
| 大卡片圆角 | 0 |
| Calendar / Header / Navigation / Inspector 阴影 | 0 |
| 品牌强调色系统 | 1 |
| 阴影等级 | 2 |
| 圆角值 | 4（0 / 3 / 6 / 8px） |
| Spacing 值 | 7（4 / 8 / 12 / 16 / 24 / 32 / 48px） |
| Motion 时长 | 4（120 / 180 / 240 / 320ms） |
| 正式字体系统 | 1（系统字体） |

## 响应式预算

- 375、768、1024、1440 和宽屏均不得产生页面级水平滚动。
- Calendar workspace 固定为一个视口高；TimeGrid 自己滚动，不能撑高页面。
- 1180px 以下 Navigation 默认折叠；720px 以下 Inspector 和 Overlay 使用 sheet。
- 桌面 Navigation 为 236px，Inspector 为 304px，CalendarCanvas 使用全部剩余空间。

## 验证门槛

每次重要前端改动至少运行相关测试、完整 `dotnet test`、最终 `dotnet build`。涉及 PWA、IndexedDB 或资源图谱时还要运行 Release publish，并检查六张固定截图。
