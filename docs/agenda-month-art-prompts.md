# 独立月份像素画

十二个月使用十二次独立的内置 imagegen 请求生成，每张都是单一场景，未从图集裁切。旧图集保留但不再用于日程横幅。三月、四月连接失败后分别重试成功。

文件：`src/MoiCalendar.App/wwwroot/images/agenda-month-01-pixel-v1.png` 至 `agenda-month-12-pixel-v1.png`。

显示使用原图宽高属性和 `width:100%;height:auto` 预留空间、不裁切；手机标题独立排列。实际输出比例约 2.5:1 至 3:1，不把提示词的4:1或虚拟像素目标当作实测值。

## 完整提示词集合

验证：5 项月份图片专项测试、完整 107 项 JavaScript 测试、613 项 .NET 测试通过；最后构建通过，0 警告、0 错误。Chromium 桌面 1440×900 与手机 375×812 布局验收确认图片自然比例、尺寸预留、手机标题分离且无横向溢出；未做真实 iPad/Android 设备验收。截图保存在 `artifacts/ui-audit-20261002/agenda-independent-desktop.png` 与 `agenda-independent-mobile.png`。未部署线上。

每次请求的最终提示词由下列共同前缀与对应月份场景直接连接，均为内置生成模式，无参考图片、无 CLI。

```text
Use case: stylized-concept. Asset: ONE independent monthly panoramic banner for MoiCalendar, not an atlas, not a collage, no split panels. Single landscape aspect ratio4:1, ideally2048x512. Premium hand-crafted COARSE pixel art for a calm personal calendar. Large visibly square pixel clusters, broad carefully composed forms, approximately256x64 native visual resolution integer-enlarged; restrained24-32 colors, flat clusters and strong silhouettes with atmospheric depth. NO blurry painting, no photos, no antialiasing, no fine granular texture, no excessive dithering. Whole scenic composition: complete rooftops, full foreground and generous sky, nothing important clipped. Harmonious sage greens, cream highlights and blue-gray distance; season-specific accent palette. Calm upper-left sky suitable for a title overlay. No people, letters, month names, logo, border, watermark or UI. 
```

### 1月

```text
January: a quiet snowy river with bare dark trees, snowy pines, distant blue mountains, soft winter sky. Icy blue and cream.
```

### 2月

```text
February: a snow-covered small stone village beside a stone bridge and river, distant slender church tower, winter sunshine. All roofs and tower tip entirely inside the frame.
```

### 3月

```text
March: a thawing lake with budding yellow-green willow branches, distant mountains, fresh spring light.
```

### 4月

```text
April: a white-blossoming orchard beside a small meadow path and wooden fence, fresh green grass and small flower clusters.
```

### 5月

```text
May: lush sage-green rolling countryside with a small warm stone farmhouse, winding path and distant wooded hills.
```

### 6月

```text
June: soft summer rain over a tranquil lake with reeds and willow, muted blue-green hills and a distant farmhouse, gentle readable pixel rain.
```

### 7月

```text
July: a bright leafy riverside with blue-green water, rounded rocks and summer trees, clear blue sky, warm dappled light.
```

### 8月

```text
August: sweeping golden wheat hills with a distant hilltop stone village and small bell tower. All building rooftops and tower tip entirely visible with generous sky. Strong amber foreground and sage green trees.
```

### 9月

```text
September: a harvested wheat field with round hay bales, a small farmhouse and mature ochre tree, golden late-summer light.
```

### 10月

```text
October: orange maple woodland with a small stone cottage beside a stream and low stone bridge, warm amber leaves and cool water.
```

### 11月

```text
November: a russet riverside town with a stone bridge and quiet reflections, bare branches, soft overcast cream-gray sky.
```

### 12月

```text
December: snowy pines around a still icy lake against majestic blue mountains, fresh snow and clear pale sky.
```

