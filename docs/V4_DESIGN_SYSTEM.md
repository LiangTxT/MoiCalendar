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
| Tertiary | `#929796` | `#747A7B` |
| Line | `#D9DBD7` | `#343A3C` |
| Line Strong | `#BFC3BF` | `#51595B` |
| Primary | `#283033` | `#DDE2E2` |
| Signal | `#E1C443` | `#E1C443` |
| Information | `#4AAAB6` | `#65BEC7` |
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
- Focus 使用 2px information outline，始终可见。
- Selected 使用细线、局部底色或 signal 标记，不使用大面积品牌色。
- Error 使用 danger 文本和边线；正常同步状态保持安静。
- 触控目标原则上不小于 40px；高密度日历事件可更小，但必须有等价可访问名称和可聚焦路径。
