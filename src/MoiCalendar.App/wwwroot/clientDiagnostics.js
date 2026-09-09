export function getBroadClientPlatform() {
    const userAgent = navigator.userAgent ?? "";
    if (/android/i.test(userAgent)) {
        return "Android";
    }
    if (/ipad/i.test(userAgent) ||
        (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1)) {
        return "iPadOS";
    }
    if (/iphone|ipod/i.test(userAgent)) {
        return "iOS";
    }
    if (/windows/i.test(userAgent)) {
        return "Windows";
    }
    if (/macintosh|mac os x/i.test(userAgent)) {
        return "macOS";
    }
    if (/linux/i.test(userAgent)) {
        return "Linux";
    }
    return "Browser";
}
