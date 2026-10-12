using MoiCalendar.App;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class CloudSyncFailureHelpTests
{
    [Theory]
    [InlineData(CloudSyncFailureReason.RestoreProtection, "备份恢复", "settings#sync-protection-heading")]
    [InlineData(CloudSyncFailureReason.AccountMismatch, "原账户", "settings#cloud-account-heading")]
    [InlineData(CloudSyncFailureReason.LocalAccountBinding, "不要清除", "sync-status#diagnostics-heading")]
    public void ReminderFailure_HasSafeSpecificHelp(CloudSyncFailureReason reason, string text, string link)
    {
        var result = new CloudSyncResult(CloudSyncOutcome.Failed, CloudSyncStatus.Error, 0, 0, 0,
            Message: "Bearer secret-must-not-leak", FailureReason: reason);
        var error = new ReminderSyncRequiredException(result);
        Assert.Contains(text, error.Message);
        Assert.DoesNotContain("secret-must-not-leak", error.Message);
        Assert.Equal(reason, error.FailureReason);
        Assert.Equal(link, CloudSyncFailureHelp.Link(reason));
    }

    [Theory]
    [InlineData(CloudSyncStatus.Conflict, "同步冲突")]
    [InlineData(CloudSyncStatus.AuthenticationRequired, "重新登录")]
    [InlineData(CloudSyncStatus.Offline, "联网")]
    [InlineData(CloudSyncStatus.RetryScheduled, "等待重试")]
    [InlineData(CloudSyncStatus.Error, "同步诊断")]
    public void UnknownFailures_DoNotSurfaceProviderMessages(CloudSyncStatus status, string text)
    {
        var error = new ReminderSyncRequiredException(new CloudSyncResult(
            CloudSyncOutcome.Failed, status, 0, 0, 0, Message: "Bearer secret-must-not-leak"));
        Assert.Contains(text, error.Message);
        Assert.DoesNotContain("secret-must-not-leak", error.Message);
    }
}
