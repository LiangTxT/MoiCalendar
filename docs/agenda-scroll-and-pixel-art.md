# 日程列表：自动向前加载与季节像素画

## 滚动加载

- 初次展示当前月份并预读后两个月。向上滚动接近列表顶部（260px 内）时，通过现有 `CalendarEventService` 读取前一个月，无需点击按钮。
- 列表已在顶部时，也响应继续向上的滚轮、单指下拉和向上翻页按键，不拦截原生滚动。
- 每次只允许一个扩展请求。插入早期月份时补偿内容高度差，保持已有内容的位置；禁用浏览器自动锚定，避免双重补偿。
- 月份切换使用加载版本校验，丢弃过期结果。读取失败保留已加载内容，并提供显式重试；组件销毁时清理观察器和事件监听。
- JavaScript 只协调滚动与视口，不持久化事件，不改变同步语义。

## 图集

当前日程横幅已改用十二张单独生成的月份图片，直接显示完整图片，不再使用下述图集定位。文件、实际尺寸与完整提示词集合见 [独立月份像素画](agenda-month-art-prompts.md)。图集仅保留为历史资产。

`src/MoiCalendar.App/wwwroot/images/agenda-seasons-pixel-v3.png`：2172×724，三列四行，按一月至十二月排列。使用内置 imagegen 重画，不使用 CLI；保留原来的十二个月季节主题以及旧版 v2 文件。新版减少细碎纹理，强调清晰色块与像素轮廓。

横幅现在按每格原有的 4:1 比例完整显示，取消固定高度、额外放大及中心裁切。窄屏把月份标题放在图片上方，保留完整画面；清晰像素采样不变。遵循 Impeccable 的窄范围优化方式，保留既有主题与日程操作。

上一轮两次内置 imagegen 请求因连接错误失败。本轮重试成功，保持现有 4:1 单格比例，避免引入新的裁切或改变月份映射。八月村庄屋顶完整保留。实际生成图并非严格的 128×32 原生像素图，不将提示词中的目标分辨率当作实测结果。

v3 最终生成提示词（内置 imagegen，编辑输入为 v2）：

```text
Use case: style-transfer. Edit target: the attached seasonal calendar landscape atlas. Redraw it as genuinely coarse, premium hand-crafted pixel art, NOT pixelated photography. Preserve all twelve seasonal subjects and month order. ONE atlas EXACTLY 3 columns x 4 rows, no gutters, no borders; overall aspect ratio 3:1 and each individual scene exactly4:1. Clear deliberately large square pixel clusters, a virtual native resolution of approximately128x32 per panel, sharply nearest-neighbor enlarged. Restrained 24-color palette per scene. Broad beautiful silhouettes, simple readable forms, artful color clusters and atmospheric depth. NO tiny granular texture, NO photorealistic painting, NO blur, NO anti-aliasing, NO excessive dithering, NO fine noise. Preserve January snowy river; February snowy village bridge; March willow thawing lake; April blossom orchard meadow; May green countryside farmhouse; June rainy summer lake; July leafy river; August golden wheat hills and hilltop village; September harvest hay bales farmhouse ochre tree; October autumn maple woodland cottage stream; November russet riverside village bridge; December snowy pine mountain lake. Compose every tile as an entire panoramic landscape: complete building rooftops, generous sky above rooflines, whole foreground, nothing important cut at top or bottom. August's village roofs must be fully visible. Make pixels visually much larger and crisper than the edit target, not merely sharpen the old illustration. Match existing calm Sage theme, harmonious cream skies, sage foliage, blue-gray distance and warm amber fields. No letters, titles, logos, interface or watermark. High-quality game-background pixel craftsmanship.
```

v2 原始提示词（历史记录）：

```text
Use case: stylized-concept. Asset: ONE production sprite atlas for a Chinese personal calendar's monthly panoramic banners. Create a single polished pixel-art seasonal landscape atlas, EXACTLY 3 columns by 4 rows of twelve equally sized seamless rectangular panels, touching with NO gutters, NO borders, NO text or numbers. Overall image aspect 3:1, ideally 3072x1024 pixels: every panel aspect4:1. Read order left to right top to bottom represents January through December. Preserve scene sequence: January quiet snowy river and bare trees; February snow-covered small village with stone bridge and distant church; March thawing lake, budding yellow-green willow branches; April blooming white orchard and little meadow flowers; May lush sage-green rolling countryside with farmhouse; June soft summer rain over a lake with reeds; July bright leafy riverside with sparkling blue-green water; August sweeping golden wheat hills and distant hilltop village; September harvested wheat field with round hay bales, farmhouse and mature ochre tree; October orange maple woodland, stone cottage beside a stream; November russet riverside town, bridge and still reflections; December snowy pines against majestic mountains. Art direction: meticulous premium hand-placed 16-bit pixel art, crisp square pixel clusters, controlled limited palettes, beautiful atmospheric depth and coherent perspective; like a painstakingly crafted indie game's peaceful landscape backgrounds, not photos converted with a pixel filter. Native visual pixel scale approx256x64 per panel, integer enlarged sharply; no blur, no antialiasing, no painterly strokes, no photorealism, no noise/grain. Distinct seasonal palettes with restrained Sage greens, cream skies, blue-gray distance, warm amber harvest and orange autumn. Rich but readable foreground detail, calm skies in upper-left of every tile for month title overlay; strong silhouettes, elegant pixel dithering only where intentional. All important compositions remain legible inside a very wide shallow banner. No people, UI, frames, typography, watermark or logo. Finish all twelve panels carefully at consistent quality.
```

## 验证

- v3 接入：98 个 Node 测试、613 个 .NET 测试通过；图集尺寸与十二个月映射、完整显示和窄屏布局约束均通过回归检查。生成结果已目视检查，未修改日程数据或同步逻辑。

- 新增 11 个 JavaScript 回归测试，覆盖顶部上滑、触屏阈值、并发去重、锚点补偿、月份切换、失败重试及监听清理。
- 完整验证：613 个 .NET 测试、75 个 Node 测试通过；最后的构建通过，0 警告、0 错误；本地 Release 发布构建通过（未安装可选 wasm-tools 优化工作负载）。
- 本轮浏览器验收接口不可用，尚未完成真实浏览器中的桌面/手机截图验收；自动化结果不替代实机视觉验收。未部署线上。
# 月份铭牌（2026-10-06）

日程插图上的月份标题改为紧凑的墨色铭牌：月份为主信息，年份通过细分隔线弱化。采用项目原有字体与中性色，4px 切角呼应像素插图，不使用游戏素材、玻璃效果、阴影或额外动画。参考《星露谷物语》的日期 HUD 信息分组（https://stardewvalleywiki.com/Day_Cycle），仅借鉴层级而非复制外观。

桌面保持左上角定位；手机将铭牌置于插图上方，保证完整画面不被遮挡。标题保留完整中文可访问名称，`time` 提供年月机器格式；高对比模式使用系统颜色。图片比例、无限滚动和日程服务调用均保持不变。

