using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.App;

public sealed record SyncIndicatorPresentation(string CssClass, string Label)
{
    public static SyncIndicatorPresentation From(
        CloudSyncDisplayState? state, DateTimeOffset? lastSuccessfulSyncAtUtc) => state switch
    {
        CloudSyncDisplayState.Synced when lastSuccessfulSyncAtUtc is not null =>
            new("success", "同步成功"),
        CloudSyncDisplayState.Synced => new("neutral", "尚未成功同步"),
        CloudSyncDisplayState.Syncing => new("active", "正在同步"),
        CloudSyncDisplayState.PendingChanges => new("warning", "有待同步变更"),
        CloudSyncDisplayState.RetryScheduled => new("warning", "同步未完成，等待重试"),
        CloudSyncDisplayState.Conflict => new("danger", "同步存在冲突，需要处理"),
        CloudSyncDisplayState.Error => new("danger", "同步失败"),
        CloudSyncDisplayState.AuthenticationRequired => new("danger", "同步需要重新登录"),
        CloudSyncDisplayState.Offline => new("neutral", "当前离线"),
        CloudSyncDisplayState.LocalOnly => new("neutral", "云同步未启用"),
        CloudSyncDisplayState.SignedOut => new("neutral", "尚未登录云账户"),
        _ => new("neutral", "同步状态暂时无法读取")
    };
}