namespace MoiCalendar.Sync;

public interface ISyncStorageProvider
{
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    Task EnsureDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    Task<SyncFileMetadata> UploadTextAsync(
        string path,
        string content,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default);

    Task<SyncTextFile?> DownloadTextAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncFileMetadata>> ListFilesAsync(
        string directoryPath,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string path,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default);
}

public sealed record SyncFileMetadata(
    string Path,
    string? VersionToken,
    long? Size,
    DateTimeOffset? LastModifiedUtc);

public sealed record SyncTextFile(
    string Path,
    string Content,
    string? VersionToken,
    DateTimeOffset? LastModifiedUtc);
