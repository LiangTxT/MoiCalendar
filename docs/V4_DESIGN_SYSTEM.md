# MoiCalendar V4 设计系统

状态：R1 已建立，R9 质量门槛已通过；后续改动只能使用本文件定义的语义  
视觉方向：Operational Minimalism / 克制的战术编辑感

## 1. 原则

界面优先级固定为：正确、顺滑、简洁、帅。日历画布是视觉主角；结构依靠排版、细线、留白和背景层级，不依靠大卡片、渐变或阴影。

禁止使用：大圆角卡片、嵌套卡片、玻璃拟态、装饰渐变、巨大阴影、全局胶囊控件、统计 Dashboard、emoji 图标和无目的循环动画。

## 2. 页面结构

桌面只有四个主要区域：

```text
CommandHeader  48px
NavigationPane 220–248px
CalendarCanvas 剩余空间
ContextInspector 280–320px
```

Tablet 折叠 Navigation 和 Inspector；Phone 使用 Navigation drawer、Inspector sheet，Calendar 始终占主表面。

## 3. Token

Token 的事实来源是 `wwwroot/css/v4.css`。

### 颜色

| 语义 | Light | Dark |
| --- | --- | --- |
| Canvas | `#F3F3EF` | `#141719` |
| Surface | `#FAFAF7` | `#1A1E20` |
| Elevated | `#FFFFFF` | `#222729` |
| Ink | `#171A1C` | `#F1F2EE` |
| Secondary | `#62676A` | `#ABB0B1` |
| Tertiary | `#676D6A` | `#969E9F` |
| Line | `#D9DBD7` | `#343A3C` |
| Line Strong | `#BFC3BF` | `#51595B` |
| Primary | `#283033` | `#DDE2E2` |
| Signal | `#E1C443` | `#E1C443` |
| Information | `#4AAAB6` | `#65BEC7` |
| Focus / Link | `#277680` | `#65BEC7` |
| Success | `#3F8B67` | `#67B28B` |
| Warning | `#CF9138` | `#D9A558` |
| Danger | `#CA5454` | `#E07474` |

日历实体颜色是内容数据，不属于品牌强调色。

### 间距

只使用 `4 / 8 / 12 / 16 / 24 / 32 / 48px`，对应 `--space-1` 至 `--space-7`。

### 圆角

- `0px`：大结构
- `3px`：事件、小控件
- `6px`：按钮、输入框
- `8px`：popover、sheet、浮层

胶囊只允许用于极少数状态标记和 today 日期圆点。

### 阴影

- `--shadow-popover`：菜单、popover
- `--shadow-overlay`：拖动镜像、升起的 overlay

Header、Navigation、Inspector、Calendar、Settings section 和 Event 禁止阴影。

### 动效

- Fast：120ms
- Normal：180ms
- Panel：240ms
- Major：320ms

拖动和缩放预览没有装饰延迟。所有动效必须尊重 `prefers-reduced-motion`。

## 4. 字体

V4 Beta 只使用系统字体栈。语义等级：

- DisplayDate：32–40px / 600
- PeriodTitle：20–24px / 600
- SectionTitle：13–14px / 600
- Body：13–14px / 400
- Event：12–13px / 500
- Meta：10–11px / 600 / uppercase / tracking
- Time：11–12px / tabular numerals

战术编辑感来自 Display 与 Meta 的秩序，不来自特殊字体。

## 5. 状态与可访问性

- Hover 只能强化已有层级，不能造成布局变化。
- Focus 使用 2px focus outline，始终可见；浅色模式采用更深的同色系值，链接与正文背景的对比度达到 4.5:1。
- Selected 使用细线、局部底色或 signal 标记，不使用大面积品牌色。
- Error 使用 danger 文本和边线；正常同步状态保持安静。
- 触控目标原则上不小于 40px；高密度日历事件可更小，但必须有等价可访问名称和可聚焦路径。

## 6. 月份导航轨道

连续月历保留五个月窗口和无限滚动，隐藏会随窗口回收跳动的原生纵向滑块。右侧使用固定 44px 宽的月份导航轨道：年份标头、1–12 月原生按钮；当前月以局部底色与细线标记，刻度位置不随窗口替换移动。轨道不是模拟滚动条，不显示虚假的全年滚动百分比，也不添加拖动业务逻辑。

轨道读取页面唯一的显示月份状态；点击或 Enter / Space 跳转到该年份对应月份，滚轮仍在日期区域连续浏览，跨年时只更新年份与当前月标记。按钮支持 Tab 与可见焦点，轨道内按键不被全局日历快捷键拦截。粗指针目标高度至少 44px；高度不足时轨道自身可滚动。周/日视图的原生滚动条保持不变。

## 7. 统一原生滚动条

滚动区域显式使用 `app-scrollable`，不全局覆盖所有元素。`v4.css` 中的 `--scrollbar-*` 是唯一外观来源：8px 原生点击轨道、2px 透明内边框形成约 4px 视觉滑块，透明背景、圆头、无箭头与阴影。浅色使用低透明中性灰，深色使用低透明白，拖动不使用品牌色。

- Default：周/日两轴、日程、日历外层内容、设置/帮助/隐私/条款/状态页、完整编辑器。
- Subtle：侧栏、详情、快速创建、更多事件、搜索、重复范围选择、备注文本框、短屏月份导航轨道。空闲透明，滚动或鼠标进入时显现。
- Hidden：仅连续月历窗口，按用户确认保留固定月份导航轨道；不在设置或周/日主时间轴隐藏滚动条。

`scrollbar.js` 仅通过一条 passive 捕获监听读取实际 scroll 事件。当前目标使用 `is-scrolling`，rAF 合并 class 写入；每个活动区域最多保留一个 650ms 防抖计时器，减少动态效果时为 100ms。挂起、重复加载与卸载清理计时器/监听，bfcache 恢复重新启用。不读几何、不写 scrollTop、不创建指示器 DOM、不调用 C# 或存储。

原生 CSS 控制空闲/滚动/容器悬停/滑块悬停/拖动状态，120ms 显现、220ms 回落；原生伪元素动画支持取决于浏览器，允许状态切换而非精确淡入帧，不用复杂动画补丁。此次未采用 Overlay Indicator。强制高对比模式保留系统样式，触屏不启用 hover 增强，减少动态效果关闭过渡。稳定 gutter 与时间轴现有测量同步避免状态改变时横移。
