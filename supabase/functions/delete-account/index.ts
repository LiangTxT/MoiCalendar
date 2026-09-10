const recentAuthenticationWindowMilliseconds = 10 * 60 * 1000;
const futureClockToleranceMilliseconds = 60 * 1000;
const maximumAuthorizationHeaderLength = 8192;
const maximumRequestBodyLength = 1024;

function responseHeaders(request: Request, includeCors: boolean): Headers {
  const headers = new Headers({
    "Access-Control-Allow-Headers": "authorization, apikey, content-type",
    "Access-Control-Allow-Methods": "POST, OPTIONS",
    "Cache-Control": "no-store",
    "Content-Type": "application/json; charset=utf-8",
    "Vary": "Origin",
    "X-Content-Type-Options": "nosniff",
  });
  const origin = request.headers.get("Origin");
  if (includeCors && origin) {
    headers.set("Access-Control-Allow-Origin", origin);
  }
  return headers;
}

function jsonResponse(
  request: Request,
  status: number,
  body: Record<string, unknown>,
  includeCors: boolean,
  additionalHeaders?: Record<string, string>,
): Response {
  const headers = responseHeaders(request, includeCors);
  for (const [name, value] of Object.entries(additionalHeaders ?? {})) {
    headers.set(name, value);
  }
  return new Response(JSON.stringify(body), { status, headers });
}

function requiredEnvironment(name: string): string {
  const value = Deno.env.get(name)?.trim();
  if (!value) {
    throw new Error(`Missing required server environment: ${name}`);
  }
  return value;
}

function serviceUrl(baseUrl: string, relativePath: string): URL {
  const normalizedBaseUrl = baseUrl.endsWith("/") ? baseUrl : `${baseUrl}/`;
  return new URL(relativePath, normalizedBaseUrl);
}

function isLoopback(hostname: string): boolean {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
}

function allowedOrigins(): Set<string> {
  const configured = requiredEnvironment("MOICALENDAR_ALLOWED_ORIGINS")
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean);
  const result = new Set<string>();
  for (const value of configured) {
    const url = new URL(value);
    if ((url.protocol !== "https:" && !(url.protocol === "http:" && isLoopback(url.hostname))) ||
        url.pathname !== "/" || url.search || url.hash || url.username || url.password) {
      throw new Error("MOICALENDAR_ALLOWED_ORIGINS contains an unsafe origin");
    }
    result.add(url.origin);
  }
  if (result.size === 0) {
    throw new Error("MOICALENDAR_ALLOWED_ORIGINS is empty");
  }
  return result;
}

function isRequestOriginAllowed(request: Request): boolean {
  const origin = request.headers.get("Origin");
  return origin === null || allowedOrigins().has(origin);
}

