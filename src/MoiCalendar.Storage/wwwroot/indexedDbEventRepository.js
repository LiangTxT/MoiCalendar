let databasePromise;
let configuredDatabaseName;
let configuredDatabaseVersion;
let configuredEventStoreName;
let configuredOperationStoreName;
let configuredSettingsStoreName;
let configuredSyncLogStoreName;
let configuredRestoreSnapshotStoreName;
const heldOperationLocks = new Map();
const exclusiveOperationLockName = "moicalendar-local-data-operation";
const latestRestoreSnapshotKey = "latest";
const restoreSyncBlockedSettingKey = "restoreSyncBlocked";

export async function initialize(
    databaseName,
    databaseVersion,
    eventStoreName,
    operationStoreName,
    settingsStoreName,
    syncLogStoreName,
    restoreSnapshotStoreName) {
    if (
        !databaseName ||
        !eventStoreName ||
        !operationStoreName ||
        !settingsStoreName ||
        !syncLogStoreName ||
        !restoreSnapshotStoreName ||
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
            configuredSettingsStoreName !== settingsStoreName ||
            configuredSyncLogStoreName !== syncLogStoreName ||
            configuredRestoreSnapshotStoreName !== restoreSnapshotStoreName
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
    configuredSyncLogStoreName = syncLogStoreName;
    configuredRestoreSnapshotStoreName = restoreSnapshotStoreName;
    databasePromise = openDatabase(
        databaseName,
        databaseVersion,
        eventStoreName,
        operationStoreName,
        settingsStoreName,
        syncLogStoreName,
        restoreSnapshotStoreName);

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

export async function getEventByIdIncludingDeleted(id) {
    validateId(id);
    const calendarEvent = await getStoredEvent(id);
    if (!calendarEvent) {
        return null;
    }

    validateEvent(calendarEvent);
    return calendarEvent;
}

export async function upsertEvent(calendarEvent) {
    validateEvent(calendarEvent);
    const database = await getDatabase();
    const transaction = database.transaction(configuredEventStoreName, "readwrite");
    const request = transaction.objectStore(configuredEventStoreName).put(calendarEvent);
    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
    return calendarEvent;
}

export async function getAllEventsIncludingDeleted() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredEventStoreName, "readonly");
    const request = transaction.objectStore(configuredEventStoreName).getAll();
    const [records] = await Promise.all([
        requestAsPromise(request),
        transactionAsPromise(transaction)
    ]);

    for (const calendarEvent of records) {
        validateEvent(calendarEvent);
    }

    return records.sort((left, right) => left.id.localeCompare(right.id));
}

export async function replaceAllEventsAndResetSync(calendarEvents) {
    if (!Array.isArray(calendarEvents)) {
        throw new Error("恢复事件列表无效。");
    }

    const eventIds = new Set();
    for (const calendarEvent of calendarEvents) {
        validateEvent(calendarEvent);
        if (eventIds.has(calendarEvent.id)) {
            throw new Error("恢复事件包含重复 ID。");
        }
        eventIds.add(calendarEvent.id);
    }

    const database = await getDatabase();
    const transaction = database.transaction(
        [
            configuredEventStoreName,
            configuredOperationStoreName,
            configuredSettingsStoreName,
            configuredRestoreSnapshotStoreName
        ],
        "readwrite");
    const completion = transactionAsPromise(transaction);

    try {
        const eventStore = transaction.objectStore(configuredEventStoreName);
        const operationStore = transaction.objectStore(configuredOperationStoreName);
        const settingsStore = transaction.objectStore(configuredSettingsStoreName);
        const snapshotStore = transaction.objectStore(configuredRestoreSnapshotStoreName);
        const [existingEvents, existingOperations, existingSyncGuard] = await Promise.all([
            requestAsPromise(eventStore.getAll()),
            requestAsPromise(operationStore.getAll()),
            requestAsPromise(settingsStore.get(restoreSyncBlockedSettingKey))
        ]);
        const createdAtUtc = new Date().toISOString();
        const snapshot = {
            key: latestRestoreSnapshotKey,
            createdAtUtc,
            calendarEvents: existingEvents,
            syncOperations: existingOperations,
            wasSyncBlocked: existingSyncGuard?.value === true
        };
        const requests = [
            snapshotStore.put(snapshot),
            eventStore.clear(),
            operationStore.clear(),
            settingsStore.delete("syncStatus"),
            settingsStore.put({ key: restoreSyncBlockedSettingKey, value: true }),
            ...calendarEvents.map(calendarEvent => eventStore.put(calendarEvent))
        ];

        await Promise.all([
            ...requests.map(requestAsPromise),
            completion
        ]);

        return {
            createdAtUtc,
            eventCount: existingEvents.length,
            syncOperationCount: existingOperations.length
        };
    } catch (error) {
        await abortTransactionAfterFailure(transaction, completion);
        throw error;
    }
}

