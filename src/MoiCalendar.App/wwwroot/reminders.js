import "./_content/MoiCalendar.Storage/reminderNotifications.js";

async function registration() {
    if (!("serviceWorker" in navigator)) return null;
    return await navigator.serviceWorker.getRegistration();
}
export async function status() {
    const worker = await registration();
    return { permission: "Notification" in globalThis ? Notification.permission : "unsupported",
        pushSupported: !!worker && "PushManager" in globalThis,
        subscribed: !!(worker && await worker.pushManager?.getSubscription()) };
}
export function requestPermission() {
    if (!("Notification" in globalThis)) return Promise.resolve("unsupported");
    return Notification.permission === "default" ? Notification.requestPermission() : Promise.resolve(Notification.permission);
}
export async function subscribe(publicKey) {
    const worker = await registration();
    if (!worker || !("PushManager" in globalThis)) throw new Error("后台推送不可用，请安装 PWA 并允许通知。");
    const padded = publicKey.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(publicKey.length / 4) * 4, "=");
    const key = Uint8Array.from(atob(padded), char => char.charCodeAt(0));
    if (key.length !== 65) throw new Error("推送公钥格式无效。");
    const old = await worker.pushManager.getSubscription();
    const oldKey = old && new Uint8Array(old.options.applicationServerKey);
    if (old && (oldKey.length !== key.length || oldKey.some((value, i) => value !== key[i]))) await old.unsubscribe();
    const subscription = await worker.pushManager.getSubscription() || await worker.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
    return JSON.stringify(subscription.toJSON());
}
export async function unsubscribe() {
    const worker = await registration();
    const subscription = await worker?.pushManager?.getSubscription();
    if (subscription) await subscription.unsubscribe();
}
export async function show(data) {
    if (!("Notification" in globalThis) || Notification.permission !== "granted") return false;
    const delivery = globalThis.moicalendarReminderNotifications;
    if (!delivery.valid(data)) return false;
    const worker = await registration();
    if (worker?.showNotification) await worker.showNotification(data.title, delivery.options(data));
    else {
        const notification = new Notification(data.title, delivery.options(data));
        notification.onclick = () => { window.focus(); window.location.assign(delivery.url(data)); notification.close(); };
    }
    return true;
}
