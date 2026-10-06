import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createLoginHandler } from '../../supabase/functions/account-login/handler.mjs';

function fixture(responses = []) {
  const calls = [];
  const configuration = {
    SUPABASE_URL: 'https://backend.example.com/', SUPABASE_ANON_KEY: 'public-key',
    SUPABASE_SERVICE_ROLE_KEY: 'server-only-key', MOICALENDAR_ALLOWED_ORIGINS: 'https://calendar.example.com',
    MOICALENDAR_LOGIN_RATE_SECRET: 'test-only-rate-secret-at-least-32-characters',
  };
  const handler = createLoginHandler({ env: name => configuration[name], fetch: async (url, options) => {
    calls.push({ url: String(url), ...options });
    const next = responses.shift();
    if (next instanceof Error) throw next;
    if (!next) throw new Error('意外的额外请求');
    return Response.json(next.body, { status: next.status ?? 200 });
  } });
  const request = (body = { email: 'User@Example.com', password: 'password123' }, extra = {}) => handler(new Request(
    'https://backend.example.com/functions/v1/account-login', {
      method: 'POST', headers: { Origin: 'https://calendar.example.com', apikey: 'public-key',
        'Content-Type': 'application/json', ...extra.headers }, body: JSON.stringify(body),
    }));
  return { calls, configuration, request, handler };
}

test('未注册、密码错误和无密码账户严格区分，仅失败后查询账户', async () => {
  for (const [account, code] of [
    [{ exists: false, password_set: false }, 'email_not_registered'],
    [{ exists: true, password_set: true }, 'wrong_password'],
    [{ exists: true, password_set: false }, 'password_not_set'],
  ]) {
    const f = fixture([{ body: true }, { status: 400, body: { error_code: 'invalid_credentials' } }, { body: account }]);
    const response = await f.request();
    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), { code });
    assert.equal(f.calls.length, 3);
    assert.match(f.calls[0].url, /claim_login_attempt$/);
    const key = JSON.parse(f.calls[0].body).p_email_key;
    assert.match(key, /^[a-f0-9]{64}$/);
    assert.equal(JSON.parse(f.calls[2].body).p_email, 'user@example.com');
    assert.equal(f.calls[1].headers.apikey, 'public-key');
    assert.equal(f.calls[2].headers.apikey, 'server-only-key');
  }
});

test('限流拒绝、存储失败或异常计数不绕过防护', async () => {
  for (const [first, status] of [[{ body: false }, 429], [{ body: 'true' }, 503],
    [{ status: 500, body: {} }, 503], [new Error('网络异常'), 503]]) {
    const f = fixture([first]);
    const response = await f.request();
    assert.equal(response.status, status);
    assert.equal(f.calls.length, 1);
    if (status === 429) assert.equal(response.headers.get('Retry-After'), '900');
  }
});

test('真实认证成功只返回会话白名单，不查询邮箱', async () => {
  const f = fixture([{ body: true }, { body: {
    access_token: 'access', refresh_token: 'refresh', expires_in: 3600,
    user: { id: 'user-id', email: 'user@example.com', encrypted_password: 'never-return' },
    secret: 'never-return',
  } }]);
  const response = await f.request();
  const result = await response.json();
  assert.equal(response.status, 200);
  assert.equal(result.access_token, 'access');
  assert.equal(JSON.stringify(result).includes('never-return'), false);
  assert.equal(f.calls.length, 2);
  assert.equal(response.headers.get('Cache-Control'), 'no-store');
});

test('邮箱未验证、停用与服务限流不会被误报成密码错误', async () => {
  for (const code of ['email_not_confirmed', 'user_banned', 'email_provider_disabled', 'captcha_failed']) {
    const f = fixture([{ body: true }, { status: 400, body: { error_code: code } }]);
    assert.deepEqual(await (await f.request()).json(), { code });
    assert.equal(f.calls.length, 2);
  }
  const f = fixture([{ body: true }, { status: 429, body: {} }]);
  assert.equal((await f.request()).status, 429);
});

test('输入校验、来源限制和错误 key 均在读库之前拒绝', async () => {
  for (const [body, extra, status] of [
    [{ email: 'not-email', password: 'p' }, {}, 400],
    [{ email: 'user@example.com', password: '' }, {}, 400],
    [{ email: 'user@example.com', password: 'x'.repeat(1025) }, {}, 400],
    [{ email: 'user@example.com', password: 'p' }, { headers: { Origin: 'https://evil.example.com' } }, 403],
    [{ email: 'user@example.com', password: 'p' }, { headers: { apikey: 'wrong' } }, 401],
    [{ extra: 'x'.repeat(9000) }, {}, 413],
  ]) {
    const f = fixture();
    assert.equal((await f.request(body, extra)).status, status);
    assert.equal(f.calls.length, 0);
  }
});

test('不存在 Origin 时不能绕过限流，伪造转发 IP 无作用', async () => {
  const f = fixture([{ body: false }]);
  const response = await f.handler(new Request('https://backend.example.com/functions/v1/account-login', {
    method: 'POST', headers: { apikey: 'public-key', 'Content-Type': 'application/json', 'X-Forwarded-For': '1.2.3.4' },
    body: JSON.stringify({ email: 'user@example.com', password: 'p' }),
  }));
  assert.equal(response.status, 429);
});

test('数据库限流及邮箱查询只能由 service_role 执行，窗口计数原子更新', () => {
  const sql = readFileSync(new URL('../../supabase/migrations/20261006000000_detailed_login.sql', import.meta.url), 'utf8');
  assert.match(sql, /pg_advisory_xact_lock/);
  assert.match(sql, /on conflict\(key\) do update/);
  assert.match(sql, /enable row level security/);
  assert.match(sql, /revoke all on function public\.moicalendar_classify_login_email\(text\) from public, anon, authenticated/);
  assert.match(sql, /grant execute on function public\.moicalendar_classify_login_email\(text\) to service_role/);
  assert.match(sql, /set search_path = pg_catalog, pg_temp/);
});

test('查询异常或认证服务异常时不猜测账户状态，不泄漏提供程序错误', async () => {
  for (const responses of [
    [{ body: true }, { status: 500, body: { message: 'private-internal-error' } }],
    [{ body: true }, { status: 400, body: { error_code: 'invalid_credentials' } }, { status: 500, body: {} }],
    [{ body: true }, { status: 400, body: { error_code: 'invalid_credentials' } }, { body: { exists: 'false' } }],
  ]) {
    const f = fixture(responses);
    const response = await f.request();
    assert.equal(response.status, 503);
    assert.deepEqual(await response.json(), { code: 'login_unavailable' });
  }
});
