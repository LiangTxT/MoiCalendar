import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';
import assert from 'node:assert/strict';

const source = readFileSync(new URL('../../src/MoiCalendar.Storage/wwwroot/indexedDbEventRepository.js', import.meta.url), 'utf8');
const functionSource = source.slice(source.indexOf('export async function applyRemoteSyncOperation'), source.indexOf('export async function applyCalendarImport')).replace('export ', '');
const master = { id: 'series', startUtc: '2026-10-01T09:00:00Z', timeZoneId: 'UTC', isAllDay: false,
    recurrenceRule: 'FREQ=DAILY;COUNT=5', updatedAtUtc: '2026-10-06T01:00:00Z', excludedOccurrenceStartsUtc: ['2026-10-02T09:00:00Z'] };

async function apply(local, remote) {
    let saved = local;
    const context = vm.createContext({
        configuredEventStoreName: 'events', configuredOperationStoreName: 'ops',
        validateSyncOperation() {}, validateEvent() {},
        getDatabase: async () => ({ transaction: () => ({ objectStore: name => name === 'events'
            ? { get: () => saved, put: value => { saved = value; } }
            : { get: () => null, put() {} } }) }),
        requestAsPromise: async value => value, transactionAsPromise: async () => {},
        abortTransactionAfterFailure: async () => {}
    });
    vm.runInContext(functionSource, context);
    await context.applyRemoteSyncOperation(remote, { operationId: 'op', entityId: 'series', status: 3 }, false);
    return saved;
}

test('外部同步合并相同系列的不同删除，并保留较新普通字段', async () => {
    const saved = await apply(master, { ...master, title: 'new', updatedAtUtc: '2026-10-06T02:00:00Z', excludedOccurrenceStartsUtc: ['2026-10-03T09:00:00Z'] });
    assert.equal(saved.title, 'new');
    assert.equal(saved.excludedOccurrenceStartsUtc.length, 2);
});
test('迟到的旧外部操作不会覆盖较新普通字段，但其删除例外仍可合并', async () => {
    const saved = await apply({ ...master, title: 'local' }, { ...master, title: 'old', updatedAtUtc: '2026-10-05T02:00:00Z', excludedOccurrenceStartsUtc: ['2026-10-03T09:00:00Z'] });
    assert.equal(saved.title, 'local');
    assert.equal(saved.excludedOccurrenceStartsUtc.length, 2);
});
test('不跨锚点合并，也不因例外合并复活墓碑', async () => {
    const shifted = await apply(master, { ...master, startUtc: '2026-10-01T10:00:00Z', updatedAtUtc: '2026-10-06T02:00:00Z', excludedOccurrenceStartsUtc: [] });
    assert.equal(shifted.excludedOccurrenceStartsUtc.length, 0);
    const deleted = await apply({ ...master, deletedAtUtc: '2026-10-06T01:00:00Z' }, { ...master, updatedAtUtc: '2026-10-05T02:00:00Z', excludedOccurrenceStartsUtc: ['2026-10-03T09:00:00Z'] });
    assert.ok(deleted.deletedAtUtc);
    assert.equal(deleted.excludedOccurrenceStartsUtc.length, 1);
});
