namespace MoiCalendar.Sync.OneDrive;

public interface IOneDriveConnectionTester
{
    Task<OneDriveConnectionTestResult> TestAsync(CancellationToken cancellationToken = default);
}

public sealed record OneDriveConnectionTestResult(bool IsSuccess, string Message);

public sealed class OneDriveConnectionTester(OneDriveSyncStorageProvider storageProvider)
    : IOneDriveConnectionTester
{
    public const string TestFileName = "hello.json";
    public const string TestContent = "{\"message\":\"Hello from MoiCalendar\"}";

    public async Task<OneDriveConnectionTestResult> TestAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await storageProvider.TestConnectionAsync(cancellationToken))
            {
                return new(false, "无法访问 OneDrive 应用文件夹。");
            }

            await storageProvider.UploadTextAsync(
                TestFileName,
                TestContent,
                cancellationToken: cancellationToken);

            var downloaded = await storageProvider.DownloadTextAsync(
                TestFileName,
                cancellationToken);

            if (downloaded is null ||
                !string.Equals(downloaded.Content, TestContent, StringComparison.Ordinal))
            {
                return new(false, "已写入 hello.json，但读回的内容不一致。");
            }

            return new(true, "连接成功：已在 OneDrive 应用文件夹中写入并验证 hello.json。");
        }
        catch (Exception exception) when (exception is SyncStorageException or OperationCanceledException)
        {
            return new(
                false,
                exception is OperationCanceledException
                    ? "连接测试已取消。"
                    : $"连接失败：{exception.Message}");
        }
    }
}
