// Notification delivery metadata only. Calendar data remains owned by the storage layer.
globalThis.moicalendarReminderNotifications = (() => {
    const request = value => new Promise((resolve, reject) => {
        value.onsuccess = () => resolve(value.result);
        value.onerror = () => reject(value.error);
    });
    const complete = tx => new Promise((resolve, reject) => {
        tx.oncomplete = resolve;
        tx.onerror = tx.onabort = () => reject(tx.error || new Error("提醒事务中止。"));
    });
    function valid(data) {
        return data && /^[a-f0-9-]{36}$/i.test(data.eventId) && typeof data.key === "string" && data.key.length <= 300 &&
            typeof data.title === "string" && data.title.length <= 200 && Number.isFinite(Date.parse(data.dueUtc)) &&
            Number.isFinite(Date.parse(data.occurrenceStartUtc)) && Date.parse(data.dueUtc) <= Date.now() + 5000 &&
            Date.parse(data.dueUtc) >= Date.now() - 15 * 60000;
    }
    function url(data) {
        const start = new Date(data.occurrenceStartUtc);
        const localDate = `${start.getFullYear()}-${String(start.getMonth() + 1).padStart(2, "0")}-${String(start.getDate()).padStart(2, "0")}`;
        const date = data.isAllDay ? data.date || start.toISOString().slice(0, 10) : localDate;
        return `?view=day&date=${/^\d{4}-\d{2}-\d{2}$/.test(date) ? date : ""}`;
    }
    function options(data) {
        return { body: data.isAllDay ? "全天日程" : new Intl.DateTimeFormat("zh-CN", { dateStyle: "short", timeStyle: "short" }).format(new Date(data.occurrenceStartUtc)),
            tag: "moi-reminder:" + data.key, renotify: false, data: { url: url(data) }, icon: "icon-192.png" };
    }
    async function showPush(data, registration) {
        if (!valid(data) || typeof data.ownerId !== "string") return;
        const database = await request(indexedDB.open("MoiCalendar"));
        try {
            if (!database.objectStoreNames.contains("settings") || !database.objectStoreNames.contains("events")) return;
            const tx = database.transaction(["settings", "events"], "readwrite");
            const done = complete(tx);
            const settings = tx.objectStore("settings");
            const owner = await request(settings.get("reminders-push-owner"));
            const current = await request(tx.objectStore("events").get(data.eventId));
            const blocked = current && (current.deletedAtUtc ||
                (current.excludedOccurrenceStartsUtc || []).some(value => Date.parse(value) === Date.parse(data.occurrenceStartUtc)) ||
                (Date.parse(current.updatedAtUtc) > Date.parse(data.updatedAtUtc) &&
                    (current.reminderMinutesBeforeStart !== data.minutes ||
                        Date.parse(current.startUtc) !== Date.parse(data.seriesStartUtc) ||
                        current.reminderTimeZoneId !== data.reminderTimeZoneId ||
                        (current.allDayReminderMinuteOfDay ?? 420) !== data.allDayReminderMinuteOfDay)));
            const key = "reminder-delivered:" + data.key;
            const seen = await request(settings.get(key));
            if (owner?.value !== data.ownerId || blocked || seen) { await done; return; }
            settings.put({ key, value: new Date().toISOString() });
            // Prune old metadata even when this app stays closed for months.
            const records = await request(settings.getAll());
            for (const record of records) if (record.key.startsWith("reminder-delivered:") && Date.parse(record.value) < Date.now() - 30 * 86400000) settings.delete(record.key);
            await done;
            try { await registration.showNotification(data.title, options(data)); }
            catch (error) {
                const rollback = database.transaction("settings", "readwrite");
                const settled = complete(rollback);
                rollback.objectStore("settings").delete(key);
                await settled;
                throw error;
            }
        } finally { database.close(); }
    }
    return { valid, url, options, showPush };
})();
