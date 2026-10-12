import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/MoiCalendar.App/Pages/Settings.razor', import.meta.url), 'utf8');
const css = readFileSync(new URL('../../src/MoiCalendar.App/Pages/Settings.razor.css', import.meta.url), 'utf8');

test('设备列表默认折叠，使用原生可访问展开控件并显示数量', () => {
    const disclosure = source.match(/<details class="cloud-device-disclosure"[^>]*>[\s\S]*?<\/details>/)?.[0];
    assert.ok(disclosure);
    assert.doesNotMatch(disclosure.split('>')[0], /\bopen\b/);
    assert.match(disclosure, /@key="cloudAccount.Id"/);
    assert.match(disclosure, /<summary>设备列表 · @cloudDevices.Count 台<\/summary>/);
    assert.match(disclosure, /class="cloud-device-list"/);
    assert.match(disclosure, /RenameCloudDeviceAsync/);
    assert.match(disclosure, /RevokeCloudDeviceAsync/);
    assert.match(disclosure, /不会撤销底层云账户登录会话/);
});

test('展开入口保留触屏点击面积、主题配色与键盘焦点', () => {
    assert.match(css, /\.cloud-device-disclosure > summary\s*\{[^}]*min-height: 44px/);
    assert.match(css, /\.cloud-device-disclosure > summary:focus-visible/);
    assert.doesNotMatch(css, /\.cloud-device-disclosure[^}]*display:\s*none/);
});
