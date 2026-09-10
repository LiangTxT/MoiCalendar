import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { brotliDecompressSync, gunzipSync } from "node:zlib";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const publishRoot = process.argv[2]
    ? resolve(repositoryRoot, process.argv[2])
    : join(
        repositoryRoot,
        "src",
        "MoiCalendar.App",
        "bin",
        "Release",
        "net10.0",
        "publish",
        "wwwroot");

for (const relativePath of [
    "index.html",
    "appsettings.json",
    "appsettings.Production.json",
    "staticwebapp.config.json",
    "service-worker.js",
    "service-worker-assets.js"
]) {
    requireFile(relativePath);
}

for (const forbiddenEnvironment of ["Local", "Managed"]) {
    const relativePath = `appsettings.${forbiddenEnvironment}.json`;
    if (existsSync(join(publishRoot, relativePath))) {
        fail(`生产发布产物不得包含 ${relativePath}。`);
    }
}

const production = readJson("appsettings.Production.json");
const application = requireObject(production.MoiCalendar, "MoiCalendar");
assertOnlyKeys(application, ["PublicBaseUrl", "CloudBackend"], "MoiCalendar");
validateRemoteHttpsUrl(application.PublicBaseUrl, "MoiCalendar:PublicBaseUrl");

const cloud = requireObject(application.CloudBackend, "MoiCalendar:CloudBackend");
assertOnlyKeys(cloud, ["Enabled", "BaseUrl", "PublicKey", "Supabase"], "MoiCalendar:CloudBackend");
if (cloud.Enabled !== true) {
    fail("Azure 生产部署要求 CloudBackend.Enabled=true。使用本地模式时不要运行生产部署工作流。");
}
validateRemoteHttpsUrl(cloud.BaseUrl, "MoiCalendar:CloudBackend:BaseUrl");
validatePublicKey(cloud.PublicKey);

const supabase = requireObject(cloud.Supabase, "MoiCalendar:CloudBackend:Supabase");
assertOnlyKeys(supabase, ["RealtimePath"], "MoiCalendar:CloudBackend:Supabase");
if (typeof supabase.RealtimePath !== "string" ||
    !supabase.RealtimePath.startsWith("/") ||
    supabase.RealtimePath.includes("?") ||
    supabase.RealtimePath.includes("#") ||
    supabase.RealtimePath.includes("://")) {
    fail("生产 RealtimePath 必须是安全的根相对路径。");
}

const staticWebApp = readJson("staticwebapp.config.json");
if (staticWebApp.navigationFallback?.rewrite !== "/index.html") {
    fail("staticwebapp.config.json 必须把客户端路由回退到 /index.html。");
}
const globalHeaders = requireObject(staticWebApp.globalHeaders, "staticwebapp.config.json:globalHeaders");
const contentSecurityPolicy = globalHeaders["Content-Security-Policy"];
if (typeof contentSecurityPolicy !== "string") {
    fail("生产静态产物缺少 Content-Security-Policy。");
}
for (const requiredDirective of [
    "default-src 'self'",
    "base-uri 'self'",
    "object-src 'none'",
    "frame-ancestors 'none'",
    "script-src 'self' 'wasm-unsafe-eval'",
    "connect-src 'self' https: wss:"
]) {
    if (!contentSecurityPolicy.includes(requiredDirective)) {
        fail(`生产 Content-Security-Policy 缺少 ${requiredDirective}。`);
    }
}
const scriptDirective = contentSecurityPolicy
    .split(";")
    .map(value => value.trim())
    .find(value => value.startsWith("script-src "));
if (!scriptDirective?.includes("'sha256-") || scriptDirective.includes("'unsafe-inline'")) {
    fail("生产 script-src 必须使用构建期哈希授权内联脚本，且不能使用 unsafe-inline。");
}
for (const [header, expected] of Object.entries({
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "strict-origin-when-cross-origin",
    "X-Frame-Options": "DENY"
})) {
    if (globalHeaders[header] !== expected) {
        fail(`生产静态产物缺少安全头 ${header}: ${expected}。`);
    }
}
const staticWebAppBytes = readFileSync(join(publishRoot, "staticwebapp.config.json"));
assertCompressedCopyMatches("staticwebapp.config.json.br", brotliDecompressSync, staticWebAppBytes);
assertCompressedCopyMatches("staticwebapp.config.json.gz", gunzipSync, staticWebAppBytes);

