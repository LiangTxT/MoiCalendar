export function downloadJson(fileName, json) {
    if (typeof fileName !== "string" ||
        !/^mycalendar-backup-\d{4}-\d{2}-\d{2}\.json$/.test(fileName) ||
        typeof json !== "string") {
        throw new Error("备份下载参数无效。");
    }

    const blob = new Blob([json], { type: "application/json;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = "none";
    document.body.appendChild(anchor);

    try {
        anchor.click();
    } finally {
        anchor.remove();
        window.setTimeout(() => URL.revokeObjectURL(url), 0);
    }
}
