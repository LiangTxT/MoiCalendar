# PWA 安全自动更新实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 公网部署后自动获取新版，空闲时切换；不打断任何本站窗口的编辑或保存，不删除本地日程。

**Architecture:** 保留 Blazor 按 manifest 完整性预缓存和离线 cache-first。页面负责检查更新、判断 DOM 编辑状态和接管后的一次刷新；等待中的 Worker 向所有 scope 内窗口确认安全，再在已有本地数据操作锁内激活。失败或旧页面不回应时保留旧版。

**Tech Stack:** 标准 Service Worker、MessageChannel、Web Locks、现有 Node VM 测试与 .NET 10；无新依赖。

## Task 1: 更新协议回归测试

Files: `scripts/tests/pwa-updates.test.mjs`、`scripts/tests/pwa-update-worker.test.mjs`。

- [x] 编写 VM 行为测试：`assert.equal(reloads, 1)` 检查一次控制器切换；`assert.equal(activations, 0)` 检查编辑、写锁、未响应旧窗口拒绝激活；离线检查不发请求。
- [x] 执行 `node --test scripts/tests/pwa-*.test.mjs`，确认缺少更新机制的失败。

## Task 2: 页面检查与 Worker 激活

Files: 新建 `wwwroot/pwaUpdates.js`、`wwwroot/pwaUpdateWorker.js`、`wwwroot/css/pwa-updates.css`；修改 `wwwroot/index.html`、`wwwroot/service-worker.published.js`。

- [x] 页面以 `navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' })` 注册，加载、恢复前台、恢复联网及每分钟检查 `registration.update()`；等待中的新版每 5 秒在空闲状态请求激活。
- [x] Worker 仅在明确消息请求、资源安装完成、所有页面回应 ready 时调用 `self.skipWaiting()`。以 `navigator.locks.request('moicalendar-local-data-operation', { ifAvailable: true }, ...)` 保护正在保存的数据；锁不可用或无法确认安全时延期。
- [x] 页面检查编辑弹窗、脏表单、焦点、拖动及近期交互；安全时使 app-frame inert，完成第二阶段提交确认才激活。未提交的握手可超时恢复；提交后保持保护至真正激活。标准 controllerchange 后只刷新一次，不使用 clients.claim 接管未确认窗口。
- [x] 新窗口共享启动门槛与 Worker 独占更新锁协调；锁内复查客户端，合并并发恢复申请；等待 Worker 被淘汰时重新恢复门槛。缓存安装与旧缓存回收使用另一独立锁协调。
- [x] 执行两组测试，22 项通过。

## Task 3: 发布边界和验证

Files: `wwwroot/staticwebapp.config.json`、`scripts/validate-production-publish.mjs`、`docs/static-hosting.md`。

- [x] 给入口和 Worker/manifest 配置 `Cache-Control: no-cache`，不让 HTTP/CDN 长期持有旧更新入口；不改变领域、存储、同步逻辑。
- [x] 发布验证器检查更新脚本、激活协议和缓存头；文档说明完整发布、首次旧版迁移、离线保持旧版，以及不得清理 IndexedDB。
- [x] 执行 `node --test scripts/tests/*.test.mjs`（232 项通过）、`dotnet test --no-restore`（728 项通过）。
- [x] 用户确认临时 OneDrive 权限保留后，仅本地 `dotnet publish src/MoiCalendar.App -c Release`；在仓库外临时目录运行本地静态预览，不上传公网。
- [x] Chromium 桌面 1280×720、手机尺寸 390×844：v1→v2 空闲自动刷新；v2→v3 编辑窗口使两窗口延期、保存后两窗口更新；离线查看已有日程、新建后离线重开；v3→v4 模拟激活延迟 17 秒，保护锁持续，新开窗口受门槛保护，最终三窗口更新。无应用异常和错误遮罩；截图、脚本保存在仓库外。
- [x] 浏览器发现 Windows 发布 CSP 的 CRLF 哈希不匹配；规范化计算文本而不修改资源字节，LF／CRLF／CR 三项测试先红后绿，重新生成匹配的安全头后启动正常。Browser 插件不可用，使用已缓存的 Playwright CLI；未新增依赖。
- [x] 独立代码审查通过，无剩余 Critical／Important；`dotnet build --no-restore` 成功，0 警告、0 错误。不提交、不 push、不部署；关闭本次独立测试浏览器和本地 QA 服务，保留仓库外验证截图。
