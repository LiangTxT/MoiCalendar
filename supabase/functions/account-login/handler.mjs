// 可在 Deno 和 Node 测试中使用；从不记录密码、令牌、邮箱或提供程序原始错误。
export function createLoginHandler({ env, fetch: send = globalThis.fetch, crypto: crypt = globalThis.crypto }) {
  const required = name => {
    const value = env(name)?.trim();
    if (!value) throw new Error('missing server configuration');
    return value;
  };
  const url = path => new URL(path, required('SUPABASE_URL').replace(/\/?$/, '/'));
  const serverCall = (path, body) => send(url(path), {
    method: 'POST', headers: {
      apikey: required('SUPABASE_SERVICE_ROLE_KEY'),
      Authorization: `Bearer ${required('SUPABASE_SERVICE_ROLE_KEY')}`,
      'Content-Type': 'application/json',
    }, body: JSON.stringify(body), signal: AbortSignal.timeout(10000), redirect: 'error',
  });
  return async request => {
    const headers = new Headers({
      'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff', Vary: 'Origin',
      'Access-Control-Allow-Headers': 'apikey, content-type',
      'Access-Control-Allow-Methods': 'POST, OPTIONS',
    });
    const reply = (status, code) => new Response(JSON.stringify({ code }), { status, headers });
    try {
      const allowed = required('MOICALENDAR_ALLOWED_ORIGINS').split(',').map(value => {
        const origin = new URL(value.trim());
        if ((origin.protocol !== 'https:' && !(origin.protocol === 'http:' &&
            ['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname))) ||
            origin.pathname !== '/' || origin.search || origin.hash || origin.username || origin.password)
          throw new Error('unsafe origin configuration');
        return origin.origin;
      });
      const origin = request.headers.get('Origin');
      if (origin && !allowed.includes(origin)) return reply(403, 'origin_not_allowed');
      if (origin) headers.set('Access-Control-Allow-Origin', origin);
      if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers });
      if (request.method !== 'POST') return reply(405, 'method_not_allowed');
      const publicKey = env('MOICALENDAR_LOGIN_PUBLIC_KEY')?.trim() || required('SUPABASE_ANON_KEY');
      if (request.headers.get('apikey') !== publicKey) return reply(401, 'login_unavailable');
      if (!request.headers.get('Content-Type')?.toLowerCase().startsWith('application/json'))
        return reply(415, 'validation_failed');
      if (Number(request.headers.get('Content-Length')) > 8192) return reply(413, 'request_too_large');
      // Content-Length 可伪造或不存在；流式读取也有硬上限。
      const reader = request.body?.getReader();
      if (!reader) return reply(400, 'validation_failed');
      const chunks = [];
      let length = 0;
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        length += value.length;
        if (length > 8192) { await reader.cancel(); return reply(413, 'request_too_large'); }
        chunks.push(value);
      }
      const bytes = new Uint8Array(length);
      let offset = 0;
      for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
      let input;
      try { input = JSON.parse(new TextDecoder().decode(bytes)); }
      catch { return reply(400, 'validation_failed'); }
      const email = typeof input?.email === 'string' ? input.email.trim().toLowerCase() : '';
      if (email.length > 320 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))
        return reply(400, 'email_address_invalid');
      if (typeof input.password !== 'string' || !input.password.length) return reply(400, 'password_required');
      if (input.password.length > 1024) return reply(400, 'password_too_long');
      const secret = required('MOICALENDAR_LOGIN_RATE_SECRET');
      if (secret.length < 32) throw new Error('weak server configuration');
      const encoder = new TextEncoder();
      const key = await crypt.subtle.importKey('raw', encoder.encode(secret),
        { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
      const hash = Array.from(new Uint8Array(await crypt.subtle.sign('HMAC', key, encoder.encode(email))))
        .map(value => value.toString(16).padStart(2, '0')).join('');
      const limit = await serverCall('rest/v1/rpc/moicalendar_claim_login_attempt', { p_email_key: hash });
      if (!limit.ok) return reply(503, 'login_unavailable');
      const admitted = await limit.json();
      if (admitted === false) {
        headers.set('Retry-After', '900');
        return reply(429, 'login_rate_limited');
      }
      if (admitted !== true) return reply(503, 'login_unavailable');
      const auth = await send(url('auth/v1/token?grant_type=password'), {
        method: 'POST', headers: { apikey: required('SUPABASE_ANON_KEY'), 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password: input.password }),
        signal: AbortSignal.timeout(10000), redirect: 'error',
      });
      const result = await auth.json();
      if (auth.ok) {
        if (!result.access_token || !result.refresh_token || !result.user?.id)
          return reply(503, 'login_unavailable');
        return new Response(JSON.stringify({
          access_token: result.access_token, refresh_token: result.refresh_token,
          expires_in: result.expires_in, token_type: result.token_type,
          user: { id: result.user.id, email: result.user.email,
            email_confirmed_at: result.user.email_confirmed_at, user_metadata: result.user.user_metadata },
        }), { status: 200, headers });
      }
      const code = result.error_code || result.code;
      if (auth.status === 429) return reply(429, 'login_rate_limited');
      if (code === 'invalid_credentials') {
        const lookup = await serverCall('rest/v1/rpc/moicalendar_classify_login_email', { p_email: email });
        if (!lookup.ok) return reply(503, 'login_unavailable');
        const account = await lookup.json();
        if (account.exists === false) return reply(400, 'email_not_registered');
        if (account.exists !== true || typeof account.password_set !== 'boolean')
          return reply(503, 'login_unavailable');
        return reply(400, account.password_set ? 'wrong_password' : 'password_not_set');
      }
      const safeCodes = ['email_not_confirmed', 'user_banned', 'email_provider_disabled',
        'captcha_failed', 'over_request_rate_limit', 'over_email_send_rate_limit'];
      return safeCodes.includes(code) ? reply(400, code) : reply(503, 'login_unavailable');
    } catch {
      return reply(503, 'login_unavailable');
    }
  };
}
