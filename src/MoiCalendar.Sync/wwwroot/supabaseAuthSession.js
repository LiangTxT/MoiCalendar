const legacySessionKey = "moicalendar.supabase.auth.session.v1";
const sessionKey = "moicalendar.supabase.auth.session.v2";

export function readSession() {
    const saved = localStorage.getItem(sessionKey);
    // 空字符串是主动退出标记，防止另一旧标签页重新迁入已退出的会话。
    if (saved !== null) return saved || null;
    const legacy = sessionStorage.getItem(legacySessionKey);
    if (legacy) writeSession(legacy);
    return legacy;
}

export function writeSession(json) {
    if (typeof json !== "string" || json.length === 0) {
        throw new Error("认证会话格式无效。");
    }

    // PWA / 手机浏览器关闭后仍可恢复；不保存密码、不放入日历备份。
    localStorage.setItem(sessionKey, json);
    sessionStorage.removeItem(legacySessionKey);
}

export function clearSession() {
    localStorage.setItem(sessionKey, "");
    sessionStorage.removeItem(legacySessionKey);
}

export function readAuthCallback() {
    if (!window.location.hash || window.location.hash.length <= 1) {
        return null;
    }

    const values = new URLSearchParams(window.location.hash.substring(1));
    const recognized = values.has("access_token") ||
        values.has("refresh_token") ||
        values.has("error") ||
        values.has("error_code") ||
        values.has("error_description");
    if (!recognized) {
        return null;
    }

    const callback = {
        accessToken: values.get("access_token"),
        refreshToken: values.get("refresh_token"),
        tokenType: values.get("token_type"),
        expiresIn: parsePositiveInteger(values.get("expires_in")),
        type: values.get("type"),
        errorCode: values.get("error_code") ?? values.get("error"),
        errorDescription: values.get("error_description")
    };

    history.replaceState(null, document.title, window.location.pathname + window.location.search);
    return callback;
}

function parsePositiveInteger(value) {
    const parsed = Number.parseInt(value ?? "", 10);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}
