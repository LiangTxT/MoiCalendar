const jsonHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Content-Type": "application/json; charset=utf-8",
};

const recentAuthenticationWindowMilliseconds = 10 * 60 * 1000;
const futureClockToleranceMilliseconds = 60 * 1000;

function jsonResponse(status: number, body: Record<string, unknown>): Response {
  return new Response(JSON.stringify(body), { status, headers: jsonHeaders });
}

function requiredEnvironment(name: string): string {
  const value = Deno.env.get(name);
  if (!value) {
    throw new Error(`Missing required server environment: ${name}`);
  }

  return value;
}

function serviceUrl(baseUrl: string, relativePath: string): URL {
  const normalizedBaseUrl = baseUrl.endsWith("/") ? baseUrl : `${baseUrl}/`;
  return new URL(relativePath, normalizedBaseUrl);
}

Deno.serve(async (request: Request): Promise<Response> => {
  if (request.method === "OPTIONS") {
    return new Response(null, { status: 204, headers: jsonHeaders });
  }
  if (request.method !== "POST") {
    return jsonResponse(405, { code: "method_not_allowed" });
  }

  try {
    const authorization = request.headers.get("Authorization");
    if (!authorization?.startsWith("Bearer ")) {
      return jsonResponse(401, { code: "authentication_required" });
    }

    const supabaseUrl = requiredEnvironment("SUPABASE_URL");
    const anonymousKey = requiredEnvironment("SUPABASE_ANON_KEY");
    const serviceRoleKey = requiredEnvironment("SUPABASE_SERVICE_ROLE_KEY");

    // Auth verifies the presented token. The account id is obtained only from
    // this verified response; the client cannot submit a target user id.
    const userResponse = await fetch(serviceUrl(supabaseUrl, "auth/v1/user"), {
      headers: {
        "apikey": anonymousKey,
        "Authorization": authorization,
      },
    });
    if (!userResponse.ok) {
      return jsonResponse(401, { code: "authentication_required" });
    }

    const verifiedUser = await userResponse.json() as {
      id?: string;
      last_sign_in_at?: string | null;
    };
    if (!verifiedUser.id ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(verifiedUser.id)) {
      return jsonResponse(401, { code: "authentication_required" });
    }

    const lastSignInAt = Date.parse(verifiedUser.last_sign_in_at ?? "");
    const signInAge = Date.now() - lastSignInAt;
    if (!Number.isFinite(lastSignInAt) ||
        signInAge < -futureClockToleranceMilliseconds ||
        signInAge > recentAuthenticationWindowMilliseconds) {
      return jsonResponse(409, { code: "recent_authentication_required" });
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
      });

    // A racing retry may observe that the account has already gone away.
    if (!deleteResponse.ok && deleteResponse.status !== 404) {
      return jsonResponse(503, { code: "account_deletion_failed" });
    }

    return jsonResponse(200, { deleted: true });
  } catch {
    // Never return or log token material, provider responses or server secrets.
    return jsonResponse(503, { code: "account_deletion_unavailable" });
  }
});