export async function getRestoreSafetySnapshot() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredRestoreSnapshotStoreName, "readonly");
    const completion = transactionAsPromise(transaction);
    const snapshot = await requestAsPromise(
        transaction.objectStore(configuredRestoreSnapshotStoreName).get(latestRestoreSnapshotKey));
    await completion;

    if (!snapshot) {
        return null;
    }

    validateRestoreSafetySnapshot(snapshot);
    return {
        createdAtUtc: snapshot.createdAtUtc,
        eventCount: snapshot.calendarEvents.length,
        syncOperationCount: snapshot.syncOperations.length
    };
}

export async function restoreLatestSafetySnapshot() {
    const database = await getDatabase();
    const transaction = database.transaction(
        [
            configuredEventStoreName,
            configuredOperationStoreName,
            configuredSettingsStoreName,
            configuredRestoreSnapshotStoreName
        ],
        "readwrite");
    const completion = transactionAsPromise(transaction);
    try {
        const eventStore = transaction.objectStore(configuredEventStoreName);
        const operationStore = transaction.objectStore(configuredOperationStoreName);
        const settingsStore = transaction.objectStore(configuredSettingsStoreName);
        const snapshotStore = transaction.objectStore(configuredRestoreSnapshotStoreName);
        const snapshot = await requestAsPromise(snapshotStore.get(latestRestoreSnapshotKey));
        if (!snapshot) {
            throw new Error("没有可用的恢复前安全快照。");
        }

        validateRestoreSafetySnapshot(snapshot);
        const requests = [
            eventStore.clear(),
            operationStore.clear(),
            settingsStore.delete("syncStatus"),
            snapshot.wasSyncBlocked
                ? settingsStore.put({ key: restoreSyncBlockedSettingKey, value: true })
                : settingsStore.delete(restoreSyncBlockedSettingKey),
            ...snapshot.calendarEvents.map(calendarEvent => eventStore.put(calendarEvent)),
            ...snapshot.syncOperations.map(operation => operationStore.put(operation)),
            snapshotStore.delete(latestRestoreSnapshotKey)
        ];
        await Promise.all([
            ...requests.map(requestAsPromise),
            completion
        ]);

        return {
            eventCount: snapshot.calendarEvents.length,
            safetySnapshotCreatedAtUtc: snapshot.createdAtUtc
        };
    } catch (error) {
        await abortTransactionAfterFailure(transaction, completion);
        throw error;
    }
}

export async function isSyncBlockedAfterRestore() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredSettingsStoreName, "readonly");
    const completion = transactionAsPromise(transaction);
    const setting = await requestAsPromise(
        transaction.objectStore(configuredSettingsStoreName).get(restoreSyncBlockedSettingKey));
    await completion;
    return setting?.value === true;
}

export async function allowSyncAfterRestore() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredSettingsStoreName, "readwrite");
    const request = transaction.objectStore(configuredSettingsStoreName)
        .delete(restoreSyncBlockedSettingKey);
    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
}

export async function acquireExclusiveOperationLock() {
    if (!navigator.locks?.request) {
        throw new Error("当前浏览器不支持安全的跨页面数据操作锁。");
    }

    const leaseId = typeof crypto.randomUUID === "function" ? crypto.randomUUID() : createUuid();
    let releaseLock;
    let markAcquired;
    let markFailed;
    const acquired = new Promise((resolve, reject) => {
        markAcquired = resolve;
        markFailed = reject;
    });
    const releaseRequested = new Promise(resolve => {
        releaseLock = resolve;
    });
    const lockRequest = navigator.locks.request(exclusiveOperationLockName, async () => {
        markAcquired();
        await releaseRequested;
    });
    lockRequest.catch(markFailed);
    await acquired;
    heldOperationLocks.set(leaseId, { releaseLock, lockRequest });
    return leaseId;
}

