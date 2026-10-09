# 手机顶部日期与浮动新建按钮 Implementation Plan

> **For agentic workers:** 按 superpowers:executing-plans 在当前已核对的 feature/calendar-ux-v2 工作区逐步执行，不创建其他工作区，不提交或发布。

**Goal:** 顶部单行显示日期和四种视图，只有新建按钮覆盖在表格左下角；日、周顶部日期复用月视图原有数字与单位样式。

**Architecture:** 保留 CalendarToolbar 的按钮和事件回调；为日、周标题传递 DateOnly 起止日期并渲染数字/单位。仅替换手机断点布局，不改表格、日程数据或持久化。完整日期由 aria-label 保留，手机隐藏年及星期等次要片段以适应窄屏，但月视图的年月格式保持不变。

**Tech Stack:** Blazor WebAssembly、C#、CSS Grid、Node 原生测试、.NET 测试。

## 完成记录

以上五项已执行。新增检查先失败再通过；201 项 JS 与 728 项 .NET 测试通过。Chromium 在 320/390/541/600/1260px 验证日期导航、四视图切换、新建弹窗及浮动按钮。独立审查提示的 541px 跨年周裁切已复现并修正（标题宽与滚动宽从 247/257 变为 206/206）。月视图字体和格式、表格日期均未更改；未使用真机或 Safari 验证。截图留在仓库外，不提交或部署。

## 执行步骤

- [ ] 更新 scripts/tests/calendar-mobile-layout.test.mjs：断言只有 add-event-button 使用 fixed，左侧和底部 safe-area，去除 84px 底部留白，视图按钮仍在顶部；新增日周 period-number/period-unit 与 DateOnly 参数测试。先运行 `node --test scripts/tests/calendar-mobile-layout.test.mjs` 并确认新行为尚未实现。
- [ ] CalendarToolbar.razor 与 Home.razor：传入 PeriodStartDate/PeriodEndDate；日视图拆分年月日、星期，周视图拆分起止日期、跨月/跨年单位，复用现有字体。保留 MonthYear/MonthNumber 的年月标记与所有 onclick。
- [ ] v4.css：共享 period-number/period-unit 样式，保留原字体数值。手机 toolbar 使用三列 28px/minmax(0,1fr)/auto、48px 单行；日期导航使用 24px/弹性日期/24px。四种视图按钮紧凑无换行；只有新建按钮 fixed、48px、左下 16px + safe-area、z-index 20。删除底部留白，月视图最小高度保持可收缩。
- [ ] `node --test scripts/tests/*.test.mjs`；`dotnet test --no-restore`。验证 320px、390px、桌面浏览器：四种视图切换、新建弹窗关闭、顶栏几何不相交、悬浮按钮与表格几何相交、错误日志。截图保存在仓库外。
- [ ] 检查 diff 仅包含指定源文件、测试和本计划，最后 `dotnet build --no-restore`；不提交、不部署。
