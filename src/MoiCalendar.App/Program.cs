using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MoiCalendar.App;
using MoiCalendar.App.Authentication;
using MoiCalendar.App.Configuration;
using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync;
using MoiCalendar.Sync.OneDrive;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var appConfiguration = MoiCalendarConfiguration.Load(
    builder.Configuration,
    new Uri(builder.HostEnvironment.BaseAddress));

builder.Services.AddSingleton(appConfiguration);
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMsalAuthentication(options =>
{
    options.ProviderOptions.Authentication.Authority =
        appConfiguration.MicrosoftAuthentication.Authority;
    options.ProviderOptions.Authentication.ClientId =
        appConfiguration.MicrosoftAuthentication.ClientId ?? string.Empty;
    options.ProviderOptions.DefaultAccessTokenScopes.Add(
        OneDriveGraphSettings.AppFolderScope);

    if (appConfiguration.MicrosoftAuthentication.RedirectPath is { } redirectPath)
    {
        options.AuthenticationPaths.LogInCallbackPath = redirectPath;
    }
});
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IEventRepository, IndexedDbEventRepository>();
builder.Services.AddScoped<CalendarEventService>();
builder.Services.AddSingleton<ISyncProviderSelection, InMemorySyncProviderSelection>();
builder.Services.AddScoped<IOneDriveAccessTokenProvider, MsalOneDriveAccessTokenProvider>();
builder.Services.AddScoped(sp => new OneDriveSyncStorageProvider(
    new HttpClient { BaseAddress = new Uri(OneDriveGraphSettings.BaseUrl) },
    sp.GetRequiredService<IOneDriveAccessTokenProvider>()));
builder.Services.AddScoped<IOneDriveConnectionTester, OneDriveConnectionTester>();

await builder.Build().RunAsync();
