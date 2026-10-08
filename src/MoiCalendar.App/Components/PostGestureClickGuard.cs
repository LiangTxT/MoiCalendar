namespace MoiCalendar.App.Components;

/// <summary>只拦截拖动松手产生的短时合成点击，不能吞掉之后的正常点击。</summary>
public sealed class PostGestureClickGuard
{
    private DateTimeOffset? expiresAt;

    public void Suppress(DateTimeOffset now) => expiresAt = now.AddMilliseconds(500);

    public bool Consume(DateTimeOffset now)
    {
        var suppressed = expiresAt is { } deadline && now < deadline;
        expiresAt = null;
        return suppressed;
    }
}
