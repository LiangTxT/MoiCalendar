const sessionKey = "moicalendar.supabase.auth.session.v1";

export function readSession() {
    return sessionStorage.getItem(sessionKey);
}

export function writeSession(json) {
    if (typeof json !== "string" || json.length === 0) {
        throw new Error("认证会话格式无效。");
    }

    sessionStorage.setItem(sessionKey, json);
}

export function clearSession() {
    sessionStorage.removeItem(sessionKey);
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