const indexHtml = readText("index.html");
if (!/<base\s+href=["']\/["']\s*\/?>/i.test(indexHtml)) {
    fail("Azure 根路径部署要求 index.html 使用 <base href=\"/\">。");
}

const assetManifest = readText("service-worker-assets.js");
if (!assetManifest.includes("appsettings.Production.json")) {
    fail("Service Worker 资源清单缺少 appsettings.Production.json。");
}
const publishedServiceWorker = readText("service-worker.js");
if (!publishedServiceWorker.includes("/^staticwebapp\\.config\\.json$/")) {
    fail("Service Worker 必须排除发布后加固的 staticwebapp.config.json，避免资源哈希不一致。");
}

console.log("生产静态产物验证通过：路径、Production 配置、SPA fallback 与 PWA 资源完整。未输出任何 key。");

function requireFile(relativePath) {
    if (!existsSync(join(publishRoot, relativePath))) {
        fail(`生产发布产物缺少 ${relativePath}。`);
    }
}

function readText(relativePath) {
    return readFileSync(join(publishRoot, relativePath), "utf8");
}

function readJson(relativePath) {
    try {
        return JSON.parse(readText(relativePath));
    } catch {
        fail(`${relativePath} 不是有效 JSON。`);
    }
}

function requireObject(value, path) {
    if (!value || typeof value !== "object" || Array.isArray(value)) {
        fail(`${path} 必须是配置对象。`);
    }
    return value;
}

function assertOnlyKeys(value, allowedKeys, path) {
    const unexpected = Object.keys(value).filter(key => !allowedKeys.includes(key));
    if (unexpected.length > 0) {
        fail(`${path} 包含不允许进入浏览器的配置字段。`);
    }
}

function validateRemoteHttpsUrl(value, path) {
    let url;
    try {
        url = new URL(value);
    } catch {
        fail(`${path} 必须是绝对 HTTPS URL。`);
    }
    if (url.protocol !== "https:" || url.search || url.hash || url.username || url.password ||
        url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]") {
        fail(`${path} 必须是非回环、无凭据、查询参数或片段的绝对 HTTPS URL。`);
    }
}

function validatePublicKey(value) {
    if (typeof value !== "string" || value.trim().length === 0) {
        fail("生产 CloudBackend.PublicKey 必须是 Publishable/anon 客户端 key。");
    }
    const normalized = value.trim().toLowerCase();
    if (normalized.startsWith("sb_secret_") ||
        normalized.includes("service_role") ||
        normalized.includes("supabase_admin")) {
        fail("生产发布拒绝 service-role、secret 或管理员 key。");
    }

    const segments = value.split(".");
    if (segments.length !== 3) {
        return;
    }
    try {
        const payload = JSON.parse(Buffer.from(segments[1], "base64url").toString("utf8"));
        const role = String(payload.role ?? "").toLowerCase();
        if (role === "service_role" || role === "supabase_admin") {
            fail("生产发布拒绝 service-role 或管理员 JWT。");
        }
    } catch (error) {
        if (error?.message?.startsWith("生产发布拒绝")) {
            throw error;
        }
        // Publishable keys need not use JWT format. Runtime validation remains authoritative.
    }
}

function assertCompressedCopyMatches(relativePath, decompress, expected) {
    const path = join(publishRoot, relativePath);
    if (existsSync(path) && !decompress(readFileSync(path)).equals(expected)) {
        fail(`${relativePath} 与加固后的 staticwebapp.config.json 不一致。`);
    }
}

function fail(message) {
    console.error(message);
    process.exit(1);
}