function bearerToken(request: Request): string | null {
  const authorization = request.headers.get("Authorization");
  if (!authorization || authorization.length > maximumAuthorizationHeaderLength) {
    return null;
  }
  const match = /^Bearer ([A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)$/.exec(authorization);
  return match?.[1] ?? null;
}

function parseVerifiedJwtClaims(token: string): {
  sub?: string;
  role?: string;
  iat?: number;
  exp?: number;
} | null {
  try {
    const encoded = token.split(".")[1];
    const base64 = encoded.replace(/-/g, "+").replace(/_/g, "/")
      .padEnd(Math.ceil(encoded.length / 4) * 4, "=");
    return JSON.parse(atob(base64));
  } catch {
    return null;
  }
}

async function claimDeletionAttempt(
  supabaseUrl: string,
  serviceRoleKey: string,
  verifiedUserId: string,
): Promise<boolean | null> {
  const response = await fetch(
    serviceUrl(supabaseUrl, "rest/v1/rpc/moicalendar_claim_account_deletion_attempt"),
    {
      method: "POST",
      headers: {
        "apikey": serviceRoleKey,
        "Authorization": `Bearer ${serviceRoleKey}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ p_owner_id: verifiedUserId }),
    },
  );
  if (!response.ok) {
    return null;
  }
  try {
    const result = await response.json();
    return typeof result === "boolean" ? result : null;
  } catch {
    return null;
  }
}

Deno.serve(async (request: Request): Promise<Response> => {
  let originAllowed = false;
  try {
    originAllowed = isRequestOriginAllowed(request);
  } catch {
    return jsonResponse(request, 503, { code: "account_deletion_unavailable" }, false);
  }
  if (!originAllowed) {
    return jsonResponse(request, 403, { code: "origin_not_allowed" }, false);
  }
  if (request.method === "OPTIONS") {
    return new Response(null, { status: 204, headers: responseHeaders(request, true) });
  }
  if (request.method !== "POST") {
    return jsonResponse(request, 405, { code: "method_not_allowed" }, true);
  }

  const contentLength = Number.parseInt(request.headers.get("Content-Length") ?? "0", 10);
  if (Number.isFinite(contentLength) && contentLength > maximumRequestBodyLength) {
    return jsonResponse(request, 413, { code: "request_too_large" }, true);
  }

  try {
    const token = bearerToken(request);
    if (!token) {
      return jsonResponse(request, 401, { code: "authentication_required" }, true);
    }

    const supabaseUrl = requiredEnvironment("SUPABASE_URL");
    const anonymousKey = requiredEnvironment("SUPABASE_ANON_KEY");

    // Auth verifies the exact presented token. The deletion target is derived
    // only from this response and never from request JSON or a query parameter.
    const authorization = `Bearer ${token}`;
    const userResponse = await fetch(serviceUrl(supabaseUrl, "auth/v1/user"), {
      headers: {
        "apikey": anonymousKey,
        "Authorization": authorization,
      },
    });
    if (!userResponse.ok) {
      return jsonResponse(request, 401, { code: "authentication_required" }, true);
    }

    const verifiedUser = await userResponse.json() as {
      id?: string;
      last_sign_in_at?: string | null;
    };
    if (!verifiedUser.id ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(verifiedUser.id)) {
      return jsonResponse(request, 401, { code: "authentication_required" }, true);
    }

    // Parsing is safe only after Auth has verified this exact bearer token.
    const claims = parseVerifiedJwtClaims(token);
    const now = Date.now();
    const issuedAt = (claims?.iat ?? 0) * 1000;
    const expiresAt = (claims?.exp ?? 0) * 1000;
    const lastSignInAt = Date.parse(verifiedUser.last_sign_in_at ?? "");
    const signInAge = now - lastSignInAt;
    const tokenAge = now - issuedAt;
    if (claims?.sub !== verifiedUser.id || claims?.role !== "authenticated" ||
        !Number.isFinite(lastSignInAt) ||
        signInAge < -futureClockToleranceMilliseconds ||
        signInAge > recentAuthenticationWindowMilliseconds ||
        tokenAge < -futureClockToleranceMilliseconds ||
        tokenAge > recentAuthenticationWindowMilliseconds ||
        expiresAt <= now) {
      return jsonResponse(request, 409, { code: "recent_authentication_required" }, true);
    }

    const serviceRoleKey = requiredEnvironment("SUPABASE_SERVICE_ROLE_KEY");
    const attemptAllowed = await claimDeletionAttempt(
      supabaseUrl,
      serviceRoleKey,
      verifiedUser.id,
    );
    if (attemptAllowed === null) {
      return jsonResponse(request, 503, { code: "account_deletion_unavailable" }, true);
    }
    if (!attemptAllowed) {
      return jsonResponse(
        request,
        429,
        { code: "account_deletion_rate_limited" },
        true,
        { "Retry-After": "900" },
      );
    }

    const deleteResponse = await fetch(
      serviceUrl(supabaseUrl, `auth/v1/admin/users/${encodeURIComponent(verifiedUser.id)}`),
      {
        method: "DELETE",
        headers: {
          "apikey": serviceRoleKey,
          "Authorization": `Bearer ${serviceRoleKey}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ should_soft_delete: false }),
      },
    );

    // A racing retry may observe that the account has already gone away.
    if (!deleteResponse.ok && deleteResponse.status !== 404) {
      return jsonResponse(request, 503, { code: "account_deletion_failed" }, true);
    }

    return jsonResponse(request, 200, { deleted: true }, true);
  } catch {
    // Never return or log token material, provider responses or server secrets.
    return jsonResponse(request, 503, { code: "account_deletion_unavailable" }, true);
  }
});
