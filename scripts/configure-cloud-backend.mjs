import { spawnSync } from "node:child_process";
import { existsSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, resolve } from "node:path";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const mode = process.argv[2]?.toLowerCase();
const argumentsByName = parseArguments(process.argv.slice(3));

if (mode !== "local" && mode !== "managed" && mode !== "production") {
    fail("用法：node scripts/configure-cloud-backend.mjs <local|managed|production> [--base-url URL --public-key KEY]");
}

const configuration = mode === "local"
    ? { ...readLocalSupabaseStatus(), enabled: true }
    : mode === "managed"
    ? {
        enabled: true,
        baseUrl: requiredArgument("base-url"),
        publicKey: requiredArgument("public-key")
    }
    : readProductionEnvironment();

if (configuration.enabled) {
    validateBaseUrl(configuration.baseUrl);
    validatePublicKey(configuration.publicKey);
    if (mode === "production") {
        const productionCloudUrl = new URL(configuration.baseUrl);
        if (productionCloudUrl.protocol !== "https:") {
            fail("生产 CloudBackend BaseUrl 必须使用 HTTPS。");
        }
        if (isLoopbackHostname(productionCloudUrl.hostname)) {
            fail("生产 CloudBackend BaseUrl 不能指向 localhost 或回环地址。");
        }
    }
}

const environmentName = mode === "local"
    ? "Local"
    : mode === "managed"
    ? "Managed"
    : "Production";
const realtimePath = mode === "production"
    ? optionalEnvironment("MOICALENDAR_SUPABASE_REALTIME_PATH") ?? "/realtime/v1/websocket"
    : argumentsByName.get("realtime-path") ?? "/realtime/v1/websocket";
validateRealtimePath(realtimePath);

const outputPath = join(
    repositoryRoot,
    "src",
    "MoiCalendar.App",
    "wwwroot",
    `appsettings.${environmentName}.json`);
const document = {
    MoiCalendar: {
        ...(mode === "production" ? { PublicBaseUrl: configuration.publicBaseUrl } : {}),
        CloudBackend: {
            Enabled: configuration.enabled,
            BaseUrl: configuration.baseUrl,
            PublicKey: configuration.publicKey,
            Supabase: {
                RealtimePath: realtimePath
            }
        }
    }
};

