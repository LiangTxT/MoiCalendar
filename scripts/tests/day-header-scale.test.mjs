import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const css = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/css/v4.css', import.meta.url), 'utf8');
const rule = selector => css.slice(css.indexOf(`\n${selector} {`) + 1).split('}')[0];

test('日视图与周视图共用54px日期栏高度，不使用视觉缩放造成空白', () => {
    assert.match(rule('.week-grid-frame'), /grid-template-rows: 54px auto minmax\(0, 1fr\)/);
    assert.doesNotMatch(rule('.week-calendar.day-mode .week-grid-frame'), /grid-template-rows/);
    assert.match(rule('.day-date-navigation'), /min-height: 54px/);
    const day = css.slice(css.indexOf('.day-date-navigation {'), css.indexOf('.week-calendar.day-mode .timed-event-metadata'));
    assert.doesNotMatch(day, /scale\(|zoom:|70px/);
});

test('日期圆点、数字、星期和农历同步收敛，手机三行内容不超过栏高', () => {
    assert.match(rule('.day-date-navigation strong'), /width: 24px;\s*height: 24px;\s*font-size: 13px;/);
    assert.match(rule('.day-date-navigation .day-date-main'), /font-size: 10px;\s*line-height: 14px;/);
    assert.match(rule('.day-date-navigation .day-lunar-label'), /font-size: 9px;\s*line-height: 12px;/);
    assert.match(css, /\.day-date-navigation strong \{ width: 22px; height: 22px; font-size: 12px; \}/);
    assert.ok(12 + 22 + 12 + 2 <= 54);
});
