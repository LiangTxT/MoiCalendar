using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.App;

public sealed record BrowserReminderStatus(string Permission, bool PushSupported, bool Subscribed, bool LocalEnabled = false);

public sealed class BrowserReminderService(IJSRuntime js, IReminderStateStore state, ICloudReminderTransport cloud,
    IAccountService accounts, IDeviceService devices, ICloudSyncService sync) : IAsyncDisposable
{
    private IJSObjectReference? module;
    public bool CloudAvailable => cloud.IsAvailable;
    private async Task<IJSObjectReference> ModuleAsync() => module ??= await js.InvokeAsync<IJSObjectReference>("import", "./reminders.js");
    public async Task<BrowserReminderStatus> StatusAsync()
    {
        var status = await (await ModuleAsync()).InvokeAsync<BrowserReminderStatus>("status");
        return status with { LocalEnabled = await state.GetEnabledAsync(), Subscribed = status.Subscribed && await state.GetPushOwnerAsync() is not null };
    }
    public async Task<string> EnableLocalAsync()
    {
        var permission = await (await ModuleAsync()).InvokeAsync<string>("requestPermission");
        await state.SetEnabledAsync(true);
        return permission;
    }
    public Task DisableLocalAsync() => state.SetEnabledAsync(false);
    public async Task EnableBackgroundAsync()
    {
        if (!cloud.IsAvailable) throw new InvalidOperationException("云后端未配置，本地提醒仍可使用。");
        if (await (await ModuleAsync()).InvokeAsync<string>("requestPermission") != "granted")
            throw new InvalidOperationException("系统通知权限未授予。请在浏览器设置中允许通知；iPad 请先添加到主屏幕。");
        var account = await accounts.GetCurrentAccountAsync() ?? throw new InvalidOperationException("请先登录云账户。");
        var key = await cloud.GetPublicKeyAsync();
        // Ensures active device registration and pushes reminder fields before subscribing.
        var result = await sync.SynchronizeAsync();
        if (!result.IsSuccess) throw new InvalidOperationException("请先解决同步问题，再开启后台提醒；本地提醒仍可使用。");
        var subscription = await (await ModuleAsync()).InvokeAsync<string>("subscribe", key);
        try
        {
            await cloud.RegisterAsync((await devices.GetDeviceIdentityAsync()).DeviceId, subscription);
            await state.SetPushOwnerAsync(account.Id);
        }
        catch
        {
            await state.SetPushOwnerAsync(null);
            await (await ModuleAsync()).InvokeVoidAsync("unsubscribe");
            throw;
        }
    }
    public async Task DisableBackgroundAsync()
    {
        await state.SetPushOwnerAsync(null);
        await (await ModuleAsync()).InvokeVoidAsync("unsubscribe");
        if (cloud.IsAvailable && await accounts.GetCurrentAccountAsync() is not null)
            await cloud.UnregisterAsync((await devices.GetDeviceIdentityAsync()).DeviceId);
    }
    public async Task<bool> ShowAsync(CalendarReminder reminder) =>
        await (await ModuleAsync()).InvokeAsync<bool>("show", reminder);
    public async ValueTask DisposeAsync()
    {
        if (module is not null) await module.DisposeAsync();
    }
}
