namespace MoiCalendar.Sync.Cloud;

public sealed class CloudSyncRetryPolicy : ICloudSyncRetryPolicy
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);
    private const double JitterRatio = 0.2;

    public int MaximumAttemptsPerOperation => 3;

    public TimeSpan GetDelay(int totalFailedAttemptCount)
    {
        if (totalFailedAttemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFailedAttemptCount));
        }

        var exponent = Math.Min(totalFailedAttemptCount - 1, 20);
        var withoutJitter = Math.Min(
            BaseDelay.TotalMilliseconds * Math.Pow(2, exponent),
            MaximumDelay.TotalMilliseconds);
        var jitter = 1 + ((Random.Shared.NextDouble() * 2 - 1) * JitterRatio);
        return TimeSpan.FromMilliseconds(Math.Clamp(
            withoutJitter * jitter,
            BaseDelay.TotalMilliseconds * (1 - JitterRatio),
            MaximumDelay.TotalMilliseconds));
    }
}

public sealed class SystemCloudSyncDelay : ICloudSyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, cancellationToken);
}
