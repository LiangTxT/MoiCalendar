import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const css = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/css/v4.css', import.meta.url), 'utf8');

test('手机视图切换与日期保持单行，仅新建按钮覆盖在左下角表格上', () => {
    const phone = css.slice(css.indexOf('@media (max-width: 540px)'), css.indexOf('@media (max-width: 400px)'));
    assert.match(phone, /grid-template-rows: 48px minmax\(0, 1fr\)/);
    assert.match(phone, /grid-template-columns: 28px minmax\(0, 1fr\) auto/);
    assert.match(phone, /grid-template-rows: 48px;/);
    assert.match(phone, /\.toolbar-actions \{[^}]*grid-column: 3;[^}]*grid-row: 1;/);
    assert.match(phone, /\.toolbar-actions \.add-event-button \{[^}]*position: fixed;[^}]*safe-area-inset-bottom[^}]*safe-area-inset-left[^}]*z-index: 20;/);
    assert.doesNotMatch(phone, /\.toolbar-actions \{[^}]*position: fixed/);
    assert.doesNotMatch(phone, /padding-bottom: calc\(84px/);
    assert.match(phone, /width: 48px;[^}]*height: 48px/);
    assert.match(phone, /\.month-period-title \.period-number \{ font-size: 20px/);
    assert.match(phone, /\.month-view \{ min-height: 0; \}/);
});

test('日周顶部日期使用月视图数字与单位，保留完整可访问日期与年月格式', () => {
    const toolbar = readFileSync(new URL('../../src/MoiCalendar.App/Components/CalendarToolbar.razor', import.meta.url), 'utf8');
    const home = readFileSync(new URL('../../src/MoiCalendar.App/Pages/Home.razor', import.meta.url), 'utf8');
    assert.match(toolbar, /DateOnly PeriodStartDate/);
    assert.match(toolbar, /DateOnly PeriodEndDate/);
    assert.match(toolbar, /period-number">@PeriodStartDate\.Day/);
    assert.match(toolbar, /period-number">@PeriodEndDate\.Day/);
    assert.match(toolbar, /class="command-period-title range-period-title"/);
    assert.match(toolbar, /aria-label="@PeriodTitle"/);
    assert.match(toolbar, /period-number">@MonthYear<\/span><span class="period-unit">年/);
    assert.match(home, /PeriodStartDate="@\(displayMode == CalendarViewMode.Week \? weekView.Week.StartDate : selectedDate\)"/);
    assert.match(css, /\.range-period-title \.period-number,/);
    assert.match(css, /\.range-period-title \.period-unit,/);
    assert.match(css, /\.period-year \{ display: none; \}/);
});

test('窄平板跨年周使用紧凑字号，不沿用桌面24px导致日期末尾被裁切', () => {
    const tablet = css.slice(css.indexOf('@media (max-width: 720px)'), css.indexOf('/* 手机日期和视图切换'));
    assert.match(tablet, /\.range-period-title \.period-number \{[^}]*font-size: 16px;/);
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
