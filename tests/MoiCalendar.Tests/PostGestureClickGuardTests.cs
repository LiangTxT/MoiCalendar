using MoiCalendar.App.Components;

namespace MoiCalendar.Tests;

public sealed class PostGestureClickGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalClickIsNeverSuppressed()
    {
        Assert.False(new PostGestureClickGuard().Consume(Now));
    }

    [Fact]
    public void GestureSyntheticClickIsConsumedOnlyOnce()
    {
        var guard = new PostGestureClickGuard();
        guard.Suppress(Now);
        Assert.True(guard.Consume(Now.AddMilliseconds(10)));
        Assert.False(guard.Consume(Now.AddMilliseconds(20)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(60000)]
    public void MissingSyntheticClickDoesNotSwallowLaterTap(int elapsedMilliseconds)
    {
        var guard = new PostGestureClickGuard();
        guard.Suppress(Now);
        Assert.False(guard.Consume(Now.AddMilliseconds(elapsedMilliseconds)));
    }
}
