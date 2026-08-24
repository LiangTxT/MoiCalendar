let databasePromise;
let configuredDatabaseName;
let configuredDatabaseVersion;
let configuredStoreName;

export async function initialize(databaseName, databaseVersion, storeName) {
    if (!databaseName || !storeName || !Number.isInteger(databaseVersion) || databaseVersion < 1) {
        throw new Error("IndexedDB 初始化参数无效。");
    }

    if (databasePromise) {
        if (
            configuredDatabaseName !== databaseName ||
            configuredDatabaseVersion !== databaseVersion ||
            configuredStoreName !== storeName
        ) {
            throw new Error("IndexedDB 已使用不同配置初始化。");
        }

        await databasePromise;
        return;
    }

    configuredDatabaseName = databaseName;
    configuredDatabaseVersion = databaseVersion;
    configuredStoreName = storeName;
    databasePromise = openDatabase(databaseName, databaseVersion, storeName);

    try {
        await databasePromise;
    } catch (error) {
        databasePromise = undefined;
        throw error;
    }
}

export async function createEvent(calendarEvent) {
    validateEvent(calendarEvent);
    const database = await getDatabase();
    const transaction = database.transaction(configuredStoreName, "readwrite");
    const request = transaction.objectStore(configuredStoreName).add(calendarEvent);

    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
    return calendarEvent;
}

export async function updateEvent(calendarEvent) {
    validateEvent(calendarEvent);
    const existing = await getStoredEvent(calendarEvent.id);
    if (!existing) {
        throw new Error("找不到要更新的日历事件。");
    }

    const database = await getDatabase();
    const transaction = database.transaction(configuredStoreName, "readwrite");
    const request = transaction.objectStore(configuredStoreName).put(calendarEvent);

    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
    return calendarEvent;
}

export async function deleteEvent(id, deletedAtUtc) {
    validateId(id);
    validateDateValue(deletedAtUtc, "deletedAtUtc");
    const existing = await getStoredEvent(id);

    if (!existing || existing.deletedAtUtc) {
        return false;
    }

    const deletedEvent = {
        ...existing,
        deletedAtUtc,
        updatedAtUtc: deletedAtUtc
    };
    const database = await getDatabase();
    const transaction = database.transaction(configuredStoreName, "readwrite");
    const request = transaction.objectStore(configuredStoreName).put(deletedEvent);

    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
    return true;
}

export async function getEventById(id) {
    validateId(id);
    const calendarEvent = await getStoredEvent(id);

    if (!calendarEvent || calendarEvent.deletedAtUtc) {
        return null;
    }

    validateEvent(calendarEvent);
    return calendarEvent;
}

export async function getEventsByRange(startUtc, endUtc) {
    validateDateValue(startUtc, "startUtc");
    validateDateValue(endUtc, "endUtc");

    if (Date.parse(endUtc) <= Date.parse(startUtc)) {
        throw new Error("查询结束时间必须晚于开始时间。");
    }

    const database = await getDatabase();
    const transaction = database.transaction(configuredStoreName, "readonly");
    const store = transaction.objectStore(configuredStoreName);
    const index = store.index("startUtc");
    const request = index.getAll(IDBKeyRange.upperBound(endUtc, true));
    const [records] = await Promise.all([
        requestAsPromise(request),
        transactionAsPromise(transaction)
    ]);

    for (const calendarEvent of records) {
        validateEvent(calendarEvent);
    }

    return records
        .filter(calendarEvent =>
            !calendarEvent.deletedAtUtc &&
            Date.parse(calendarEvent.startUtc) < Date.parse(endUtc) &&
            Date.parse(calendarEvent.endUtc) > Date.parse(startUtc))
        .sort((left, right) =>
            Date.parse(left.startUtc) - Date.parse(right.startUtc) ||
            left.title.localeCompare(right.title));
}

async function getStoredEvent(id) {
    const database = await getDatabase();
    const transaction = database.transaction(configuredStoreName, "readonly");
    const request = transaction.objectStore(configuredStoreName).get(id);
    const [calendarEvent] = await Promise.all([
        requestAsPromise(request),
        transactionAsPromise(transaction)
    ]);
    return calendarEvent;
}

async function getDatabase() {
    if (!databasePromise) {
        throw new Error("IndexedDB 尚未初始化。");
    }

    return await databasePromise;
}

function openDatabase(databaseName, databaseVersion, storeName) {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, databaseVersion);

        request.onupgradeneeded = () => {
            const database = request.result;
            const store = database.objectStoreNames.contains(storeName)
                ? request.transaction.objectStore(storeName)
                : database.createObjectStore(storeName, { keyPath: "id" });

            if (!store.indexNames.contains("startUtc")) {
                store.createIndex("startUtc", "startUtc", { unique: false });
            }
        };

        request.onerror = () => reject(request.error ?? new Error("无法打开 IndexedDB。"));
        request.onblocked = () => reject(new Error("IndexedDB 升级被其他页面阻止，请关闭旧页面后重试。"));
        request.onsuccess = () => {
            const database = request.result;
            database.onversionchange = () => {
                database.close();
                databasePromise = undefined;
            };
            resolve(database);
        };
    });
}

function requestAsPromise(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error ?? new Error("IndexedDB 请求失败。"));
    });
}

function transactionAsPromise(transaction) {
    return new Promise((resolve, reject) => {
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error ?? new Error("IndexedDB 事务失败。"));
        transaction.onabort = () => reject(transaction.error ?? new Error("IndexedDB 事务已中止。"));
    });
}

function validateEvent(calendarEvent) {
    if (!calendarEvent || typeof calendarEvent !== "object") {
        throw new Error("事件数据不是有效对象。");
    }

    validateId(calendarEvent.id);

    for (const property of ["title", "description", "location", "timeZoneId"]) {
        if (typeof calendarEvent[property] !== "string") {
            throw new Error(`事件字段 ${property} 无法序列化。`);
        }
    }

    if (typeof calendarEvent.isAllDay !== "boolean") {
        throw new Error("事件字段 isAllDay 无法序列化。");
    }

    for (const property of ["startUtc", "endUtc", "createdAtUtc", "updatedAtUtc"]) {
        validateDateValue(calendarEvent[property], property);
    }

    if (calendarEvent.deletedAtUtc !== null && calendarEvent.deletedAtUtc !== undefined) {
        validateDateValue(calendarEvent.deletedAtUtc, "deletedAtUtc");
    }
}

function validateId(id) {
    if (typeof id !== "string" || id.length === 0) {
        throw new Error("事件 ID 无效。");
    }
}

function validateDateValue(value, propertyName) {
    if (typeof value !== "string" || Number.isNaN(Date.parse(value))) {
        throw new Error(`事件字段 ${propertyName} 不是有效日期。`);
    }
}
