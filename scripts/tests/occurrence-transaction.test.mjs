import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';
import assert from 'node:assert/strict';

// 运行实际事务入口，替换数据库传输；验证检查分支与原子提交边界。
const source = readFileSync(new URL('../../src/MoiCalendar.Storage/wwwroot/indexedDbEventRepository.js', import.meta.url), 'utf8');
const functionSource = source.slice(source.indexOf('export async function applyCalendarImport'), source.indexOf('export async function updateEventWithSyncOperation')).replace('export ', '');
function setup({ uid = null, stale = false, failCreate = false } = {}) {
    const original = { id: 'master', externalUid: uid, updatedAtUtc: '2026-10-05T00:00:00Z' };
    const records = [original];
    const operations = [], outbox = [];
    let aborted = false;
    const store = {
        getAll: () => records.slice(),
        put: event => { records[0] = event; },
        add: event => { if (failCreate) throw new Error('disk failure'); records.push(event); }
    };
    const transaction = { objectStore: name => name === 'events' ? store : name === 'ops' ? { add: op => operations.push(op) } : name === 'outbox' ? { add: op => outbox.push(op) } : { get: () => null } };
    const context = vm.createContext({
        configuredEventStoreName: 'events', configuredOperationStoreName: 'ops', configuredSyncOutboxStoreName: 'outbox', configuredCloudEntityStateStoreName: 'state',
        validateEventAndOperation() {}, validateEvent() {},
        getDatabase: async () => ({ transaction: () => transaction }),
        transactionAsPromise: async () => {}, requestAsPromise: async value => value,
        createCloudEntityStateKey: () => '', createSyncOutboxEntry: op => op,
        abortTransactionAfterFailure: async () => { aborted = true; records.splice(0, records.length, original); operations.length = 0; outbox.length = 0; }
    });
    vm.runInContext(functionSource, context);
    const changes = [
        { calendarEvent: { ...original, title: 'series', excludedOccurrenceStartsUtc: ['2026-10-06T09:00:00Z'] }, operation: { id: 'update' }, expectedExistingEventId: 'master', expectedExistingUpdatedAtUtc: stale ? '2026-10-04T00:00:00Z' : original.updatedAtUtc },
        { calendarEvent: { id: 'single', externalUid: null }, operation: { id: 'create' }, expectedExistingEventId: null }
    ];
    return { run: () => context.applyCalendarImport(changes), records, operations, outbox, original, aborted: () => aborted };
}
for (const uid of [null, 'imported-event-uid']) {
    test(`单次编辑事务支持 ${uid ? '导入' : '本地'} 系列及无 UID 独立事件`, async () => {
        const db = setup({ uid });
        await db.run();
        assert.equal(db.records.length, 2);
        assert.equal(db.operations.length, 2);
        assert.equal(db.outbox.length, 2);
        assert.equal(db.aborted(), false);
    });
}
for (const options of [{ stale: true }, { failCreate: true }]) {
    test(`版本冲突或新事件写入失败会回滚整个事务 ${JSON.stringify(options)}`, async () => {
        const db = setup(options);
        await assert.rejects(db.run());
        assert.deepEqual(db.records, [db.original]);
        assert.equal(db.operations.length, 0);
        assert.equal(db.outbox.length, 0);
        assert.equal(db.aborted(), true);
    });
}
