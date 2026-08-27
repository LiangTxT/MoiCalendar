let databasePromise;
let configuredDatabaseName;
let configuredDatabaseVersion;
let configuredEventStoreName;
let configuredOperationStoreName;
let configuredSettingsStoreName;

export async function initialize(
    databaseName,
    databaseVersion,
    eventStoreName,
    operationStoreName,
    settingsStoreName) {
    if (
        !databaseName ||
        !eventStoreName ||
        !operationStoreName ||
        !settingsStoreName ||
        !Number.isInteger(databaseVersion) ||
        databaseVersion < 1) {
        throw new Error("IndexedDB 初始化参数无效。");
    }

    if (databasePromise) {
        if (
            configuredDatabaseName !== databaseName ||
            configuredDatabaseVersion !== databaseVersion ||
            configuredEventStoreName !== eventStoreName ||
            configuredOperationStoreName !== operationStoreName ||
            configuredSettingsStoreName !== settingsStoreName
        ) {
            throw new Error("IndexedDB 已使用不同配置初始化。");
        }

        await databasePromise;
        return;
    }

    configuredDatabaseName = databaseName;
    configuredDatabaseVersion = databaseVersion;
    configuredEventStoreName = eventStoreName;
    configuredOperationStoreName = operationStoreName;
    configuredSettingsStoreName = settingsStoreName;
    databasePromise = openDatabase(
        databaseName,
        databaseVersion,
        eventStoreName,
        operationStoreName,
        settingsStoreName);

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
    const transaction = database.transaction(configuredEventStoreName, "readwrite");
    const request = transaction.objectStore(configuredEventStoreName).add(calendarEvent);

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
    const transaction = database.transaction(configuredEventStoreName, "readwrite");
    const request = transaction.objectStore(configuredEventStoreName).put(calendarEvent);

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
    const transaction = database.transaction(configuredEventStoreName, "readwrite");
    const request = transaction.objectStore(configuredEventStoreName).put(deletedEvent);

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
    const transaction = database.transaction(configuredEventStoreName, "readonly");
    const store = transaction.objectStore(configuredEventStoreName);
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

export async function createEventWithSyncOperation(calendarEvent, operation) {
    validateEventAndOperation(calendarEvent, operation, 0);
    const database = await getDatabase();
    const transaction = database.transaction(
        [configuredEventStoreName, configuredOperationStoreName],
        "readwrite");
    const eventRequest = transaction.objectStore(configuredEventStoreName).add(calendarEvent);
    const operationRequest = transaction.objectStore(configuredOperationStoreName).add(operation);

    await Promise.all([
        requestAsPromise(eventRequest),
        requestAsPromise(operationRequest),
        transactionAsPromise(transaction)
    ]);
    return calendarEvent;
}

export async function updateEventWithSyncOperation(calendarEvent, operation) {
    validateEventAndOperation(calendarEvent, operation, 1);
    const database = await getDatabase();
    const transaction = database.transaction(
        [configuredEventStoreName, configuredOperationStoreName],
        "readwrite");
    const eventStore = transaction.objectStore(configuredEventStoreName);
    const existing = await requestAsPromise(eventStore.get(calendarEvent.id));
    if (!existing || existing.deletedAtUtc) {
        transaction.abort();
        throw new Error("找不到要更新的日历事件。");
    }

    const eventRequest = eventStore.put(calendarEvent);
    const operationRequest = transaction.objectStore(configuredOperationStoreName).add(operation);
    await Promise.all([
        requestAsPromise(eventRequest),
        requestAsPromise(operationRequest),
        transactionAsPromise(transaction)
    ]);
    return calendarEvent;
}

export async function deleteEventWithSyncOperation(deletedEvent, operation) {
    validateEventAndOperation(deletedEvent, operation, 2);
    if (!deletedEvent.deletedAtUtc) {
        throw new Error("删除事件必须包含删除时间。");
    }

    const database = await getDatabase();
    const transaction = database.transaction(
        [configuredEventStoreName, configuredOperationStoreName],
        "readwrite");
    const eventStore = transaction.objectStore(configuredEventStoreName);
    const existing = await requestAsPromise(eventStore.get(deletedEvent.id));
    if (!existing || existing.deletedAtUtc) {
        await transactionAsPromise(transaction);
        return false;
    }

    const eventRequest = eventStore.put(deletedEvent);
    const operationRequest = transaction.objectStore(configuredOperationStoreName).add(operation);
    await Promise.all([
        requestAsPromise(eventRequest),
        requestAsPromise(operationRequest),
        transactionAsPromise(transaction)
    ]);
    return true;
}

export async function addSyncOperation(operation) {
    validateSyncOperation(operation);
    const database = await getDatabase();
    const transaction = database.transaction(configuredOperationStoreName, "readwrite");
    const request = transaction.objectStore(configuredOperationStoreName).add(operation);
    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
    return operation;
}

export async function getSyncOperationById(operationId) {
    validateId(operationId);
    const database = await getDatabase();
    const transaction = database.transaction(configuredOperationStoreName, "readonly");
    const request = transaction.objectStore(configuredOperationStoreName).get(operationId);
    const [operation] = await Promise.all([
        requestAsPromise(request),
        transactionAsPromise(transaction)
    ]);

    if (operation) {
        validateSyncOperation(operation);
    }

    return operation ?? null;
}

export async function getSyncOperationsByStatus(status) {
    validateStatus(status);
    const database = await getDatabase();
    const transaction = database.transaction(configuredOperationStoreName, "readonly");
    const request = transaction.objectStore(configuredOperationStoreName).index("status").getAll(status);
    const [operations] = await Promise.all([
        requestAsPromise(request),
        transactionAsPromise(transaction)
    ]);

    for (const operation of operations) {
        validateSyncOperation(operation);
    }

    return operations.sort((left, right) =>
        Date.parse(left.timestampUtc) - Date.parse(right.timestampUtc) ||
        left.operationId.localeCompare(right.operationId));
}

export async function updateSyncOperationStatus(operationId, status) {
    validateId(operationId);
    validateStatus(status);
    const database = await getDatabase();
    const transaction = database.transaction(configuredOperationStoreName, "readwrite");
    const store = transaction.objectStore(configuredOperationStoreName);
    const operation = await requestAsPromise(store.get(operationId));
    if (!operation) {
        transaction.abort();
        throw new Error("找不到要更新的同步操作。");
    }

    operation.status = status;
    validateSyncOperation(operation);
    const request = store.put(operation);
    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
    return operation;
}

export async function getOrCreateDeviceId() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredSettingsStoreName, "readwrite");
    const store = transaction.objectStore(configuredSettingsStoreName);
    const existing = await requestAsPromise(store.get("deviceId"));

    if (existing?.value) {
        await transactionAsPromise(transaction);
        return existing.value;
    }

    const deviceId = typeof crypto.randomUUID === "function"
        ? crypto.randomUUID()
        : createUuid();
    const request = store.put({ key: "deviceId", value: deviceId });
    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
    return deviceId;
}

