import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const hardener = fileURLToPath(new URL('../harden-production-publish.mjs', import.meta.url));
for (const [label, newline] of [['LF', '\n'], ['CRLF', '\r\n'], ['CR', '\r']]) {
    test(`发布 CSP 哈希按浏览器规则处理 ${label}，不修改资源完整性字节`, () => {
        const directory = mkdtempSync(join(tmpdir(), 'moicalendar-csp-'));
        try {
            const script = `${newline}  window.example = true;${newline}`;
            const html = `<html><script>${script}</script></html>`;
            writeFileSync(join(directory, 'index.html'), html);
            writeFileSync(join(directory, 'staticwebapp.config.json'), JSON.stringify({ globalHeaders: { 'Cache-Control': 'no-cache' } }));
            const result = spawnSync(process.execPath, [hardener, directory], { encoding: 'utf8' });
            assert.equal(result.status, 0, result.stderr);
            const policy = JSON.parse(readFileSync(join(directory, 'staticwebapp.config.json'), 'utf8')).globalHeaders;
            const parsedScript = script.replace(/\r\n?/g, '\n');
            const hash = createHash('sha256').update(parsedScript).digest('base64');
            assert.ok(policy['Content-Security-Policy'].includes(`'sha256-${hash}'`), '必须授权 HTML 解析后的脚本内容');
            assert.equal(policy['Cache-Control'], 'no-cache');
            assert.doesNotMatch(policy['Content-Security-Policy'].split(';').find(x => x.includes('script-src')), /unsafe-inline/);
            assert.equal(readFileSync(join(directory, 'index.html'), 'utf8'), html);
        } finally {
            // 唯一临时测试目录，仅删除本测试创建的文件。
            rmSync(directory, { recursive: true, force: true });
        }
    });
}
