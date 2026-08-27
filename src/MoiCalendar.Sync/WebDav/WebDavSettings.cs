namespace MoiCalendar.Sync.WebDav;

public sealed class WebDavSettings
{
    public WebDavSettings(
        string baseUrl,
        string username,
        string password,
        string remotePath)
    {
        BaseUrl = baseUrl;
        Username = username;
        Password = password;
        RemotePath = remotePath;
    }

    public string BaseUrl { get; }

    public string Username { get; }

    public string Password { get; }

    public string RemotePath { get; }

    public override string ToString() =>
        $"WebDavSettings {{ BaseUrl = {BaseUrl}, Username = {Username}, Password = ***, RemotePath = {RemotePath} }}";
}
