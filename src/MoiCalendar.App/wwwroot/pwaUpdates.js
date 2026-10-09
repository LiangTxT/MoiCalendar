// 只管理应用资源更新和页面交互；不读取或清理任何日历业务存储。
(() => {
    const sw = navigator.serviceWorker;
    let registration, started, checking = false, disposed = false, reloaded = false;
    let controller = sw?.controller, controllerChanged = false;
    let prepared = null, freezeTimer, checkTimer, retryTimer, notice;
    let lastActivity = -Infinity;
    let gate, gatePending;
    const updateLockName = 'moicalendar-app-update';
    // 新窗口必须先取得共享通行锁。正在切换版本时，只渲染，不允许操作旧界面。
    function acquireGate() {
        if (!navigator.locks?.request || disposed || gate) return Promise.resolve();
        if (gatePending) return gatePending;
        const root = document.getElementById('app');
        const wasInert = root?.inert;
        if (root) root.inert = true;
        gatePending = new Promise(resolve => {
            const hold = async lock => {
                if (!lock) {
                    resolve();
                    await navigator.locks.request(updateLockName, { mode: 'shared' }, () => {
                        if (!disposed && !reloaded) { reloaded = true; window.location.reload(); }
                    });
                    return;
                }
                if (disposed) { resolve(); return; }
                let release;
                const held = new Promise(done => { release = done; });
                gate = { release, done: null };
                const lease = gate;
                if (root) root.inert = wasInert;
                resolve();
                await held;
                lease.done?.();
            };
            navigator.locks.request(updateLockName, { mode: 'shared', ifAvailable: true }, hold)
                .catch(() => { if (root) root.inert = wasInert; resolve(); });
        }).finally(() => { gatePending = null; });
        return gatePending;
    }
    async function releaseGate() {
        await gatePending;
        if (!gate) return;
        const lease = gate; gate = null;
        await new Promise(resolve => { lease.done = resolve; lease.release(); });
    }
    const dirtyRoots = new Set();
    const editSelector = 'input,textarea,select,[contenteditable="true"]';
    const busySelector = '[role="dialog"],dialog[open],[aria-busy="true"],.is-browser-interacting,.is-direct-manipulation,.is-period-paging';
    const editable = () => {
        for (const root of dirtyRoots) if (!root.isConnected) dirtyRoots.delete(root);
        return !document.querySelector('.app-frame') || dirtyRoots.size > 0 ||
            !!document.querySelector(busySelector) || document.activeElement?.matches?.(editSelector) ||
            window.location.pathname.includes('/authentication/') || performance.now() - lastActivity < 2000;
    };
    function inform(text) {
        if (!notice) {
            notice = document.createElement('aside');
            notice.className = 'pwa-update-notice';
            notice.setAttribute('role', 'status');
            notice.setAttribute('aria-live', 'polite');
            document.body.append(notice);
        }
        notice.textContent = text;
        notice.hidden = !text;
    }
    async function thaw() {
        clearTimeout(freezeTimer);
        const previous = prepared;
        prepared = null;
        previous?.worker?.removeEventListener?.('statechange', obsolete);
        await acquireGate();
        if (previous?.frame?.isConnected && !prepared) previous.frame.inert = previous.wasInert;
    }
    function reloadIfSafe() {
        if (reloaded || disposed || !controllerChanged || (!prepared && editable())) return;
        reloaded = true;
        window.location.reload();
    }
    function tryWaiting() {
        if (disposed || navigator.onLine === false || document.visibilityState !== 'visible') return;
        reloadIfSafe();
        if (reloaded || prepared) return;
        if (registration?.waiting) {
            if (editable()) inform('新版已就绪。请保存并关闭编辑界面，或返回日历后自动更新。');
            else registration.waiting.postMessage({ type: 'MOI_REQUEST_UPDATE' });
        } else if (!controllerChanged) {
            sw.controller?.postMessage?.({ type: 'MOI_PAGE_READY' });
        }
    }
    async function check() {
        if (disposed || checking || prepared || navigator.onLine === false || document.visibilityState !== 'visible') return;
        checking = true;
        try {
            if (!registration) {
                registration = await sw.register('service-worker.js', { updateViaCache: 'none' });
                if (disposed) return;
                registration.addEventListener('updatefound', updateFound);
                updateFound();
            }
            await registration.update();
        }
        catch { /* 断网或部署中资源尚未就绪时保留当前应用，下次联网重试。 */ }
        finally { checking = false; tryWaiting(); }
    }
    function activity() { lastActivity = performance.now(); }
    function input(event) {
        activity();
        if (event.target.matches?.(editSelector)) {
            dirtyRoots.add(event.target.closest('form,[role="dialog"],.settings-section') ?? event.target);
        }
    }
    async function message(event) {
        const { type, token } = event.data ?? {};
        const workerUrl = new URL('service-worker.js', document.baseURI ?? window.location.href).href;
        if (disposed || event.source?.scriptURL !== workerUrl || typeof token !== 'string') return;
        if (type === 'MOI_PREPARE_UPDATE') {
            const ready = navigator.onLine !== false && !editable();
            if (ready) {
                if (prepared) { event.ports[0]?.postMessage({ token, ready: false }); return; }
                const frame = document.querySelector('.app-frame');
                prepared = { token, frame, wasInert: frame.inert, committed: false, worker: event.source };
                event.source.addEventListener?.('statechange', obsolete);
                frame.inert = true;
                inform('正在应用新版…本地日程会保留。');
                // 仅未提交的握手可超时撤销；skipWaiting 提交后不能恢复旧版操作。
                freezeTimer = setTimeout(() => {
                    void thaw();
                    inform('新版尚未完成切换，已恢复当前页面；稍后会自动重试。');
                }, 15000);
                await releaseGate();
            } else inform('新版已就绪。请保存并关闭编辑界面，或返回日历后自动更新。');
            event.ports[0]?.postMessage({ token, ready: ready && prepared?.token === token });
        } else if (type === 'MOI_COMMIT_UPDATE') {
            const ready = prepared?.token === token;
            if (ready) { prepared.committed = true; clearTimeout(freezeTimer); }
            event.ports[0]?.postMessage({ token, ready });
        } else if (type === 'MOI_UPDATE_DEFERRED') {
            if (prepared && prepared.token !== token) return;
            await thaw();
            inform('新版已就绪。正在等待本站其他窗口完成编辑或保存；完成后自动更新。');
        }
    }
    function obsolete() {
        // 连续部署或浏览器自发更新可淘汰等待中的 Worker；淘汰后它不可能再激活。
        if (prepared?.worker?.state === 'redundant') {
            void thaw();
            inform('检测到更新的版本，正在重新检查。');
        }
    }
    function changed() {
        const next = sw.controller;
        if (next && controller !== next && (controller || prepared)) controllerChanged = true;
        controller = next;
        reloadIfSafe();
    }
    function updateFound() {
        const worker = registration.installing;
        if (!worker) return;
        const state = () => {
            if (worker.state !== 'installed' && worker.state !== 'redundant') return;
            worker.removeEventListener('statechange', state);
            tryWaiting();
        };
        worker.addEventListener('statechange', state);
    }
    function foreground() { if (document.visibilityState === 'visible') void check(); }
    function dispose() {
        disposed = true;
        void thaw(); void releaseGate();
        clearInterval(checkTimer); clearInterval(retryTimer);
        sw?.removeEventListener('message', message);
        sw?.removeEventListener('controllerchange', changed);
        registration?.removeEventListener('updatefound', updateFound);
        document.removeEventListener('visibilitychange', foreground);
        document.removeEventListener('input', input, true);
        document.removeEventListener('change', input, true);
        document.removeEventListener('pointerdown', activity, true);
        document.removeEventListener('keydown', activity, true);
        window.removeEventListener('online', check);
        window.removeEventListener('pagehide', pageHide);
        window.removeEventListener('pageshow', pageShow);
        notice?.remove(); notice = null;
        dirtyRoots.clear();
    }
    function pageHide(event) {
        // bfcache 保留状态与监听器，避免恢复后丢失未保存标记；后台计时器不发网络请求。
        if (!event.persisted) dispose();
    }
    function pageShow(event) {
        if (event.persisted) {
            changed();
            if (!prepared?.committed) void thaw();
            void check();
        }
    }
    async function start() {
        if (started) return started;
        if (!sw) return;
        started = (async () => {
            sw.addEventListener('message', message);
            sw.addEventListener('controllerchange', changed);
            document.addEventListener('input', input, true);
            document.addEventListener('change', input, true);
            document.addEventListener('pointerdown', activity, true);
            document.addEventListener('keydown', activity, true);
            document.addEventListener('visibilitychange', foreground);
            window.addEventListener('online', check);
            window.addEventListener('pagehide', pageHide);
            window.addEventListener('pageshow', pageShow);
            await acquireGate();
            if (disposed) return;
            checkTimer = setInterval(check, 60000);
            retryTimer = setInterval(tryWaiting, 5000);
            await check();
        })();
        return started;
    }
    window.moicalendarUpdates = { start, dispose };
})();
