import { randomUUID } from "node:crypto";
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, resolve } from "node:path";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const status = readLocalStatus();
const apiUrl = new URL(status.API_URL);
if (!["127.0.0.1", "localhost", "::1"].includes(apiUrl.hostname)) {
    fail("安全检查失败：此脚本只允许连接本机 Supabase CLI 栈。");
}
const publicKey = status.PUBLISHABLE_KEY ?? status.ANON_KEY;
if (!publicKey) {
    fail("本地状态缺少 PUBLISHABLE_KEY/ANON_KEY。");
}

const email = `moicalendar-portability-${Date.now()}@example.test`;
const password = `${randomUUID()}Aa1!`;
const deviceId = randomUUID();
const firstEventId = randomUUID();

const signup = await request("auth/v1/signup", {
    method: "POST",
    body: { email, password }
});
if (!signup.access_token || !signup.user?.id) {
    fail("本地注册没有返回测试会话；请检查本地邮件确认配置。");
}

const login = await request("auth/v1/token?grant_type=password", {
    method: "POST",
    body: { email, password }
});
const accessToken = login.access_token;
const ownerId = login.user?.id;
if (!accessToken || !ownerId) {
    fail("本地登录没有返回有效会话。");
}

const registeredDevice = await registerDevice(accessToken, deviceId, "开发电脑");
assert(registeredDevice.id === deviceId, "当前设备登记失败。");
const revisionBeforeDuplicateRegistration = await currentOwnerRevision(accessToken);
const duplicateRegistration = await registerDevice(accessToken, deviceId, "不应覆盖名称");
const revisionAfterDuplicateRegistration = await currentOwnerRevision(accessToken);
assert(
    duplicateRegistration.id === deviceId &&
        duplicateRegistration.name === "开发电脑" &&
        revisionAfterDuplicateRegistration === revisionBeforeDuplicateRegistration,
    "重复 DeviceId 登记没有保持幂等、意外覆盖名称或推进了 revision。");

const firstPayload = eventPayload(firstEventId, "本地可移植性测试");
const createRequest = mutation(firstEventId, "create", null, firstPayload);
const created = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: accessToken,
    body: createRequest
});
assert(created.status === "applied" && created.server_revision > 0, "创建 mutation 未成功应用。");

const duplicate = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: accessToken,
    body: createRequest
});
assert(
    duplicate.status === "applied" && duplicate.server_revision === created.server_revision,
    "重复 mutation 没有返回稳定的幂等结果。");

const initialPull = await pull(accessToken, 0);
assert(
    initialPull.changes.some(change => change.calendar_event?.id === firstEventId),
    "初始 cursor Pull 未返回已创建事件。");

const updatedPayload = {
    ...firstPayload,
    title: "本地可移植性测试（已更新）",
    updatedAtUtc: new Date(Date.now() + 1000).toISOString()
};
const updated = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: accessToken,
    body: mutation(firstEventId, "update", created.server_revision, updatedPayload)
});
assert(updated.status === "applied", "更新 mutation 未成功应用。");

const stale = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: accessToken,
    body: mutation(firstEventId, "update", created.server_revision, {
        ...updatedPayload,
        title: "不应覆盖的新值"
    })
});
assert(stale.status === "conflict" && stale.conflict?.code === "stale_revision", "陈旧写入未返回结构化冲突。");

const updatedPull = await pull(accessToken, initialPull.cursor);
assert(
    updatedPull.changes.some(change => change.calendar_event?.title === updatedPayload.title),
    "更新后的 cursor Pull 结果不正确。");

const realtime = openRealtime(accessToken, ownerId);
await realtime.joined;
const deleted = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: accessToken,
    body: mutation(firstEventId, "delete", updated.server_revision, updatedPayload)
});
assert(deleted.status === "applied", "删除 mutation 未成功应用。");
await realtime.wakeUp;
realtime.close();

const deletedPull = await pull(accessToken, updatedPull.cursor);
const tombstone = deletedPull.changes.find(change => change.calendar_event?.id === firstEventId);
assert(tombstone?.calendar_event?.deletedAtUtc, "删除后的 Pull 未返回 tombstone。");

const legacyDeletedEventId = randomUUID();
const legacyDeletedAt = new Date().toISOString();
const legacyDeleteRequest = mutation(legacyDeletedEventId, "delete", null, {
    ...eventPayload(legacyDeletedEventId, "同步启用前已删除"),
    updatedAtUtc: legacyDeletedAt,
    deletedAtUtc: legacyDeletedAt
});
const legacyDeleted = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: accessToken,
    body: legacyDeleteRequest
});
assert(
    legacyDeleted.status === "applied" && legacyDeleted.server_revision > deletedPull.cursor,
    "云端不存在的本地删除未生成 tombstone。");
