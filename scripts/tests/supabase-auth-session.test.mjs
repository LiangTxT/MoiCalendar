import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.Sync/wwwroot/supabaseAuthSession.js', import.meta.url), 'utf8')
    .replace(/^export /gm, '');
const legacyKey = 'moicalendar.supabase.auth.session.v1';
const key = 'moicalendar.supabase.auth.session.v2';
const session = JSON.stringify({ accessToken: 'test-only-access', refreshToken: 'test-only-refresh', expiresAtUtc: '2026-10-11T00:00:00Z' });
const storage = (values = new Map()) => ({
    getItem: k => values.get(k) ?? null,
    setItem: (k, value) => values.set(k, value),
    removeItem: k => values.delete(k),
    values
});
function setup(local = storage(), tab = storage()) {
    const context = vm.createContext({ localStorage: local, sessionStorage: tab });
    vm.runInContext(source, context);
    return { read: () => context.readSession(), write: value => context.writeSession(value), clear: () => context.clearSession(), local, tab };
}

test('关闭标签页后重新打开可恢复会话，不依赖 sessionStorage', () => {
    const f = setup(); f.write(session);
    assert.equal(f.tab.getItem(legacyKey), null);
    const reopened = setup(f.local);
    assert.equal(reopened.read(), session);
});

test('旧版会话迁入持久存储并移除旧副本', () => {
    const f = setup(storage(), storage(new Map([[legacyKey, session]])));
    assert.equal(f.read(), session);
    assert.equal(f.local.getItem(key), session);
    assert.equal(f.tab.getItem(legacyKey), null);
});

test('以持久存储的最新会话为准，不用旧标签页令牌覆盖', () => {
    const f = setup(storage(new Map([[key, session]])), storage(new Map([[legacyKey, 'old-session']])));
    assert.equal(f.read(), session);
    assert.equal(f.local.getItem(key), session);
});

test('主动退出后不恢复，也不从另一旧标签页复活会话', () => {
    const local = storage();
    const first = setup(local); first.write(session);
    const legacyTab = setup(local, storage(new Map([[legacyKey, session]])));
    first.clear();
    assert.equal(setup(local).read(), null);
    assert.equal(legacyTab.read(), null);
    assert.equal(local.getItem(key), '');
    first.write(session);
    assert.equal(setup(local).read(), session);
});

test('退出只清认证信息，不清日历、偏好或其他站点存储', () => {
    const f = setup(storage(new Map([['calendar-preference', 'keep']])));
    f.tab.setItem(legacyKey, session); f.write(session); f.clear();
    assert.equal(f.tab.getItem(legacyKey), null);
    assert.equal(f.local.getItem('calendar-preference'), 'keep');
});

test('没有会话时不产生登录，写入格式无效或存储受限时不静默退回短期存储', () => {
    const f = setup(); assert.equal(f.read(), null);
    for (const invalid of ['', null, {}]) assert.throws(() => f.write(invalid));
    const blocked = setup({getItem(){throw new Error('blocked');},setItem(){throw new Error('blocked');}});
    assert.throws(() => blocked.write(session));
    assert.throws(() => blocked.read());
    assert.equal(blocked.tab.getItem(legacyKey), null);
});
