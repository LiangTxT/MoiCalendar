using MoiCalendar.App;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class SyncIndicatorPresentationTests
{
    [Theory]
    [InlineData(CloudSyncDisplayState.PendingChanges, "warning")]
    [InlineData(CloudSyncDisplayState.RetryScheduled, "warning")]
    [InlineData(CloudSyncDisplayState.Conflict, "danger")]
    [InlineData(CloudSyncDisplayState.Error, "danger")]
    [InlineData(CloudSyncDisplayState.AuthenticationRequired, "danger")]
    [InlineData(CloudSyncDisplayState.Syncing, "active")]
    [InlineData(CloudSyncDisplayState.Offline, "neutral")]
    [InlineData(CloudSyncDisplayState.LocalOnly, "neutral")]
    [InlineData(CloudSyncDisplayState.SignedOut, "neutral")]
    public void PreviousSuccess_DoesNotHideCurrentProblem(
        CloudSyncDisplayState state, string cssClass)
    {
        var presentation = SyncIndicatorPresentation.From(state, DateTimeOffset.UtcNow);
        Assert.Equal(cssClass, presentation.CssClass);
        Assert.NotEqual("同步成功", presentation.Label);
    }

    [Fact]
    public void Success_RequiresConfirmedSuccessfulSync()
    {
        Assert.Equal("neutral", SyncIndicatorPresentation.From(CloudSyncDisplayState.Synced, null).CssClass);
        Assert.Equal("success", SyncIndicatorPresentation.From(CloudSyncDisplayState.Synced, DateTimeOffset.UtcNow).CssClass);
    }

    [Fact]
    public void UnreadableStatus_IsNeutral() =>
        Assert.Equal("neutral", SyncIndicatorPresentation.From(null, null).CssClass);
}