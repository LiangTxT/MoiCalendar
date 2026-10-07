self.addEventListener("push", event => {
    event.waitUntil((async () => {
        let payload;
        try { payload = event.data?.json(); } catch { return; }
        await self.moicalendarReminderNotifications.showPush(payload, self.registration);
    })());
});
self.addEventListener("notificationclick", event => {
    event.notification.close();
    event.waitUntil((async () => {
        const route = event.notification.data?.url;
        if (typeof route !== "string" || !/^\?view=day&date=\d{4}-\d{2}-\d{2}$/.test(route)) return;
        const url = new URL(route, self.registration.scope).href;
        const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        const current = windows.find(client => client.url.startsWith(self.registration.scope));
        if (current) { await current.navigate(url); await current.focus(); }
        else await self.clients.openWindow(url);
    })());
});
