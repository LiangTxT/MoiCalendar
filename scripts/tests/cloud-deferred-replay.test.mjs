import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';
import assert from 'node:assert/strict';

const source = readFileSync(new URL('../../src/MoiCalendar.Storage/wwwroot/indexedDbEventRepository.js', import.meta.url), 'utf8');
const functionSource = source.slice(source.indexOf('export async function applyCloudChangesAndAdvanceCursor'), source.indexOf('export async function getCloudSyncState')).replace('export ', '');

test('pending 变更保留最早修订，清空 outbox 后重放并推进游标', async () => {
    let state = { scope: 'cloud', lastSuccessfulServerRevision: 0 };
    let pending = [{ entityId: 'master' }];
    let event = { id: 'master', title: 'local' };
    const stores = {
        events: { put: value => { event = value; } },
        entities: { put() {} },
        sync: { get: () => state, put: value => { state = value; } },
        outbox: { getAll: () => pending }
    };
    const context = vm.createContext({
        configuredEventStoreName: 'events', configuredCloudEntityStateStoreName: 'entities',
        configuredCloudSyncStateStoreName: 'sync', configuredSyncOutboxStoreName: 'outbox',
        validateSyncScope() {}, validateCloudRemoteChange() {}, validateDateValue() {},
        getDatabase: async () => ({ transaction: () => ({ objectStore: name => stores[name], abort() {} }) }),
        transactionAsPromise: async () => {}, requestAsPromise: async value => value,
        createCloudEntityStateKey: (_, id) => id
    });
    vm.runInContext(functionSource, context);
    const remote = { calendarEvent: { id: 'master', title: 'remote' }, serverRevision: 12 };
    await context.applyCloudChangesAndAdvanceCursor('cloud', [remote], 15, '2026-10-06T00:00:00Z');
    assert.equal(state.lastSuccessfulServerRevision, 11);
    assert.equal(state.deferredEntityRevisions.master, 12);
    assert.equal(event.title, 'local');
    await context.applyCloudChangesAndAdvanceCursor('cloud', [{ ...remote, serverRevision: 16 }], 20, '2026-10-06T00:00:00Z');
    assert.equal(state.lastSuccessfulServerRevision, 11);
    pending = [];
    await context.applyCloudChangesAndAdvanceCursor('cloud', [remote], 20, '2026-10-06T00:00:00Z');
    assert.equal(state.lastSuccessfulServerRevision, 20);
    assert.equal(Object.keys(state.deferredEntityRevisions).length, 0);
    assert.equal(event.title, 'remote');
});
