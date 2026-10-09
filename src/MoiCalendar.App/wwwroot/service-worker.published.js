// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js');
self.importScripts('./pwaUpdateWorker.js');
self.importScripts('./_content/MoiCalendar.Storage/reminderNotifications.js', './reminderWorker.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/ ];
const runtimeFontPattern = /\.woff2$/i;
// Hosting metadata is consumed by Azure Static Web Apps during deployment rather
// than by the application. It is hardened after publish, so it must not be part
// of the integrity-checked offline cache.
const offlineAssetsExclude = [ /^service-worker\.js$/, /^staticwebapp\.config\.json$/ ];

const baseUrl = new URL('./', self.registration.scope);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall(event) {
    console.info('Service worker: Install');

    // Fetch and cache all matching items from the assets manifest
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    const populate = () => caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
    if (navigator.locks?.request) await navigator.locks.request('moicalendar-app-cache', {}, populate);
    else await populate();
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // 旧缓存由 pwaUpdateWorker 在所有页面完成重新加载后回收。
    // 激活时删除会使仍在退出旧版的窗口发生延迟加载资源失败。
}

async function onFetch(event) {
    let cachedResponse = null;
    let cache = null;
    if (event.request.method === 'GET') {
        // For all navigation requests, try to serve index.html from cache,
        // unless that request is for an offline resource.
        // If you need some URLs to be server-rendered, edit the following check to exclude those URLs
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !manifestUrlList.some(url => url === event.request.url);

        const request = shouldServeIndexHtml ? new URL('index.html', baseUrl).href : event.request;
        cache = await caches.open(cacheName);
        cachedResponse = await cache.match(request);

        if (!cachedResponse && runtimeFontPattern.test(new URL(event.request.url).pathname)) {
            const networkResponse = await fetch(event.request);
            if (networkResponse.ok) {
                await cache.put(event.request, networkResponse.clone());
            }

            return networkResponse;
        }
    }

    return cachedResponse || fetch(event.request);
}
