using MoiCalendar.App;
using MoiCalendar.App.Components;

namespace MoiCalendar.Tests;

public sealed class ReminderSetupTipTests
{
    [Theory]
    [InlineData(false, false, true, "提醒未开启，日程已保存。")]
    [InlineData(false, false, false, "提醒未开启，日程已保存。")]
    [InlineData(false, true, true, "本地提醒未开启，离线时不会提醒。")]
    [InlineData(true, false, true, "后台推送未开启，关闭应用后不会提醒。")]
    [InlineData(true, true, true, null)]
    [InlineData(true, false, false, null)]
    public void ExplainsMissingChannelWithoutChangingSettings(bool local, bool subscribed, bool cloud, string? expected)
    {
        var state = new BrowserReminderStatus("granted", true, subscribed, local);
        Assert.Equal(expected, ReminderSetupTip.MessageFor(state, cloud));
        Assert.Equal(local, state.LocalEnabled);
        Assert.Equal(subscribed, state.Subscribed);
    }

    [Fact]
    public void DeniedPermissionWithLocalFallbackDoesNotClaimAllRemindersAreDisabled()
    {
        var state = new BrowserReminderStatus("denied", true, false, true);
        Assert.Equal("后台推送未开启，关闭应用后不会提醒。", ReminderSetupTip.MessageFor(state, true));
    }
}
