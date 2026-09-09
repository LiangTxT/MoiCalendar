using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MoiCalendar.App;
using MoiCalendar.App.Authentication;
using MoiCalendar.App.Configuration;
using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync;
using MoiCalendar.Sync.Supabase;
using MoiCalendar.Sync.OneDrive;
using MoiCalendar.Sync.Diagnostics;
using MoiCalendar.Sync.Cloud;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var appConfiguration = MoiCalendarConfiguration.Load(
    builder.Configuration,
    new Uri(builder.HostEnvironment.BaseAddress));
var supabaseConfiguration = new SupabaseBackendOptions
{
    RealtimePath = builder.Configuration[
        "MoiCalendar:CloudBackend:Supabase:RealtimePath"] ?? "/realtime/v1/websocket"
};

builder.Services.AddSingleton(appConfiguration);
builder.Services.AddSingleton(appConfiguration.Diagnostics);
builder.Services.AddSupabaseCloudBackend(appConfiguration.CloudBackend, supabaseConfiguration);
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

    var redirectPath = appConfiguration.MicrosoftAuthentication.RedirectPath ??
        MoiCalendarConfiguration.DefaultMicrosoftLoginCallbackPath;
    options.AuthenticationPaths.LogInCallbackPath = redirectPath;
    options.ProviderOptions.Authentication.RedirectUri =
        appConfiguration.MicrosoftLoginCallbackUrl.AbsoluteUri;
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
builder.Services.AddScoped<IOperationalDiagnosticRepository, IndexedDbOperationalDiagnosticRepository>();
builder.Services.AddScoped<ILocalStorageHealthService, IndexedDbLocalStorageHealthService>();
builder.Services.AddScoped<IClientPlatformProvider, BrowserClientPlatformProvider>();
builder.Services.AddScoped<LocalOperationalDiagnosticsSink>(serviceProvider =>
    new LocalOperationalDiagnosticsSink(
        appConfiguration.Diagnostics,
        serviceProvider.GetRequiredService<IOperationalDiagnosticRepository>(),
        serviceProvider.GetRequiredService<IClientPlatformProvider>(),
        serviceProvider.GetRequiredService<TimeProvider>(),
        typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown"));
builder.Services.AddScoped<IOperationalDiagnosticsSink>(serviceProvider =>
    serviceProvider.GetRequiredService<LocalOperationalDiagnosticsSink>());
builder.Services.AddScoped<IDeviceService, IndexedDbDeviceService>();
builder.Services.AddScoped<IndexedDbSyncOutboxRepository>();
builder.Services.AddScoped<ISyncOutboxRepository>(sp =>
    sp.GetRequiredService<IndexedDbSyncOutboxRepository>());
builder.Services.AddScoped<ICloudConflictRepository>(sp =>
    sp.GetRequiredService<IndexedDbSyncOutboxRepository>());
builder.Services.AddScoped<ISyncStateRepository, IndexedDbSyncStateRepository>();
builder.Services.AddScoped<ICloudSyncBindingRepository, IndexedDbCloudSyncBindingRepository>();
builder.Services.AddScoped<ICloudChangeApplyRepository, IndexedDbCloudChangeApplyRepository>();
builder.Services.AddScoped<IAccountDeletionLocalRepository, IndexedDbAccountDeletionLocalRepository>();
builder.Services.AddScoped<ILocalEventChangeRepository, IndexedDbEventChangeRepository>();
builder.Services.AddScoped<IRemoteSyncApplyRepository, IndexedDbRemoteSyncApplyRepository>();
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
builder.Services.AddScoped<IDiagnosticReportService>(serviceProvider =>
    new DiagnosticReportService(
        appConfiguration.Diagnostics,
        serviceProvider.GetRequiredService<IOperationalDiagnosticRepository>(),
        serviceProvider.GetRequiredService<ILocalStorageHealthService>(),
        serviceProvider.GetRequiredService<IClientPlatformProvider>(),
        serviceProvider.GetRequiredService<ICloudBackend>(),
        serviceProvider.GetRequiredService<IAccountService>(),
        serviceProvider.GetRequiredService<ICloudSyncStatusService>(),
        serviceProvider.GetRequiredService<IRealtimeNotifier>(),
        serviceProvider.GetRequiredService<TimeProvider>(),
        typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown"));
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
