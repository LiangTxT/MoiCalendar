using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class InMemoryOperationRepository : IOperationRepository
{
    private readonly Dictionary<Guid, SyncOperation> operations = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<SyncOperation> AddAsync(
        SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!operations.TryAdd(operation.OperationId, operation))
            {
                throw new InvalidOperationException("相同 ID 的同步操作已经存在。");
            }

            return operation;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SyncOperation?> GetByIdAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return operations.GetValueOrDefault(operationId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncOperation>> GetByStatusAsync(
        SyncOperationStatus status,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return operations.Values
                .Where(operation => operation.Status == status)
                .OrderBy(operation => operation.TimestampUtc)
                .ThenBy(operation => operation.OperationId)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SyncOperation> UpdateStatusAsync(
        Guid operationId,
        SyncOperationStatus status,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!operations.TryGetValue(operationId, out var operation))
            {
                throw new KeyNotFoundException("找不到要更新的同步操作。");
            }

            var updated = operation with { Status = status };
            operations[operationId] = updated;
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }
}
