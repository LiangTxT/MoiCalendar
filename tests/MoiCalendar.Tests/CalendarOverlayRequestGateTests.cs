using MoiCalendar.App.Components;

namespace MoiCalendar.Tests;

public sealed class CalendarOverlayRequestGateTests
{
    [Fact]
    public async Task DelayedClick_ClosingBeforeDelayCompletesDoesNotDispatch()
    {
        var gate = new CalendarOverlayRequestGate();
        var delay = new TaskCompletionSource();
        var calls = 0;
        var pending = gate.DispatchAfterAsync(delay.Task, () => { calls++; return Task.CompletedTask; });
        gate.Invalidate();
        delay.SetResult();
        await pending;
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DelayedClicks_OnlyLatestDispatchesEvenAcrossTwoViews()
    {
        var gate = new CalendarOverlayRequestGate();
        var firstDelay = new TaskCompletionSource();
        var secondDelay = new TaskCompletionSource();
        var calls = new List<string>();
        var first = gate.DispatchAfterAsync(firstDelay.Task, () => { calls.Add("月"); return Task.CompletedTask; });
        var second = gate.DispatchAfterAsync(secondDelay.Task, () => { calls.Add("周"); return Task.CompletedTask; });
        firstDelay.SetResult();
        await first;
        secondDelay.SetResult();
        await second;
        Assert.Equal(new[] { "周" }, calls);
    }

    [Fact]
    public async Task DelayedClick_UnchangedRequestDispatchesOnce()
    {
        var gate = new CalendarOverlayRequestGate();
        var calls = 0;
        await gate.DispatchAfterAsync(Task.CompletedTask, () => { calls++; return Task.CompletedTask; });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DelayedClick_DoubleClickCancellationDoesNotDispatch()
    {
        var gate = new CalendarOverlayRequestGate();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.DispatchAfterAsync(
            Task.FromCanceled(new CancellationToken(true)),
            () => { calls++; return Task.CompletedTask; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LatestClickWins_WhenOlderReadFinishesLast()
    {
        var gate = new CalendarOverlayRequestGate();
        var firstRead = new TaskCompletionSource<string>();
        var secondRead = new TaskCompletionSource<string>();
        var shown = new List<string>();
        var first = LoadAsync(firstRead.Task);
        var second = LoadAsync(secondRead.Task);

        secondRead.SetResult("新选择");
        await second;
        firstRead.SetResult("旧选择");
        await first;

        Assert.Equal(new[] { "新选择" }, shown);

        async Task LoadAsync(Task<string> read)
        {
            var request = gate.BeginRequest();
            var result = await read;
            if (gate.IsCurrent(request)) shown.Add(result);
        }
    }

    [Fact]
    public async Task OpenThenCloseOverlay_InvalidatesPendingReadEvenWhenKindReturnsToNone()
    {
        var gate = new CalendarOverlayRequestGate();
        var read = new TaskCompletionSource<bool>();
        var request = gate.BeginRequest();
        var completion = CompleteAsync();
        gate.Invalidate(); // 打开搜索。
        gate.Invalidate(); // 关闭搜索，回到 None。
        read.SetResult(true);

        Assert.False(await completion);

        async Task<bool> CompleteAsync()
        {
            await read.Task;
            return gate.IsCurrent(request);
        }
    }

    [Fact]
    public void EscapeBeforeDetailsAppear_InvalidatesPendingRequest()
    {
        var gate = new CalendarOverlayRequestGate();
        var request = gate.BeginRequest();
        gate.Invalidate();
        Assert.False(gate.IsCurrent(request));
    }

    [Fact]
    public void FocusAwaitMustRecheckRequest_AndNextRequestCanStillComplete()
    {
        var gate = new CalendarOverlayRequestGate();
        var request = gate.BeginRequest();
        Assert.True(gate.IsCurrent(request));
        gate.Invalidate(); // JS 焦点记录等待期间发生弹层切换或页面卸载。
        Assert.False(gate.IsCurrent(request));
        Assert.True(gate.IsCurrent(gate.BeginRequest()));
    }
}
