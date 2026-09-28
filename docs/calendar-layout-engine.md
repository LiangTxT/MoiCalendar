# MoiCalendar 日历布局引擎

状态：已实现（Module 34）  
适用范围：Month、Week、Day 的纯布局计算  
最后更新：2026-09-23

## 设计边界

布局引擎位于 `MoiCalendar.Core`，只接收已经转换到显示时区的渲染输入、可见日期范围和布局指标，返回不可变渲染结果。它不访问 DOM、CSS、IndexedDB、SyncOutbox 或云服务，也不修改 `CalendarEvent`。

`CalendarRenderEvent.LocalStart` 和 `LocalEnd` 使用半开区间 `[开始, 结束)`。全天事件的结束时间同样不包含在事件内，因此 9 月 7 日至 9 月 10 日表示占用 7、8、9 三天。时区及夏令时换算必须在创建渲染输入之前完成。

## 计算流程

```text
CalendarRenderEvent（显示时区）
  + CalendarVisibleRange
  + TimeGridMetrics / 可用事件行数
  -> MultiDayLayoutEngine
  -> CalendarRenderSegment
  -> MonthLayoutEngine / AllDayLayoutEngine / TimeGridLayoutEngine
  -> 视图渲染模型
```

### 可见范围

`VisibleRangeLayoutEngine` 统一计算半开日期范围：

- Month：从包含当月 1 日的首周起，固定六周、42 天；
- Week：从配置的每周第一天起，共七天；
- Day：锚点日期的一天；
- Agenda：锚点所在自然月，不包含月视图的前后补位日期。

默认每周从星期一开始，也可以显式传入其他起始星期。

### 跨日与可见范围裁剪

`MultiDayLayoutEngine` 把一个渲染事件拆成每日 `CalendarRenderSegment`，所有片段保留相同 `EventId`，不会创建新的持久化事件。结束时间恰好为午夜时归属于前一天，避免产生零长度的次日片段。

每个片段携带：

- `StartsBeforeVisibleRange` / `EndsAfterVisibleRange`：事件是否被当前可见范围裁剪；
- `ContinuesFromPreviousDay` / `ContinuesToNextDay`：片段是否与相邻日期连续；
- `Position`：`Single`、`Start`、`Middle` 或 `End`。

### 时间网格坐标

`TimeGridCoordinateConverter` 是分钟与纵向逻辑百分比之间的唯一转换入口：

```text
Y% = (分钟 - 可见开始分钟) / 可见总分钟 × 100
分钟 = 可见开始分钟 + Y% × 可见总分钟
```

`TimeGridMetrics` 定义可见起止分钟和最小显示时长。`TimeGridLayoutEngine` 先裁剪到可见时段，再计算 `TopPercentage` 与 `HeightPercentage`。短事件可以获得最小可读高度，但其领域持续时间不会被修改。指针交互也复用同一反向转换。

### 定时事件重叠

`OverlapLayoutEngine` 使用稳定的扫描分配：

1. 按开始时间、较长持续时间、来源顺序和稳定 ID 排序；
2. 通过最晚结束时间建立传递碰撞组，因此链式重叠仍属于同一组；
3. 把事件放入第一个已经空闲的逻辑列，否则新建一列；
4. 为组内所有事件返回一致的 `ColumnCount`；
5. 由 `Column / ColumnCount` 直接得到 `LeftPercentage` 和 `WidthPercentage`。

边界相接但没有时间交集的事件可以复用列。最小显示高度参与视觉碰撞检测，避免短事件在同一位置完全遮盖。

### 全天事件

`AllDayLayoutEngine` 先把全天事件裁剪到可见范围，再按日历网格行拆段。每个网格行内使用稳定的区间行分配；当上一事件的包含结束日期早于新事件开始日期时，该显示行可以复用。跨周事件是同一个 `EventId` 的多个渲染片段。

### 月格溢出

`MonthLayoutEngine` 对每一天统一排序和裁剪事件行，返回 `VisibleEvents` 与 `OverflowCount`。Razor 日格只消费结果，不自行重复实现“+N 更多”的计算规则。

## 确定性与测试护栏

布局结果只取决于输入值，不取决于输入集合原有顺序、DOM 尺寸或 CSS。固定日期测试覆盖：无重叠、两重重叠、三重嵌套、链式重叠、相同起止时间、跨午夜、跨多日、全天、月格溢出和可见范围裁剪。

## Week / Day 共享时间网格（Module 36）

周视图和日视图只使用一个 `CalendarTimeGrid`。日视图通过 `VisibleDate` 把同一周数据投影为单列，并不会启用另一套时间轴算法。共享组成如下：

```text
CalendarTimeGrid
  ├─ TimeGridHeader
  ├─ AllDayPanel
  ├─ CurrentTimeIndicator（独立分钟计时器）
  └─ TimedEventBlock
       └─ CalendarTimeGridProjection
            └─ TimeGridLayoutEngine
```

- `CalendarTimeGridProjection` 只负责周/日可见列选择和定时事件布局适配；
- `VisibleStartHour` / `VisibleEndHour` 生成唯一 `TimeGridMetrics`，标签、事件、交互预览和当前时间线都消费同一指标；
- 全天事件始终留在 `AllDayPanel`，不会转换为 00:00–24:00 的定时块；
- 当前时间由 `CurrentTimeIndicator` 局部刷新，不重新查询事件，也不触发持久化或同步；
- JS 只记忆周、日两个滚动上下文并测量滚动条宽度，确保表头、全天区和时间列对齐；
- 初次进入定位到当前时间前约两小时，若可见范围不含今天则定位到 `DefaultScrollHour`；用户滚动后切换回来会恢复原位置。