const duplicateLegacyDelete = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: accessToken,
    body: legacyDeleteRequest
});
assert(
    duplicateLegacyDelete.status === "applied" &&
        duplicateLegacyDelete.server_revision === legacyDeleted.server_revision,
    "云端不存在的重复删除没有返回稳定的幂等结果。");
const legacyDeletePull = await pull(accessToken, deletedPull.cursor);
const legacyTombstone = legacyDeletePull.changes.find(
    change => change.calendar_event?.id === legacyDeletedEventId);
assert(legacyTombstone?.calendar_event?.deletedAtUtc, "增量 Pull 未返回历史本地删除的 tombstone。");

const missedEventId = randomUUID();
const missed = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: accessToken,
    body: mutation(missedEventId, "create", null, eventPayload(missedEventId, "断线期间事件"))
});
assert(missed.status === "applied", "断线期间 mutation 未成功应用。");
const recoveryPull = await pull(accessToken, legacyDeletePull.cursor);
assert(
    recoveryPull.changes.some(change => change.calendar_event?.id === missedEventId),
    "遗漏 Realtime 通知后，cursor Pull 未恢复变更。");

const secondEmail = `moicalendar-isolation-${Date.now()}@example.test`;
const secondPassword = `${randomUUID()}Bb2!`;
const secondSignup = await request("auth/v1/signup", {
    method: "POST",
    body: { email: secondEmail, password: secondPassword }
});
assert(secondSignup.access_token, "第二个隔离测试账户注册失败。");
const duplicateOwnerAttempt = await requestAllowFailure(
    "rest/v1/rpc/moicalendar_register_device",
    {
        method: "POST",
        token: secondSignup.access_token,
        body: { p_device_id: deviceId, p_name: "伪造设备", p_platform: "PWA" }
    });
assert(
    duplicateOwnerAttempt.status === 403 &&
        duplicateOwnerAttempt.body?.message === "moicalendar_device_id_unavailable",
    "不同用户重复使用 DeviceId 时未被拒绝。");
const secondDeviceId = randomUUID();
await registerDevice(secondSignup.access_token, secondDeviceId, "隔离账户设备");
const isolatedPull = await pull(secondSignup.access_token, 0, secondDeviceId);
assert(isolatedPull.changes.length === 0, "RLS 隔离失败：第二个用户读取到了其他用户事件。");

const crossOwnerUpdate = await request("rest/v1/rpc/moicalendar_apply_calendar_mutation", {
    method: "POST",
    token: secondSignup.access_token,
    body: mutation(
        firstEventId,
        "update",
        deleted.server_revision,
        { ...updatedPayload, title: "越权更新不应成功" },
        secondDeviceId)
});
assert(
    crossOwnerUpdate.status === "conflict" &&
        crossOwnerUpdate.conflict?.code === "entity_not_found",
    "用户 B 尝试更新用户 A 的事件时没有被所有权边界拒绝。" );

const firstUserDevices = await request("rest/v1/rpc/moicalendar_list_devices", {
    method: "POST",
    token: accessToken,
    body: {}
});
const secondUserDevices = await request("rest/v1/rpc/moicalendar_list_devices", {
    method: "POST",
    token: secondSignup.access_token,
    body: {}
});
assert(
    firstUserDevices.every(device => device.id !== secondDeviceId) &&
        secondUserDevices.every(device => device.id !== deviceId),
    "设备所有权隔离失败：用户读取到了其他账户的设备。" );
const firstUserRlsDevices = await requestAllowFailure("rest/v1/devices?select=id", {
    token: accessToken
});
const secondUserRlsDevices = await requestAllowFailure("rest/v1/devices?select=id", {
    token: secondSignup.access_token
});
assert(
    [401, 403].includes(firstUserRlsDevices.status) &&
        [401, 403].includes(secondUserRlsDevices.status),
    "devices 表不应向浏览器开放直接读取；设备列表必须经过 owner RPC。" );

const crossOwnerRename = await requestAllowFailure(
    "rest/v1/rpc/moicalendar_rename_device",
    {
        method: "POST",
        token: accessToken,
        body: { p_device_id: secondDeviceId, p_name: "不应成功" }
    });
const crossOwnerRevoke = await requestAllowFailure(
    "rest/v1/rpc/moicalendar_revoke_device",
    {
        method: "POST",
        token: accessToken,
        body: { p_device_id: secondDeviceId }
    });
