using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using MoiCalendar.Sync;
using MoiCalendar.Sync.OneDrive;

namespace MoiCalendar.App.Authentication;

public sealed class MsalOneDriveAccessTokenProvider(IAccessTokenProvider accessTokenProvider)
    : IOneDriveAccessTokenProvider
{
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await accessTokenProvider.RequestAccessToken(
            new AccessTokenRequestOptions
            {
                Scopes = OneDriveGraphSettings.GetRequestedScopes()
            });

        cancellationToken.ThrowIfCancellationRequested();

        if (result.TryGetToken(out var token))
        {
            return token.Value;
        }

        throw new SyncStorageException("无法获取 OneDrive 访问令牌，请重新登录并同意权限。");
    }
}
