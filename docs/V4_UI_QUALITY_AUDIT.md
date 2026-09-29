# MoiCalendar V4 UI 质量审计

审计日期：2026-09-29  
结论：R0–R9 关键门槛通过；没有关键 FAIL。

## 模块结果

| 区域 | 状态 | 证据 |
| --- | --- | --- |
| R0 Core / UI 边界 | PASS | Core 无 UI/基础设施依赖；静态架构测试禁止 Razor 直连 IndexedDB、SyncOutbox、Supabase 与具体 provider |
| R1 Design System | PASS | 单一 `v4.css`；固定颜色、spacing、radius、shadow、motion 和系统字体；无渐变、glass/backdrop filter |
| R2 Shell | PASS | 48px Header、236px Navigation、剩余 CalendarCanvas、304px Inspector；平板折叠、手机 drawer/sheet |
| R3 Month | PASS | 7 列、固定 6 行、紧凑事件、跨日 segment 与 `+N`；密度测试覆盖 8 个事件 |
| R4 Week / Day | PASS | 两个入口共用一个 `CalendarTimeGrid`；52px/hour、半小时线、全天区、now indicator、内部滚动 |
| R5 Drag / Resize | PASS | pointermove、mirror、rAF、40px edge auto-scroll 在 JS；超过阈值后才 pointer capture；pointerup 单次 intent |
| R6 Inspector / Editor | PASS | 单击 Inspector、双击统一 Editor、Quick Create 保留草稿升级、Escape 关闭并返回焦点、单一 OverlayHost |
| R7 功能入口 | PASS | Settings 使用 GENERAL/ACCOUNT/DATA/SYSTEM 层级；现有本地备份、导入导出和既有账户入口保留，正常状态不侵入 CalendarCanvas |
| R8 Identity / Motion | PASS | 克制 micro-label、线性层级、120/180/240/320ms 动效与 reduced-motion |
| R9 Cleanup / Quality | PASS | 旧 `app.css`、`typography.css`、字体资产和多色/多字体偏好已移除；旧 TimeGrid pointermove 提交路径已删除 |

## 交互审计

| 检查项 | 状态 | 说明 |
| --- | --- | --- |
| 重复创建 / 重复拖拽提交 | PASS | Overlay 使用单一判别状态；活跃拖拽仅在一次 pointerup 调用 .NET |
| 普通点击误触拖拽 | PASS | 6px movement threshold；未激活手势不捕获指针也不调用 .NET |
| 触控滚动误拖 | PASS | touch 同时要求移动阈值和 350ms hold |
| Inspector 陈旧或打不开 | PASS | 浏览器实测 Week 事件单击打开；修复 pointerdown 过早捕获导致的 click 丢失 |
| 双击编辑 | PASS | 180ms 单击判定窗口；浏览器实测双击直接进入统一 Editor |
| 草稿丢失 | PASS | 浏览器实测 Quick Create 的标题经“更多详情”原样进入 Editor |
| Escape 与焦点返回 | PASS | 浏览器实测关闭 Editor 后焦点回到原事件按钮 |
| TimeGrid 页面跳动 | PASS | workspace 固定 100dvh，TimeGrid 在内部滚动；修复周视图撑高页面 |
| Day 偏好保存 | PASS | IndexedDB 白名单已包含 Day，并有静态回归守卫 |
| Drag / Resize 几何 | PASS | 隔离 QA origin 实测向下 52px 将 09:00–10:00 精确移动为 10:00–11:00；Resize 后精确变为 10:00–12:00 |

## 性能与边界审计

- Month 只消费预计算布局，不在组件重复实现布局算法。
- Week 与 Day 共享 TimeGrid 和 overlap engine。
- current-time indicator 自行更新显示，不查询或持久化事件集合。
- Calendar JS 不包含 IndexedDB、localStorage、fetch、XMLHttpRequest 或 Supabase 调用。
- Inspector/Overlay 打开只改变局部判别状态；同步健康状态不会弹出打断日历操作。

## 响应式与截图门槛

浏览器实测 375、768、1024、1440、1920 宽度；页面宽度未超出视口，Calendar workspace 未超出视口高度。固定截图：

- `docs/ui-audit/01-desktop-month.png`
- `docs/ui-audit/02-desktop-week.png`
- `docs/ui-audit/03-desktop-event-selected.png`
- `docs/ui-audit/04-quick-create.png`
- `docs/ui-audit/05-settings.png`
- `docs/ui-audit/06-mobile-day.png`

另保留 768 与 1024 的 R9 响应式截图作为辅助证据。

## 已知非阻断项

- 本地开发环境没有安装 `wasm-tools`，Release publish 成功但输出了“未进行可选 WASM 优化”的 SDK 提示；这不影响正确性或离线资源生成，正式发布环境可安装 workload 获得更小体积。
- 浏览器手测事件使用独立的 `127.0.0.1:5180` QA origin，不污染常用 `5179` 本地数据库。
