namespace MoiCalendar.Sync.OneDrive;

public interface IOneDriveAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public static class OneDriveGraphSettings
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0/";
    public const string AppFolderScope = "https://graph.microsoft.com/Files.ReadWrite.AppFolder";
    public const string TemporaryBootstrapScope = "https://graph.microsoft.com/Files.ReadWrite";

    public static string[] GetRequestedScopes() =>
        [AppFolderScope, TemporaryBootstrapScope];
}
