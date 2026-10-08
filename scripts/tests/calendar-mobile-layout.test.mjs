import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const css = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/css/v4.css', import.meta.url), 'utf8');

test('手机单行日期导航与底部悬浮栏，预留安全区域且避开月份轨道', () => {
    const phone = css.slice(css.indexOf('@media (max-width: 540px)'), css.indexOf('@media (max-width: 400px)'));
    assert.match(phone, /grid-template-rows: 48px minmax\(0, 1fr\)/);
    assert.match(phone, /grid-template-columns: 44px minmax\(0, 1fr\)/);
    assert.match(phone, /grid-template-rows: 48px;/);
    assert.match(phone, /\.toolbar-actions \{[^}]*position: fixed;[^}]*safe-area-inset-bottom[^}]*z-index: 20;/);
    assert.match(phone, /left: calc\(\(100% - 44px\) \/ 2\)/);
    assert.match(phone, /padding-bottom: calc\(84px \+ env\(safe-area-inset-bottom, 0px\)\)/);
    assert.match(phone, /min-width: 44px; min-height: 44px/);
    assert.match(phone, /\.month-period-title \.period-number \{ font-size: 20px/);
    assert.match(phone, /\.month-view \{ min-height: 0; \}/);
});

test('手机月格将公历农历和月份标记纵向排布，不截断标签', () => {
    const mobile = css.slice(css.indexOf('@media (max-width: 720px)'));
    assert.match(mobile, /\.month-view \.month-day-header \{[^}]*flex-direction: column;[^}]*min-height: 58px;/);
    assert.match(mobile, /\.month-view \.date-number \{[^}]*order: -1;[^}]*flex-shrink: 0;/);
    assert.match(mobile, /\.month-view \.month-day-meta \{[^}]*flex-direction: column;[^}]*overflow: visible;/);
    assert.match(mobile, /\.month-view \.month-marker,\s*\.month-view \.lunar-date \{[^}]*overflow: visible;[^}]*white-space: normal;[^}]*overflow-wrap: anywhere;/);
    const cell = readFileSync(new URL('../../src/MoiCalendar.App/Components/MonthDayCell.razor', import.meta.url), 'utf8');
    assert.match(cell, /Day.Date.Date.Day == 1/);
    assert.match(cell, /Observances.GetLunarLabel/);
    assert.match(cell, /Day.Date.DayNumber/);
});
test('时间网格允许横向滚动传递到外层七天画布，仅纵向限制穿透', () => {
    const css = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/css/v4.css', import.meta.url), 'utf8');
    const scroll = css.match(/\.week-timed-scroll\s*\{([^}]+)\}/)[1];
    assert.match(scroll, /overscroll-behavior-x:\s*auto/);
    assert.match(scroll, /overscroll-behavior-y:\s*contain/);
    assert.doesNotMatch(scroll, /overscroll-behavior:\s*contain/);
});
test('快速创建捕获安全锁失败，保留输入而不触发全局崩溃', () => {
    const home = readFileSync(new URL('../../src/MoiCalendar.App/Pages/Home.razor', import.meta.url), 'utf8');
    const save = home.slice(home.indexOf('private async Task CreateQuickEventAsync()'), home.indexOf('private void PromoteQuickCreateToEditor()'));
    assert.match(save, /catch \(SyncOperationException\)/);
    assert.match(save, /quickCreateError = .*HTTPS/);
});
