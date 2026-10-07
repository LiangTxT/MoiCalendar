import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';
import assert from 'node:assert/strict';

const repository = readFileSync(new URL('../../src/MoiCalendar.Storage/wwwroot/indexedDbEventRepository.js', import.meta.url), 'utf8');
const slice = (start, end) => repository.slice(repository.indexOf(start), repository.indexOf(end)).replaceAll('export async function', 'async function');

test('DateTimeOffset 七位小数可解析；CAS 不丢失亚毫秒变化，并识别等价偏移', () => {
    const context = vm.createContext({ validateDateValue() {} });
    vm.runInContext(slice('function dateTimeTicks', 'function validateCloudSyncState'), context);
    assert.ok(Number.isFinite(Date.parse('2026-10-06T09:00:00.1234567Z')));
    assert.equal(context.dateTimeTicks('2026-10-06T09:00:00.1234567Z'), context.dateTimeTicks('2026-10-06T17:00:00.1234567+08:00'));
    assert.notEqual(context.dateTimeTicks('2026-10-06T09:00:00.1234567Z'), context.dateTimeTicks('2026-10-06T09:00:00.1234568Z'));
});

test('保留本地合并双方排除记录；上传前不跳游标，确认后重放，不越过其他延迟项', async () => {
    const local = { id: 'master', title: 'local', startUtc: '2026-10-01T09:00:00Z', timeZoneId: 'UTC',
        isAllDay: false, recurrenceRule: 'FREQ=DAILY;COUNT=5', excludedOccurrenceStartsUtc: ['2026-10-02T09:00:00Z'] };
    const remote = { ...local, title: 'remote', excludedOccurrenceStartsUtc: ['2026-10-03T09:00:00Z'] };
    const entries = new Map([['conflict', { mutationId: 'conflict', entityId: 'master', entityType: 0,
        conflictCode: 'stale_revision', conflictServerRevision: 12, conflictRemoteEvent: remote }],
        ['other', { mutationId: 'other', entityId: 'other', entityType: 0 }]]);
    const events = new Map([['master', local]]);
    let state = null;
    const stores = {
        events: { get: id => events.get(id), put: value => events.set(value.id, value) },
        outbox: { get: id => entries.get(id), getAll: () => [...entries.values()],
            index: () => ({ getAll: id => [...entries.values()].filter(e => e.entityId === id) }),
            delete: id => entries.delete(id), add: value => entries.set(value.mutationId, value) },
        entities: { put() {} },
        sync: { get: () => state, put: value => { state = value; } }
    };
    const context = vm.createContext({ configuredEventStoreName: 'events', configuredSyncOutboxStoreName: 'outbox',
        configuredCloudEntityStateStoreName: 'entities', configuredCloudSyncStateStoreName: 'sync',
        validateId() {}, validateDateValue() {}, validateEvent() {}, validateSyncOutboxEntry() {},
        validateSyncScope() {}, validateCloudRemoteChange() {},
        getDatabase: async () => ({ transaction: () => ({ objectStore: name => stores[name] }) }),
        requestAsPromise: async value => value, transactionAsPromise: async () => {}, abortTransactionAfterFailure: async () => {},
        createCloudEntityStateKey: (_, id) => id });
    vm.runInContext(slice('export async function resolveSyncConflictKeepLocal', 'export async function resolveSyncConflictKeepCloud') +
        slice('export async function applyCloudChangesAndAdvanceCursor', 'export async function getCloudSyncState'), context);
    const other = { id: 'other' };
    await context.applyCloudChangesAndAdvanceCursor('cloud', [{ calendarEvent: other, serverRevision: 10 },
        { calendarEvent: remote, serverRevision: 12 }], 15, '2026-10-06T00:00:00Z');
    const replacement = await context.resolveSyncConflictKeepLocal('conflict', 'replacement', '2026-10-06T00:00:00Z');
    const merged = JSON.parse(replacement.payload);
    assert.equal(merged.title, 'local');
    assert.equal(merged.excludedOccurrenceStartsUtc.length, 2);
    assert.equal(events.get('master').excludedOccurrenceStartsUtc.length, 2);
    assert.equal(state.lastSuccessfulServerRevision, 9);
    entries.delete('replacement'); // Successful push acknowledgement.
    await context.applyCloudChangesAndAdvanceCursor('cloud', [{ calendarEvent: remote, serverRevision: 12 },
        { calendarEvent: merged, serverRevision: 16 }], 16, '2026-10-06T00:01:00Z');
    assert.equal(events.get('master').excludedOccurrenceStartsUtc.length, 2);
    assert.equal(state.lastSuccessfulServerRevision, 9);
    assert.equal(state.deferredEntityRevisions.master, undefined);
    entries.delete('other');
    await context.applyCloudChangesAndAdvanceCursor('cloud', [{ calendarEvent: other, serverRevision: 10 }], 16, '2026-10-06T00:02:00Z');
    assert.equal(state.lastSuccessfulServerRevision, 16);
    assert.equal(Object.keys(state.deferredEntityRevisions).length, 0);
});

test('弹层内 Escape 只交给弹层；网格 Space 阻止页面滚动而不产生第二次新建', () => {
    const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarUi.js', import.meta.url), 'utf8');
    const window = {}, document = { addEventListener() {}, removeEventListener() {} };
    vm.runInNewContext(source, { window, document });
    let calls = 0, prevented = 0;
    window.moicalendarUi.initializeCalendarShortcuts({ invokeMethodAsync() { calls++; } });
    window.moicalendarUi.shortcutHandler({ key: 'Escape', target: { tagName: 'BUTTON', closest: s => s === '.calendar-overlay' ? {} : null },
        preventDefault() { prevented++; } });
    assert.equal(calls, 0);
    window.moicalendarUi.shortcutHandler({ key: ' ', target: { tagName: 'DIV', closest: s => s === "[role='gridcell']" ? {} : null },
        preventDefault() { prevented++; } });
    assert.equal(prevented, 1);
    assert.equal(calls, 0);
    window.moicalendarUi.shortcutHandler({ key: 'Escape', target: { tagName: 'MAIN', closest: () => null },
        preventDefault() {} });
    assert.equal(calls, 1);
});
