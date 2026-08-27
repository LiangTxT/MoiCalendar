namespace MoiCalendar.Sync;

public sealed class ActiveSyncStorageProvider(
    ISyncProviderSelection providerSelection,
    IReadOnlyDictionary<SyncProviderType, ISyncStorageProvider> providers) : ISyncStorageProvider
{
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await (await GetActiveProviderAsync(cancellationToken)).TestConnectionAsync(cancellationToken);

    public async Task EnsureDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default) =>
        await (await GetActiveProviderAsync(cancellationToken)).EnsureDirectoryAsync(directoryPath, cancellationToken);

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        await (await GetActiveProviderAsync(cancellationToken)).ExistsAsync(path, cancellationToken);

    public async Task<SyncFileMetadata> UploadTextAsync(
        string path,
        string content,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default) =>
        await (await GetActiveProviderAsync(cancellationToken)).UploadTextAsync(
            path,
            content,
            expectedVersionToken,
            cancellationToken);

    public async Task<SyncTextFile?> DownloadTextAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        await (await GetActiveProviderAsync(cancellationToken)).DownloadTextAsync(path, cancellationToken);

    public async Task<IReadOnlyList<SyncFileMetadata>> ListFilesAsync(
        string directoryPath,
        CancellationToken cancellationToken = default) =>
        await (await GetActiveProviderAsync(cancellationToken)).ListFilesAsync(directoryPath, cancellationToken);

    public async Task<bool> DeleteAsync(
        string path,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default) =>
        await (await GetActiveProviderAsync(cancellationToken)).DeleteAsync(
            path,
            expectedVersionToken,
            cancellationToken);

    private async Task<ISyncStorageProvider> GetActiveProviderAsync(CancellationToken cancellationToken)
    {
        var configuration = await providerSelection.GetAsync(cancellationToken);
        if (configuration.ProviderType == SyncProviderType.None)
        {
            throw new SyncStorageException("尚未选择同步提供商，请先在设置中选择 OneDrive 或 WebDAV。");
        }

        if (!providers.TryGetValue(configuration.ProviderType, out var provider))
        {
            throw new SyncStorageException($"{configuration.ProviderType} 尚未完成连接配置。");
        }

        return provider;
    }
}
