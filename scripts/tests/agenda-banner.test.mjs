import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const base = new URL('../../src/MoiCalendar.App/', import.meta.url);
const css = readFileSync(new URL('wwwroot/css/v4.css', base), 'utf8');
const component = readFileSync(new URL('Components/AgendaMonthSection.razor', base), 'utf8');
const sizes = Array.from({ length: 12 }, (_, i) => {
    const month = i + 1;
    const bytes = readFileSync(new URL('wwwroot/images/agenda-month-' + String(month).padStart(2, '0') + '-pixel-v1.png', base));
    assert.equal(bytes.subarray(1, 4).toString(), 'PNG');
    return { month, width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20), hash: createHash('sha256').update(bytes).digest('hex') };
});

test('十二个月均有独立且不同的完整横向图片', () => {
    assert.equal(new Set(sizes.map(image => image.hash)).size, 12);
    for (const { width, height } of sizes) {
        assert.ok(width >= 1900);
        assert.ok(width / height >= 2.4 && width / height <= 4);
    }
});

test('月份直接映射独立文件，不再定位或裁切图集', () => {
    assert.ok(component.includes('images/agenda-month-{month:D2}-pixel-v1.png'));
    assert.ok(component.includes('src="@BannerSource(Month.Month)"'));
    assert.doesNotMatch(component, /BannerStyle|agenda-art-column|agenda-art-row/);
    assert.doesNotMatch(css, /agenda-seasons-pixel|\.agenda-month-banner::before/);
});

test('图片尺寸元数据与原图一致，加载前可预留稳定空间', () => {
    assert.ok(component.includes('width="@BannerSize(Month.Month).Width" height="@BannerSize(Month.Month).Height"'));
    assert.ok(component.includes('loading="lazy" decoding="async"'));
    const section = component.match(/BannerSize\(int month\) => month switch\s*\{([^}]+)\}/)[1];
    const entries = [...section.matchAll(/([\d or]+|_) => \((\d+), (\d+)\)/g)];
    for (const { month, width, height } of sizes) {
        const entry = entries.find(match => match[1].trim() !== '_' && match[1].split('or').map(Number).includes(month)) ?? entries.find(match => match[1].trim() === '_');
        assert.deepEqual([Number(entry[2]), Number(entry[3])], [width, height], month + '月尺寸');
    }
});

test('横幅按自然比例完整显示，不放大 cover、不限定高度', () => {
    const banner = css.match(/\.agenda-month-banner \{([^}]+)\}/)[1];
    const art = css.match(/\.agenda-month-art \{([^}]+)\}/)[1];
    assert.doesNotMatch(banner, /height:|aspect-ratio:|clamp\(/);
    assert.match(art, /width: 100%/);
    assert.match(art, /height: auto/);
    assert.match(art, /image-rendering: pixelated/);
    assert.doesNotMatch(art, /transform:|cover|max\(/);
});

test('手机标题与画面分开，不遮盖或裁切风景', () => {
    assert.ok(css.includes('.agenda-month-art { order: 2; }'));
    assert.ok(css.includes('.agenda-month-banner h2 { position: static; order: 1;'));
    assert.match(component, /class="agenda-month-art"[^>]+alt=""/);
});

test('月份铭牌区分年月层级，并保留完整可访问日期', () => {
    assert.ok(component.includes('h2 aria-label="@($"{Month.Year}年{Month.Month}月")"'));
    assert.ok(component.includes('datetime="@($"{Month.Year:D4}-{Month.Month:D2}")"'));
    assert.ok(component.includes('class="agenda-month-number"'));
    assert.ok(component.includes('class="agenda-month-year"'));
    const stamp = css.match(/\.agenda-month-stamp \{([^}]+)\}/)[1];
    assert.match(stamp, /font-variant-numeric: tabular-nums/);
    assert.doesNotMatch(stamp, /backdrop-filter|box-shadow|gradient/);
    assert.ok(css.includes('@media (forced-colors: active)'));
});
