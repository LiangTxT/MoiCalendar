// 等待中的 Worker 只在所有本站窗口安全时激活；旧版不回应则维持标准 waiting 行为。
(() => {
    let checking, releaseUpdateLock;
    const readyClients = new Set();
    const inScope = client => client.type === 'window' && client.url.startsWith(self.registration.scope);
    async function ask(client, token, type = 'MOI_PREPARE_UPDATE') {
        const channel = new MessageChannel();
        let timer;
        try {
            return await new Promise(resolve => {
                timer = setTimeout(() => resolve(false), 3000);
                channel.port1.onmessage = event => resolve(event.data?.token === token && event.data.ready === true);
                try { client.postMessage({ type, token }, [channel.port2]); }
                catch { resolve(false); }
            });
        } finally { clearTimeout(timer); channel.port1.close(); channel.port2.close(); }
    }
    async function activateSafely(source) {
        const clients = (await self.clients.matchAll({ type: 'window', includeUncontrolled: true })).filter(inScope);
        if (!clients.some(client => client.id === source.id)) return;
        const token = `${self.assetsManifest.version}:${crypto.randomUUID()}`;
        let activating = false;
        const deadline = Date.now() + 8000;
        try {
            const ready = await Promise.all(clients.map(client => ask(client, token)));
            if (!ready.every(Boolean) || !navigator.locks?.request) return;
            await navigator.locks.request('moicalendar-app-update', { ifAvailable: true }, async appLock => {
                if (!appLock) return;
                await navigator.locks.request('moicalendar-local-data-operation', { ifAvailable: true }, async lock => {
                    if (!lock) return;
                    const current = (await self.clients.matchAll({ type: 'window', includeUncontrolled: true })).filter(inScope);
                    if (current.some(client => !clients.some(old => old.id === client.id))) return;
                    if (Date.now() > deadline) return;
                    const committed = await Promise.all(current.map(client => ask(client, token, 'MOI_COMMIT_UPDATE')));
                    if (!committed.every(Boolean) || Date.now() > deadline) return;
                    const activated = new Promise(resolve => { releaseUpdateLock = resolve; });
                    try {
                        // 从此不可撤销。保存锁和新窗口通行锁保持到真正激活，不能按超时释放。
                        await self.skipWaiting();
                        activating = true;
                        await activated;
                    } finally { releaseUpdateLock = null; }
                });
            });
        } catch (error) {
            console.warn('应用更新暂缓；保留当前版本。', error);
        } finally {
            if (!activating) {
                for (const client of clients) {
                    try { client.postMessage({ type: 'MOI_UPDATE_DEFERRED', token }); } catch { /* 已关闭的窗口无需恢复。 */ }
                }
            }
        }
    }
    self.addEventListener('message', event => {
        if (event.data?.type === 'MOI_PAGE_READY' && event.source && inScope(event.source)) {
            readyClients.add(event.source.id);
            const cleanup = async () => {
                if (self.registration.waiting || self.registration.installing || !self.registration.active) return;
                const clients = (await self.clients.matchAll({ type: 'window', includeUncontrolled: true })).filter(inScope);
                if (!clients.length || !clients.every(client => readyClients.has(client.id))) return;
                const current = `offline-cache-${self.assetsManifest.version}`;
                // 所有窗口已载入新资源才回收旧应用缓存；从不触碰本地日程数据库。
                for (const key of await caches.keys()) {
                    if (self.registration.waiting || self.registration.installing) return;
                    if (key.startsWith('offline-cache-') && key !== current) await caches.delete(key);
                }
            };
            // 与资源预缓存共用锁，避免回收恰好开始下载的下一版本。
            event.waitUntil(navigator.locks?.request
                ? navigator.locks.request('moicalendar-app-cache', {}, cleanup)
                : Promise.resolve());
            return;
        }
        if (event.data?.type !== 'MOI_REQUEST_UPDATE' || !event.source || !inScope(event.source)) return;
        if (!self.registration.waiting) return;
        // 一个等待中的版本最多执行一轮；不同页面的重试不会重复激活。
        if (!checking) checking = activateSafely(event.source).finally(() => { checking = null; });
        event.waitUntil(checking);
    });
    self.addEventListener('activate', event => event.waitUntil((async () => {
        // 标准激活算法会通知已受控窗口；不 claim 尚未加载完毕的新窗口。
        if (!releaseUpdateLock) return;
        releaseUpdateLock();
    })()));
})();