export async function releaseExclusiveOperationLock(leaseId) {
    const lease = heldOperationLocks.get(leaseId);
    if (!lease) {
        return;
    }

    heldOperationLocks.delete(leaseId);
    lease.releaseLock();
    await lease.lockRequest;
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

export async function getRecurringEventMasters() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredEventStoreName, "readonly");
    const request = transaction.objectStore(configuredEventStoreName).getAll();
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
            typeof calendarEvent.recurrenceRule === "string" &&
            calendarEvent.recurrenceRule.trim().length > 0)
        .sort((left, right) =>
            Date.parse(left.startUtc) - Date.parse(right.startUtc) ||
            left.id.localeCompare(right.id));
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

export async function applyCalendarImport(changes) {
    if (!Array.isArray(changes)) {
        throw new Error("导入变更不是有效数组。");
    }

    for (const change of changes) {
        if (!change || typeof change !== "object") {
            throw new Error("导入变更不是有效对象。");
        }
        validateEventAndOperation(
            change.calendarEvent,
            change.operation,
            change.expectedExistingEventId ? 1 : 0);
    }

    const database = await getDatabase();
    const transaction = database.transaction(
        [configuredEventStoreName, configuredOperationStoreName],
        "readwrite");
    const completion = transactionAsPromise(transaction);
    try {
        const eventStore = transaction.objectStore(configuredEventStoreName);
        const operationStore = transaction.objectStore(configuredOperationStoreName);
        const existingEvents = await requestAsPromise(eventStore.getAll());
        const externalUidLookup = new Map();
        for (const existingEvent of existingEvents) {
            validateEvent(existingEvent);
            if (typeof existingEvent.externalUid === "string" && existingEvent.externalUid.length > 0) {
                externalUidLookup.set(existingEvent.externalUid, existingEvent);
            }
        }

        for (const change of changes) {
            const importedEvent = change.calendarEvent;
            const currentDuplicate = externalUidLookup.get(importedEvent.externalUid);
            if (change.expectedExistingEventId) {
                if (!currentDuplicate || currentDuplicate.id !== change.expectedExistingEventId) {
                    throw new Error("预览后本地重复事件已发生变化，请重新预览后导入。");
                }
                if (new Date(currentDuplicate.updatedAtUtc).getTime() !==
                    new Date(change.expectedExistingUpdatedAtUtc).getTime()) {
                    throw new Error("预览后本地事件已被修改，请重新预览后导入。");
                }
            } else if (currentDuplicate) {
                throw new Error("预览后出现了相同 UID 的本地事件，请重新预览后导入。");
            }

            externalUidLookup.set(importedEvent.externalUid, importedEvent);
            if (change.expectedExistingEventId) {
                eventStore.put(importedEvent);
            } else {
                eventStore.add(importedEvent);
            }
            operationStore.add(change.operation);
        }

        await completion;
    } catch (error) {
        await abortTransactionAfterFailure(transaction, completion);
        throw error;
    }
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

export async function addSyncLogEntry(entry, retentionLimit) {
    validateSyncLogEntry(entry);
    if (!Number.isInteger(retentionLimit) || retentionLimit < 1) {
        throw new Error("同步日志保留数量无效。");
    }

    const database = await getDatabase();
    const transaction = database.transaction(configuredSyncLogStoreName, "readwrite");
    const completion = transactionAsPromise(transaction);
    const store = transaction.objectStore(configuredSyncLogStoreName);
    await requestAsPromise(store.put(entry));
    const entries = await requestAsPromise(store.getAll());
    entries.sort((left, right) =>
        Date.parse(right.timestampUtc) - Date.parse(left.timestampUtc) ||
        right.id.localeCompare(left.id));

    for (const expired of entries.slice(retentionLimit)) {
        store.delete(expired.id);
    }

    await completion;
}

export async function getSyncLogEntries() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredSyncLogStoreName, "readonly");
    const completion = transactionAsPromise(transaction);
    const entries = await requestAsPromise(
        transaction.objectStore(configuredSyncLogStoreName).getAll());
    await completion;

    for (const entry of entries) {
        validateSyncLogEntry(entry);
    }

    return entries.sort((left, right) =>
        Date.parse(right.timestampUtc) - Date.parse(left.timestampUtc) ||
        right.id.localeCompare(left.id));
}

export async function clearSyncLogEntries() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredSyncLogStoreName, "readwrite");
    const request = transaction.objectStore(configuredSyncLogStoreName).clear();
    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
}

export async function getSyncStatusState() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredSettingsStoreName, "readonly");
    const completion = transactionAsPromise(transaction);
    const record = await requestAsPromise(
        transaction.objectStore(configuredSettingsStoreName).get("syncStatus"));
    await completion;
    return record?.value ?? null;
}

