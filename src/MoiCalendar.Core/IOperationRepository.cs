namespace MoiCalendar.Core;

public interface IOperationRepository
{
    Task<SyncOperation> AddAsync(
        SyncOperation operation,
        CancellationToken cancellationToken = default);

    Task<SyncOperation?> GetByIdAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncOperation>> GetByStatusAsync(
        SyncOperationStatus status,
        CancellationToken cancellationToken = default);

    Task<SyncOperation> UpdateStatusAsync(
        Guid operationId,
        SyncOperationStatus status,
        CancellationToken cancellationToken = default);
}