assert(
    crossOwnerRename.status === 403 && crossOwnerRevoke.status === 403,
    "用户 A 能够修改或撤销用户 B 的设备。" );

const unauthenticatedRpc = await requestAllowFailure(
    "rest/v1/rpc/moicalendar_list_devices",
    { method: "POST", body: {} });
assert(
    [401, 403].includes(unauthenticatedRpc.status),
    "未认证请求能够执行 MoiCalendar RPC。" );

const oldDeviceId = randomUUID();
await registerDevice(accessToken, oldDeviceId, "旧设备");
const renamedDevice = await request("rest/v1/rpc/moicalendar_rename_device", {
    method: "POST",
    token: accessToken,
    body: { p_device_id: oldDeviceId, p_name: "客厅平板" }
});
assert(renamedDevice.name === "客厅平板", "设备重命名失败。");
const revokedDevice = await request("rest/v1/rpc/moicalendar_revoke_device", {
    method: "POST",
    token: accessToken,
    body: { p_device_id: oldDeviceId }
});
assert(revokedDevice.deleted_at, "设备撤销没有生成 tombstone。");
const revokedPull = await requestAllowFailure(
    "rest/v1/rpc/moicalendar_pull_calendar_changes_for_device",
    {
        method: "POST",
        token: accessToken,
        body: { p_device_id: oldDeviceId, p_after_revision: 0, p_limit: 100 }
    });
assert(
    revokedPull.status === 403 && revokedPull.body?.message === "moicalendar_device_revoked",
    "已撤销设备仍能执行增量 Pull。" );
const revokedPushEventId = randomUUID();
const revokedPush = await requestAllowFailure(
    "rest/v1/rpc/moicalendar_apply_calendar_mutation",
    {
        method: "POST",
        token: accessToken,
        body: mutation(
            revokedPushEventId,
            "create",
            null,
            eventPayload(revokedPushEventId, "不应上传"),
            oldDeviceId)
    });
assert(
    revokedPush.status === 403 && revokedPush.body?.message === "moicalendar_device_revoked",
    "已撤销设备仍能执行 mutation Push。" );

await request("rest/v1/rpc/moicalendar_acknowledge_device_sync", {
    method: "POST",
    token: accessToken,
    body: { p_device_id: deviceId, p_server_revision: recoveryPull.cursor }
});

console.log("本地 Supabase 可移植性 smoke test 通过：Auth、设备登记/重命名/撤销/所有权、幂等写入、冲突、增量 Pull、tombstone、RLS、Realtime 唤醒和漏消息恢复均正常。");
console.log("测试只使用 Publishable key 与普通用户会话；未读取或使用 service-role/secret key。");

function mutation(entityId, operation, baseRevision, payload, mutationDeviceId = deviceId) {
    return {
        p_mutation: {
            mutation_id: randomUUID(),
            device_id: mutationDeviceId,
            entity_id: entityId,
            operation,
            base_revision: baseRevision,
            payload
        }
    };
}

function eventPayload(id, title) {
    const start = new Date(Date.now() + 3600000);
    const end = new Date(start.getTime() + 3600000);
    const now = new Date().toISOString();
    return {
        id,
        title,
        description: "",
        location: "",
        startUtc: start.toISOString(),
        endUtc: end.toISOString(),
        timeZoneId: "UTC",
        isAllDay: false,
        recurrenceRule: null,
        externalUid: null,
        createdAtUtc: now,
        updatedAtUtc: now,
        deletedAtUtc: null
    };
}

async function pull(token, afterRevision, currentDeviceId = deviceId) {
    return request("rest/v1/rpc/moicalendar_pull_calendar_changes_for_device", {
        method: "POST",
        token,
        body: { p_device_id: currentDeviceId, p_after_revision: afterRevision, p_limit: 100 }
    });
}

async function registerDevice(token, currentDeviceId, name) {
    return request("rest/v1/rpc/moicalendar_register_device", {
        method: "POST",
        token,
        body: { p_device_id: currentDeviceId, p_name: name, p_platform: "PWA" }
    });
}

async function currentOwnerRevision(token) {
    const rows = await request("rest/v1/sync_state?select=latest_revision", { token });
    assert(rows.length === 1, "无法读取当前账户的同步 revision。");
    return rows[0].latest_revision;
}

async function requestAllowFailure(path, options) {
    return request(path, { ...options, allowFailure: true });
}

