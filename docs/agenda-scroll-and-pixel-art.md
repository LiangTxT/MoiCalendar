# 日程列表：自动向前加载与季节像素画

## 滚动加载

- 初次展示当前月份并预读后两个月。向上滚动接近列表顶部（260px 内）时，通过现有 `CalendarEventService` 读取前一个月，无需点击按钮。
- 列表已在顶部时，也响应继续向上的滚轮、单指下拉和向上翻页按键，不拦截原生滚动。
- 每次只允许一个扩展请求。插入早期月份时补偿内容高度差，保持已有内容的位置；禁用浏览器自动锚定，避免双重补偿。
- 月份切换使用加载版本校验，丢弃过期结果。读取失败保留已加载内容，并提供显式重试；组件销毁时清理观察器和事件监听。
- JavaScript 只协调滚动与视口，不持久化事件，不改变同步语义。

## 图集

`src/MoiCalendar.App/wwwroot/images/agenda-seasons-pixel-v2.png`：2172×724，三列四行，按一月至十二月排列。使用内置 imagegen 生成，不使用 CLI；保留原来的十二个月季节主题。横幅使用保持比例的裁切和清晰像素采样，不拉伸景物。遵循 Impeccable 的窄范围优化方式，保留既有 Sage 配色与月份文字样式。

最终生成提示词：

```text
Use case: stylized-concept. Asset: ONE production sprite atlas for a Chinese personal calendar's monthly panoramic banners. Create a single polished pixel-art seasonal landscape atlas, EXACTLY 3 columns by 4 rows of twelve equally sized seamless rectangular panels, touching with NO gutters, NO borders, NO text or numbers. Overall image aspect 3:1, ideally 3072x1024 pixels: every panel aspect4:1. Read order left to right top to bottom represents January through December. Preserve scene sequence: January quiet snowy river and bare trees; February snow-covered small village with stone bridge and distant church; March thawing lake, budding yellow-green willow branches; April blooming white orchard and little meadow flowers; May lush sage-green rolling countryside with farmhouse; June soft summer rain over a lake with reeds; July bright leafy riverside with sparkling blue-green water; August sweeping golden wheat hills and distant hilltop village; September harvested wheat field with round hay bales, farmhouse and mature ochre tree; October orange maple woodland, stone cottage beside a stream; November russet riverside town, bridge and still reflections; December snowy pines against majestic mountains. Art direction: meticulous premium hand-placed 16-bit pixel art, crisp square pixel clusters, controlled limited palettes, beautiful atmospheric depth and coherent perspective; like a painstakingly crafted indie game's peaceful landscape backgrounds, not photos converted with a pixel filter. Native visual pixel scale approx256x64 per panel, integer enlarged sharply; no blur, no antialiasing, no painterly strokes, no photorealism, no noise/grain. Distinct seasonal palettes with restrained Sage greens, cream skies, blue-gray distance, warm amber harvest and orange autumn. Rich but readable foreground detail, calm skies in upper-left of every tile for month title overlay; strong silhouettes, elegant pixel dithering only where intentional. All important compositions remain legible inside a very wide shallow banner. No people, UI, frames, typography, watermark or logo. Finish all twelve panels carefully at consistent quality.
```

## 验证

- 新增 11 个 JavaScript 回归测试，覆盖顶部上滑、触屏阈值、并发去重、锚点补偿、月份切换、失败重试及监听清理。
- 完整验证：613 个 .NET 测试、75 个 Node 测试通过；最后的构建通过，0 警告、0 错误；本地 Release 发布构建通过（未安装可选 wasm-tools 优化工作负载）。
- 本轮浏览器验收接口不可用，尚未完成真实浏览器中的桌面/手机截图验收；自动化结果不替代实机视觉验收。未部署线上。
