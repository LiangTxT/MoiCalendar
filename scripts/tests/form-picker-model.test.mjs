import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
const context = vm.createContext({ window: {} });
vm.runInContext(readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/formPickerModel.js', import.meta.url), 'utf8'), context);
const model = context.window.moicalendarPickerModel;
test('民用日期严格验证，不自动修正无效日期', () => {
    for (const value of ['2026-02-29', '2026-13-01', '0000-01-01', '2026-10-00', '2026-1-01', '', null]) assert.equal(model.parseDate(value), null);
    for (const value of ['2024-02-29', '0001-01-01', '9999-12-31', '2026-10-06']) assert.equal(model.dateKey(model.parseDate(value)), value);
});
test('月历固定六行、周一开头、包含邻月日期', () => {
    const cells = model.calendarCells('2026-10-06');
    assert.equal(cells.length, 42); assert.equal(cells[0].value, '2026-09-28'); assert.equal(cells[41].value, '2026-11-08');
    assert.equal(cells[0].adjacent, true); assert.equal(cells[3].adjacent, false);
    for (let i = 1; i < cells.length; i++) assert.equal(model.shiftDate(cells[i - 1].value, 1), cells[i].value);
});
test('跨月翻页保留日期并裁剪月末，正确处理闰年', () => {
    assert.equal(model.shiftMonth('2026-01-31', 1), '2026-02-28');
    assert.equal(model.shiftMonth('2024-01-31', 1), '2024-02-29');
    assert.equal(model.shiftMonth('2026-12-30', 1), '2027-01-30');
    assert.equal(model.shiftMonth('2026-03-31', -1), '2026-02-28');
});
test('年份边界不可越界，边缘月份的邻年日期禁用', () => {
    assert.equal(model.shiftMonth('0001-01-01', -1), null); assert.equal(model.shiftMonth('9999-12-01', 1), null);
    assert.equal(model.shiftDate('0001-01-01', -1), null); assert.equal(model.shiftDate('9999-12-31', 1), null);
    assert.ok(model.calendarCells('9999-12-01').some(cell => !cell.valid));
});
test('上下方向键日期跨午夜不使用本地夏令时', () => {
    assert.equal(model.shiftDate('2026-03-08', 1), '2026-03-09');
    assert.equal(model.shiftDate('2026-11-01', -7), '2026-10-25');
});
test('分钟精度允许任意一分钟，而非十五分钟间隔', () => {
    for (const value of ['00:00', '08:34', '11:19', '23:59']) assert.equal(model.validTime(value), true);
    for (const value of ['24:00', '08:60', '8:34', '08:34:00', '']) assert.equal(model.validTime(value), false);
});
test('日期及时间最小最大边界包含端点', () => {
    assert.equal(model.inBounds('2026-10-06', '2026-10-06', '2026-10-07'), true);
    assert.equal(model.inBounds('2026-10-05', '2026-10-06', ''), false);
    assert.equal(model.inBounds('2026-10-06T08:34', '2026-10-06T08:35', ''), false);
    assert.equal(model.inBounds('23:59', '', '23:58'), false);
});
test('弹层底部放不下则向上，横向不超出视口', () => {
    const value = model.position({ left: 800, top: 600, bottom: 640 }, 304, 300, { width: 1000, height: 700 });
    assert.equal(value.side, 'top'); assert.equal(value.left, 684); assert.equal(value.top, 292);
});
test('小视口限制高度并预留安全边距', () => {
    const value = model.position({ left: 0, top: 20, bottom: 60 }, 296, 500, { width: 320, height: 300 });
    assert.equal(value.side, 'bottom'); assert.equal(value.left, 12); assert.equal(value.maxHeight, 276); assert.equal(value.top, 12);
});
test('上下两侧都放不下时，保留完整弹层和确认按钮', () => {
    const value = model.position({ left: 500, top: 290, bottom: 326 }, 304, 450, { width: 1280, height: 720 });
    assert.equal(value.top, 258); assert.equal(value.maxHeight, 696);
});