writeFileSync(outputPath, `${JSON.stringify(document, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
console.log(`已生成 ${outputPath}`);
console.log("文件只包含浏览器可见配置；未写入 secret/service-role/admin 密钥。");

function readProductionEnvironment() {
    const enabled = requiredBooleanEnvironment("MOICALENDAR_CLOUD_ENABLED");
    const publicBaseUrl = normalizeProductionPublicBaseUrl(
        requiredEnvironment("MOICALENDAR_PUBLIC_BASE_URL"));

    if (!enabled) {
        return { enabled, publicBaseUrl, baseUrl: null, publicKey: null };
    }

    return {
        enabled,
        publicBaseUrl,
        baseUrl: requiredEnvironment("MOICALENDAR_CLOUD_BASE_URL"),
        publicKey: requiredEnvironment("MOICALENDAR_CLOUD_PUBLIC_KEY")
    };
}

function readLocalSupabaseStatus() {
    const cliEntry = join(repositoryRoot, "node_modules", "supabase", "dist", "supabase.js");
    if (!existsSync(cliEntry)) {
        fail("未安装项目级 Supabase CLI。请先运行 npm install。");
    }

    const result = spawnSync(
        process.execPath,
        [cliEntry, "status", "--output", "json"],
        { cwd: repositoryRoot, encoding: "utf8" });
    if (result.error) {
        fail(`无法运行 Supabase CLI：${result.error.message}`);
    }
    if (result.status !== 0) {
        fail("无法读取本地 Supabase 状态。请先运行 npm run supabase:start。");
    }

    const output = result.stdout.replace(/\u001b\[[0-9;]*m/g, "");
    const jsonStart = output.indexOf("{");
    const jsonEnd = output.lastIndexOf("}");
    if (jsonStart < 0 || jsonEnd <= jsonStart) {
        fail("Supabase CLI 没有返回可识别的 JSON 状态。");
    }

    let status;
    try {
        status = JSON.parse(output.slice(jsonStart, jsonEnd + 1));
    } catch {
        fail("Supabase CLI 返回了无法解析的 JSON 状态。");
    }

    const baseUrl = status.API_URL;
    const publicKey = status.PUBLISHABLE_KEY ?? status.ANON_KEY;
    if (!baseUrl || !publicKey) {
        fail("本地 Supabase 状态缺少 API_URL 或 PUBLISHABLE_KEY/ANON_KEY。");
    }
    return { baseUrl, publicKey };
}

function parseArguments(values) {
    const parsed = new Map();
    for (let index = 0; index < values.length; index += 2) {
        const name = values[index];
        const value = values[index + 1];
        if (!name?.startsWith("--") || !value) {
            fail(`无法识别的参数：${name ?? "<空>"}`);
        }
        parsed.set(name.slice(2), value);
    }
    return parsed;
}

function requiredArgument(name) {
    const value = argumentsByName.get(name);
    if (!value) {
        fail(`managed 模式缺少 --${name}。`);
    }
    return value;
}

function optionalEnvironment(name) {
    const value = process.env[name]?.trim();
    return value || null;
}

function requiredEnvironment(name) {
    const value = optionalEnvironment(name);
    if (!value) {
        fail(`production 模式缺少环境变量 ${name}。`);
    }
    return value;
}

function requiredBooleanEnvironment(name) {
    const value = requiredEnvironment(name).toLowerCase();
    if (value !== "true" && value !== "false") {
        fail(`${name} 必须是 true 或 false。`);
    }
    return value === "true";
}

function normalizeProductionPublicBaseUrl(value) {
    let url;
    try {
        url = new URL(value);
    } catch {
        fail("MOICALENDAR_PUBLIC_BASE_URL 必须是绝对 HTTPS URL。");
    }
    if (url.protocol !== "https:" || url.search || url.hash ||
        url.username || url.password || isLoopbackHostname(url.hostname)) {
        fail("MOICALENDAR_PUBLIC_BASE_URL 必须是没有凭据、查询参数或片段的绝对 HTTPS URL。");
    }
    return url.href;
}

function validateBaseUrl(value) {
    let url;
    try {
        url = new URL(value);
    } catch {
        fail("BaseUrl 必须是绝对 HTTP 或 HTTPS URL。");
    }
    if ((url.protocol !== "http:" && url.protocol !== "https:") ||
        url.search || url.hash || url.username || url.password) {
        fail("BaseUrl 必须是没有内嵌凭据、查询参数或片段的绝对 HTTP 或 HTTPS URL。");
    }
    if (url.protocol === "http:" && !isLoopbackHostname(url.hostname)) {
        fail("非本机云后端必须使用 HTTPS；HTTP 仅允许本地回环开发地址。");
    }
}

function isLoopbackHostname(hostname) {
    return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
}

function validateRealtimePath(value) {
    if (!value.startsWith("/") || value.includes("?") || value.includes("#") || value.includes("://")) {
        fail("RealtimePath 必须是以 / 开头且不含查询参数或片段的路径。");
    }
}

function validatePublicKey(value) {
    const normalized = value.toLowerCase();
    if (normalized.startsWith("sb_secret_") ||
        normalized.includes("service_role") ||
        normalized.includes("supabase_admin")) {
        fail("拒绝写入 secret、service-role 或管理员密钥。浏览器只能使用 publishable/anon key。");
    }

    const segments = value.split(".");
    if (segments.length !== 3) {
        return;
    }
    let payload;
    try {
        payload = JSON.parse(Buffer.from(segments[1], "base64url").toString("utf8"));
    } catch {
        // A publishable key is not a JWT. Other formats are validated by the backend at runtime.
        return;
    }

    const role = String(payload.role ?? "").toLowerCase();
    if (role === "service_role" || role === "supabase_admin") {
        fail("拒绝写入 service-role 或管理员 JWT。浏览器只能使用 publishable/anon key。");
    }
}

function fail(message) {
    console.error(message);
    process.exit(1);
}