async function request(path, { method = "GET", token, body, allowFailure = false } = {}) {
    const headers = {
        apikey: publicKey,
        "Content-Type": "application/json"
    };
    if (token) {
        headers.Authorization = `Bearer ${token}`;
    }
    let response;
    try {
        response = await fetch(new URL(path, ensureTrailingSlash(apiUrl)), {
            method,
            headers,
            body: body === undefined ? undefined : JSON.stringify(body)
        });
    } catch {
        fail(`无法连接本地 Supabase 路径 ${path}。`);
    }
    if (!response.ok) {
        if (allowFailure) {
            let responseBody;
            try {
                responseBody = await response.json();
            } catch {
                responseBody = null;
            }
            return { status: response.status, body: responseBody };
        }
        fail(`本地 Supabase 请求失败：${path} 返回 HTTP ${response.status}。`);
    }
    return response.json();
}

function openRealtime(token, accountId) {
    const endpoint = new URL("realtime/v1/websocket", ensureTrailingSlash(apiUrl));
    endpoint.protocol = apiUrl.protocol === "https:" ? "wss:" : "ws:";
    endpoint.searchParams.set("apikey", publicKey);
    endpoint.searchParams.set("vsn", "1.0.0");
    const socket = new WebSocket(endpoint);
    const topic = `realtime:moicalendar-portability-${accountId}`;
    const joinReference = "1";
    let joinResolve;
    let joinReject;
    let wakeResolve;
    let wakeReject;
    const joined = new Promise((resolvePromise, rejectPromise) => {
        joinResolve = resolvePromise;
        joinReject = rejectPromise;
    });
    const wakeUp = new Promise((resolvePromise, rejectPromise) => {
        wakeResolve = resolvePromise;
        wakeReject = rejectPromise;
    });
    const timeout = setTimeout(() => {
        joinReject(new Error("Realtime join 超时。"));
        wakeReject(new Error("Realtime wake-up 超时。"));
        socket.close();
    }, 10000);

    socket.addEventListener("open", () => socket.send(JSON.stringify({
        topic,
        event: "phx_join",
        payload: {
            config: {
                broadcast: { ack: false, self: false },
                presence: { enabled: false },
                postgres_changes: [{
                    event: "UPDATE",
                    schema: "public",
                    table: "sync_state",
                    filter: `owner_id=eq.${accountId}`
                }],
                private: false
            },
            access_token: token
        },
        ref: joinReference,
        join_ref: joinReference
    })));
    socket.addEventListener("message", event => {
        let message;
        try {
            message = JSON.parse(event.data);
        } catch {
            return;
        }
        if (message.event === "phx_reply" && message.ref === joinReference) {
            if (message.payload?.status !== "ok") {
                joinReject(new Error("Realtime 拒绝订阅。"));
            }
        } else if (message.event === "system" &&
            message.payload?.extension === "postgres_changes") {
            if (message.payload?.status === "ok") {
                joinResolve();
            } else {
                joinReject(new Error("Realtime Postgres Changes 订阅失败。"));
            }
        } else if (message.event === "postgres_changes") {
            clearTimeout(timeout);
            wakeResolve();
        }
    });
    socket.addEventListener("error", () => {
        joinReject(new Error("Realtime WebSocket 连接失败。"));
        wakeReject(new Error("Realtime WebSocket 连接失败。"));
    });

    return {
        joined,
        wakeUp,
        close() {
            clearTimeout(timeout);
            socket.close();
        }
    };
}

function readLocalStatus() {
    const cliEntry = join(repositoryRoot, "node_modules", "supabase", "dist", "supabase.js");
    if (!existsSync(cliEntry)) {
        fail("未安装项目级 Supabase CLI。请先运行 npm install。");
    }
    const result = spawnSync(
        process.execPath,
        [cliEntry, "status", "--output", "json"],
        { cwd: repositoryRoot, encoding: "utf8" });
    if (result.status !== 0) {
        fail("无法读取本地 Supabase 状态。请先运行 npm run supabase:start。");
    }
    const output = result.stdout.replace(/\u001b\[[0-9;]*m/g, "");
    const start = output.indexOf("{");
    const end = output.lastIndexOf("}");
    if (start < 0 || end <= start) {
        fail("Supabase CLI 没有返回可识别的 JSON 状态。");
    }
    return JSON.parse(output.slice(start, end + 1));
}

function ensureTrailingSlash(url) {
    return new URL(url.href.endsWith("/") ? url.href : `${url.href}/`);
}

function assert(condition, message) {
    if (!condition) {
        fail(message);
    }
}

function fail(message) {
    console.error(message);
    process.exit(1);
}
