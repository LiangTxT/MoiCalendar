using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class InMemorySyncLogRepository(int retentionLimit = 200) : ISyncLogRepository
{
    private readonly List<SyncLogEntry> entries = [];
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task AddAsync(SyncLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (retentionLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionLimit));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            entries.Add(SyncLogSanitizer.Sanitize(entry));
            entries.Sort((left, right) => right.TimestampUtc.CompareTo(left.TimestampUtc));
            if (entries.Count > retentionLimit)
            {
                entries.RemoveRange(retentionLimit, entries.Count - retentionLimit);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncLogEntry>> GetRecentAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return entries.ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            entries.Clear();
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class InMemorySyncStatusRepository : ISyncStatusRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SyncStatusState state = new();

    public async Task<SyncStatusState> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        SyncStatusState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await gate.WaitAsync(cancellationToken);
        try
        {
            this.state = state;
        }
        finally
        {
            gate.Release();
        }
    }
}