export async function saveSyncStatusState(state) {
    if (!state || typeof state !== "object") {
        throw new Error("同步状态无效。");
    }

    const database = await getDatabase();
    const transaction = database.transaction(configuredSettingsStoreName, "readwrite");
    const request = transaction.objectStore(configuredSettingsStoreName)
        .put({ key: "syncStatus", value: state });
    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
}

export async function getCalendarViewPreference() {
    const database = await getDatabase();
    const transaction = database.transaction(configuredSettingsStoreName, "readonly");
    const completion = transactionAsPromise(transaction);
    const record = await requestAsPromise(
        transaction.objectStore(configuredSettingsStoreName).get("calendarView"));
    await completion;
    return typeof record?.value === "string" ? record.value : null;
}

export async function saveCalendarViewPreference(viewMode) {
    if (!["Month", "Week", "Agenda"].includes(viewMode)) {
        throw new Error("日历视图偏好无效。");
    }

    const database = await getDatabase();
    const transaction = database.transaction(configuredSettingsStoreName, "readwrite");
    const request = transaction.objectStore(configuredSettingsStoreName)
        .put({ key: "calendarView", value: viewMode });
    await Promise.all([requestAsPromise(request), transactionAsPromise(transaction)]);
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
    settingsStoreName,
    syncLogStoreName,
    restoreSnapshotStoreName) {
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

            const syncLogStore = database.objectStoreNames.contains(syncLogStoreName)
                ? request.transaction.objectStore(syncLogStoreName)
                : database.createObjectStore(syncLogStoreName, { keyPath: "id" });

            if (!syncLogStore.indexNames.contains("timestampUtc")) {
                syncLogStore.createIndex("timestampUtc", "timestampUtc", { unique: false });
            }

            if (!database.objectStoreNames.contains(restoreSnapshotStoreName)) {
                database.createObjectStore(restoreSnapshotStoreName, { keyPath: "key" });
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

async function abortTransactionAfterFailure(transaction, completion) {
    try {
        transaction.abort();
    } catch {
        // The transaction may already be committed or aborting.
    }

    try {
        await completion;
    } catch {
        // Preserve and rethrow the original operation error.
    }
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

    if (calendarEvent.recurrenceRule !== null &&
        calendarEvent.recurrenceRule !== undefined &&
        typeof calendarEvent.recurrenceRule !== "string") {
        throw new Error("事件字段 recurrenceRule 无法序列化。");
    }

    if (calendarEvent.externalUid !== null &&
        calendarEvent.externalUid !== undefined &&
        (typeof calendarEvent.externalUid !== "string" ||
            calendarEvent.externalUid.length === 0 ||
            calendarEvent.externalUid.length > 1024)) {
        throw new Error("事件字段 externalUid 无法序列化。");
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

function validateRestoreSafetySnapshot(snapshot) {
    if (!snapshot ||
        snapshot.key !== latestRestoreSnapshotKey ||
        !Array.isArray(snapshot.calendarEvents) ||
        !Array.isArray(snapshot.syncOperations) ||
        typeof snapshot.wasSyncBlocked !== "boolean") {
        throw new Error("本地恢复安全快照无效。");
    }

    validateDateValue(snapshot.createdAtUtc, "createdAtUtc");
    for (const calendarEvent of snapshot.calendarEvents) {
        validateEvent(calendarEvent);
    }
    for (const operation of snapshot.syncOperations) {
        validateSyncOperation(operation);
    }
}

function validateStatus(status) {
    if (!Number.isInteger(status) || status < 0 || status > 3) {
        throw new Error("同步操作状态无效。");
    }
}

function validateSyncLogEntry(entry) {
    if (!entry || typeof entry !== "object") {
        throw new Error("同步日志不是有效对象。");
    }

    validateId(entry.id);
    validateDateValue(entry.timestampUtc, "timestampUtc");
    if (!Number.isInteger(entry.severity) || entry.severity < 0 || entry.severity > 2) {
        throw new Error("同步日志级别无效。");
    }
    if (!Number.isInteger(entry.stage) || entry.stage < 0 || entry.stage > 3) {
        throw new Error("同步日志阶段无效。");
    }
    if (typeof entry.provider !== "string" || typeof entry.message !== "string") {
        throw new Error("同步日志文本字段无效。");
    }
    if (entry.operationId != null) {
        validateId(entry.operationId);
    }
    if (entry.errorCode != null && typeof entry.errorCode !== "string") {
        throw new Error("同步日志错误代码无效。");
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
