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
    foreach (var scope in OneDriveGraphSettings.GetRequestedScopes())
    {
        options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
    }

    if (appConfiguration.MicrosoftAuthentication.RedirectPath is { } redirectPath)
    {
        options.AuthenticationPaths.LogInCallbackPath = redirectPath;
    }
});
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IRecurrenceExpansionService, RecurrenceExpansionService>();
builder.Services.AddScoped<IndexedDbConnection>();
builder.Services.AddScoped<IndexedDbLocalDataSafety>();
builder.Services.AddScoped<ILocalDataOperationLock>(sp =>
    sp.GetRequiredService<IndexedDbLocalDataSafety>());
builder.Services.AddScoped<IRestoreSyncGuard>(sp =>
    sp.GetRequiredService<IndexedDbLocalDataSafety>());
builder.Services.AddScoped<IEventRepository, IndexedDbEventRepository>();
builder.Services.AddScoped<ICalendarViewPreferenceStore, IndexedDbCalendarViewPreferenceStore>();
builder.Services.AddScoped<IBackupRestoreRepository, IndexedDbBackupRestoreRepository>();
builder.Services.AddScoped<IOperationRepository, IndexedDbOperationRepository>();
builder.Services.AddScoped<ISyncLogRepository, IndexedDbSyncLogRepository>();
builder.Services.AddScoped<ISyncStatusRepository, IndexedDbSyncStatusRepository>();
builder.Services.AddScoped<IDeviceService, IndexedDbDeviceService>();
builder.Services.AddScoped<ILocalEventChangeRepository, IndexedDbEventChangeRepository>();
builder.Services.AddScoped<CalendarEventService>();
builder.Services.AddScoped<ILocalBackupService>(sp => new LocalBackupService(
    sp.GetRequiredService<IEventRepository>(),
    sp.GetRequiredService<TimeProvider>(),
    typeof(App).Assembly.GetName().Version?.ToString()));
builder.Services.AddScoped<ILocalBackupRestoreService>(sp => new LocalBackupRestoreService(
    sp.GetRequiredService<IBackupRestoreRepository>(),
    sp.GetRequiredService<ILocalDataOperationLock>(),
    sp.GetRequiredService<IRestoreSyncGuard>()));
builder.Services.AddScoped<ICalendarExportService, CalendarExportService>();
builder.Services.AddScoped<ICalendarImportParser, CalendarImportParser>();
builder.Services.AddScoped<ICalendarImportService, CalendarImportService>();
builder.Services.AddScoped<IBrowserFileDownloadService, BrowserFileDownloadService>();
builder.Services.AddSingleton<ISyncProviderSelection, InMemorySyncProviderSelection>();
builder.Services.AddScoped<IOneDriveAccessTokenProvider, MsalOneDriveAccessTokenProvider>();
builder.Services.AddScoped(sp => new OneDriveSyncStorageProvider(
    new HttpClient { BaseAddress = new Uri(OneDriveGraphSettings.BaseUrl) },
    sp.GetRequiredService<IOneDriveAccessTokenProvider>()));
builder.Services.AddScoped<ISyncStorageProvider>(sp => new ActiveSyncStorageProvider(
    sp.GetRequiredService<ISyncProviderSelection>(),
    new Dictionary<SyncProviderType, ISyncStorageProvider>
    {
        [SyncProviderType.OneDrive] = sp.GetRequiredService<OneDriveSyncStorageProvider>()
    }));
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<ISyncService>(sp => sp.GetRequiredService<SyncService>());
builder.Services.AddScoped<ISyncDiagnosticsService>(sp => sp.GetRequiredService<SyncService>());
builder.Services.AddScoped<IOneDriveConnectionTester, OneDriveConnectionTester>();

await builder.Build().RunAsync();