async function getStoredEvent(id) {
    const database = await getDatabase();
    const transaction = database.transaction(configuredEventStoreName, "readonly");
    const request = transaction.objectStore(configuredEventStoreName).get(id);
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

function openDatabase(
    databaseName,
    databaseVersion,
    eventStoreName,
    operationStoreName,
    settingsStoreName) {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, databaseVersion);

        request.onupgradeneeded = () => {
            const database = request.result;
            const store = database.objectStoreNames.contains(eventStoreName)
                ? request.transaction.objectStore(eventStoreName)
                : database.createObjectStore(eventStoreName, { keyPath: "id" });

            if (!store.indexNames.contains("startUtc")) {
                store.createIndex("startUtc", "startUtc", { unique: false });
            }

            const operationStore = database.objectStoreNames.contains(operationStoreName)
                ? request.transaction.objectStore(operationStoreName)
                : database.createObjectStore(operationStoreName, { keyPath: "operationId" });

            if (!operationStore.indexNames.contains("status")) {
                operationStore.createIndex("status", "status", { unique: false });
            }

            if (!operationStore.indexNames.contains("timestampUtc")) {
                operationStore.createIndex("timestampUtc", "timestampUtc", { unique: false });
            }

            if (!database.objectStoreNames.contains(settingsStoreName)) {
                database.createObjectStore(settingsStoreName, { keyPath: "key" });
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

function validateEventAndOperation(calendarEvent, operation, expectedOperationType) {
    validateEvent(calendarEvent);
    validateSyncOperation(operation);

    if (operation.entityId !== calendarEvent.id) {
        throw new Error("同步操作与日历事件不匹配。");
    }

    if (operation.operationType !== expectedOperationType) {
        throw new Error("同步操作类型与事件变更不匹配。");
    }
}

function validateSyncOperation(operation) {
    if (!operation || typeof operation !== "object") {
        throw new Error("同步操作不是有效对象。");
    }

    validateId(operation.operationId);
    validateId(operation.entityId);

    if (typeof operation.deviceId !== "string" || operation.deviceId.length === 0) {
        throw new Error("同步操作的设备标识无效。");
    }

    if (!Number.isInteger(operation.operationType) || operation.operationType < 0 || operation.operationType > 2) {
        throw new Error("同步操作类型无效。");
    }

    validateDateValue(operation.timestampUtc, "timestampUtc");

    if (typeof operation.payload !== "string" || operation.payload.length === 0) {
        throw new Error("同步操作负载无效。");
    }

    validateStatus(operation.status);
}

function validateStatus(status) {
    if (!Number.isInteger(status) || status < 0 || status > 2) {
        throw new Error("同步操作状态无效。");
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

function createUuid() {
    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0"));
    return `${hex.slice(0, 4).join("")}-${hex.slice(4, 6).join("")}-${hex.slice(6, 8).join("")}-${hex.slice(8, 10).join("")}-${hex.slice(10).join("")}`;
}
