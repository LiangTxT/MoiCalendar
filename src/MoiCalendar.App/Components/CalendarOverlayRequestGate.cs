namespace MoiCalendar.App.Components;

/// <summary>仅最新请求且弹层期间未被切换时，允许异步读取结果进入界面。</summary>
public sealed class CalendarOverlayRequestGate
{
    private long revision;

    public long BeginRequest() => ++revision;

    public void Invalidate() => ++revision;

    public bool IsCurrent(long request) => request == revision;

    public async Task DispatchAfterAsync(Task delay, Func<Task> dispatch)
    {
        var request = BeginRequest();
        await delay;
        if (IsCurrent(request))
        {
            await dispatch();
        }
    }
}
