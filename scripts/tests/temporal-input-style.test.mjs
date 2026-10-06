import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const css = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/css/v4.css', import.meta.url), 'utf8');
const component = readFileSync(new URL('../../src/MoiCalendar.App/Components/TemporalInput.razor', import.meta.url), 'utf8');

test('日期与时间无图标，统一居中 D-DIN 字体与字号', () => {
    assert.doesNotMatch(component, /<svg|temporal-input-icon/);
    const rule = css.match(/:is\(\.quick-create-popover, \.event-editor\) \.temporal-input input \{([^}]+)\}/)[1];
    assert.match(rule, /font-family: "D-DIN", var\(--font-ui\)/);
    assert.match(rule, /font-size: 24px/);
    assert.match(rule, /font-weight: 400/);
    assert.match(rule, /text-align: center/);
    assert.match(rule, /padding: 12px;/);
    assert.doesNotMatch(css, /\.temporal-input-time input\s*\{/);
    assert.ok(component.includes('data-date-time-picker'));
    assert.ok(component.includes('"60"'));
});

test('D-DIN 使用有效本地字体和授权文件，纳入现有离线缓存规则', () => {
    const base = new URL('../../src/MoiCalendar.App/wwwroot/', import.meta.url);
    const bytes = readFileSync(new URL('fonts/d-din/D-DIN.woff', base));
    assert.equal(bytes.subarray(0, 4).toString(), 'wOFF');
    assert.equal(bytes.readUInt32BE(8), bytes.length);
    assert.match(css, /src: url\("\.\.\/fonts\/d-din\/D-DIN\.woff"\) format\("woff"\)/);
    assert.match(css, /font-display: swap/);
    assert.match(readFileSync(new URL('fonts/d-din/OFL-1.1.txt', base), 'utf8'), /Copyright \(C\) 2017 Datto Inc/);
    assert.match(readFileSync(new URL('fonts/d-din/FONTLOG.txt', base), 'utf8'), /Design: Charles Nix/);
    assert.ok(readFileSync(new URL('service-worker.published.js', base), 'utf8').includes('/\\.woff$/'));
});
