import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { brotliCompressSync, constants, gzipSync } from "node:zlib";

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

const indexPath = join(publishRoot, "index.html");
const staticWebAppPath = join(publishRoot, "staticwebapp.config.json");
for (const path of [indexPath, staticWebAppPath]) {
    if (!existsSync(path)) {
        fail(`无法加固生产产物，缺少 ${path}。`);
    }
}

const indexHtml = readFileSync(indexPath, "utf8");
const inlineScriptHashes = [...indexHtml.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)]
    .map(match => match[1])
    .filter(content => content.trim().length > 0)
    .map(content => `'sha256-${createHash("sha256").update(content, "utf8").digest("base64")}'`);

if (inlineScriptHashes.length === 0) {
    fail("生产 index.html 中没有找到需要授权的内联脚本，拒绝生成可能不完整的 CSP。");
}

let configuration;
try {
    configuration = JSON.parse(readFileSync(staticWebAppPath, "utf8"));
} catch {
    fail("生产 staticwebapp.config.json 不是有效 JSON。");
}

const contentSecurityPolicy = [
    "default-src 'self'",
    "base-uri 'self'",
    "object-src 'none'",
    "frame-ancestors 'none'",
    "form-action 'self'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    "style-src 'self' 'unsafe-inline'",
    `script-src 'self' 'wasm-unsafe-eval' ${inlineScriptHashes.join(" ")}`,
    "connect-src 'self' https: wss:",
    "frame-src https://login.microsoftonline.com",
    "worker-src 'self'",
    "manifest-src 'self'",
    "upgrade-insecure-requests"
].join("; ");

configuration.globalHeaders = {
    ...(configuration.globalHeaders ?? {}),
    "Content-Security-Policy": contentSecurityPolicy,
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "strict-origin-when-cross-origin",
    "X-Frame-Options": "DENY",
    "Permissions-Policy": "camera=(), microphone=(), geolocation=(), payment=(), usb=()"
};

const serializedConfiguration = `${JSON.stringify(configuration, null, 2)}\n`;
writeFileSync(staticWebAppPath, serializedConfiguration, { encoding: "utf8", mode: 0o600 });

const configurationBytes = Buffer.from(serializedConfiguration, "utf8");
const brotliPath = `${staticWebAppPath}.br`;
if (existsSync(brotliPath)) {
    writeFileSync(
        brotliPath,
        brotliCompressSync(configurationBytes, {
            params: { [constants.BROTLI_PARAM_QUALITY]: 11 }
        }),
        { mode: 0o600 });
}
const gzipPath = `${staticWebAppPath}.gz`;
if (existsSync(gzipPath)) {
    writeFileSync(gzipPath, gzipSync(configurationBytes, { level: 9 }), { mode: 0o600 });
}
console.log(`生产安全头已生成，共授权 ${inlineScriptHashes.length} 个构建期内联脚本；未使用 script-src unsafe-inline。`);

function fail(message) {
    console.error(message);
    process.exit(1);
}
