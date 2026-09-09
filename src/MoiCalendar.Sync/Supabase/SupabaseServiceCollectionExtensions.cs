using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Supabase;

public static class SupabaseServiceCollectionExtensions
{
    public static IServiceCollection AddSupabaseCloudBackend(
        this IServiceCollection services,
        CloudBackendOptions options,
        SupabaseBackendOptions? supabaseOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        supabaseOptions ??= new SupabaseBackendOptions();
        options.Validate();
        supabaseOptions.Validate();
        SupabasePublicKeyGuard.Validate(options.PublicKey);

        services.AddSingleton(options);
        services.AddSingleton(supabaseOptions);
        services.AddScoped<ICloudBackend, SupabaseCloudBackend>();
        if (options.Enabled)
        {
            var authBaseUri = BuildAuthBaseUri(options.BaseUrl!);
            var restBaseUri = BuildRestBaseUri(options.BaseUrl!);
            services.AddScoped<ISupabaseSessionStore, SupabaseBrowserSessionStore>();
            services.AddScoped<SupabaseAccountService>(serviceProvider => new SupabaseAccountService(
                new HttpClient { BaseAddress = authBaseUri },
                options.PublicKey!,
                serviceProvider.GetRequiredService<ISupabaseSessionStore>(),
                serviceProvider.GetService<TimeProvider>()));
            services.AddScoped<ISupabaseAccessTokenProvider>(serviceProvider =>
                serviceProvider.GetRequiredService<SupabaseAccountService>());
            services.AddScoped<SupabaseRealtimeNotifier>(serviceProvider =>
                new SupabaseRealtimeNotifier(
                    serviceProvider.GetRequiredService<IJSRuntime>(),
                    options,
                    supabaseOptions,
                    serviceProvider.GetRequiredService<ISupabaseAccessTokenProvider>()));
            services.AddScoped<IRealtimeNotifier>(serviceProvider =>
                serviceProvider.GetRequiredService<SupabaseRealtimeNotifier>());
            services.AddScoped<IAccountService>(serviceProvider =>
                new DiagnosticsAwareAccountService(
                    new RealtimeAwareAccountService(
                        serviceProvider.GetRequiredService<SupabaseAccountService>(),
                        serviceProvider.GetRequiredService<IRealtimeNotifier>()),
                    serviceProvider.GetService<IOperationalDiagnosticsSink>() ??
                        DisabledOperationalDiagnosticsSink.Instance));
            services.AddScoped<ICloudSyncTransport>(serviceProvider => new SupabaseCloudSyncTransport(
                new HttpClient { BaseAddress = restBaseUri },
                options.PublicKey!,
                serviceProvider.GetRequiredService<ISupabaseAccessTokenProvider>()));
            services.AddScoped<ICloudDeviceTransport>(serviceProvider => new SupabaseCloudDeviceTransport(
                new HttpClient { BaseAddress = restBaseUri },
                options.PublicKey!,
                serviceProvider.GetRequiredService<ISupabaseAccessTokenProvider>()));
            services.AddScoped<ICloudAccountDataTransport>(serviceProvider => new SupabaseAccountDataTransport(
                new HttpClient { BaseAddress = BuildServiceBaseUri(options.BaseUrl!, string.Empty) },
                options.PublicKey!,
                serviceProvider.GetRequiredService<ISupabaseAccessTokenProvider>()));
        }
        else
        {
            services.AddScoped<IAccountService, DisabledAccountService>();
            services.AddScoped<IRealtimeNotifier, DisabledRealtimeNotifier>();
            services.AddScoped<ICloudSyncTransport, DisabledCloudSyncTransport>();
            services.AddScoped<ICloudDeviceTransport, DisabledCloudDeviceTransport>();
            services.AddScoped<ICloudAccountDataTransport, DisabledCloudAccountDataTransport>();
        }
        services.AddScoped<IAccountDataService, AccountDataService>();
        services.AddScoped<CloudDeviceService>();
        services.AddScoped<ICloudDeviceService>(serviceProvider =>
            serviceProvider.GetRequiredService<CloudDeviceService>());
        services.AddScoped<ICloudDeviceSyncService>(serviceProvider =>
            serviceProvider.GetRequiredService<CloudDeviceService>());
        services.AddSingleton<ICloudSyncRetryPolicy, CloudSyncRetryPolicy>();
        services.AddSingleton<ICloudSyncDelay, SystemCloudSyncDelay>();
        services.AddScoped<ICloudSyncService, CloudSyncService>();
        services.AddScoped<ICloudSyncStatusService, CloudSyncStatusService>();
        services.AddScoped<IRealtimeSyncCoordinator, RealtimeSyncCoordinator>();
        return services;
    }

    private static Uri BuildAuthBaseUri(string configuredBaseUrl)
        => BuildServiceBaseUri(configuredBaseUrl, "auth/v1/");

    private static Uri BuildRestBaseUri(string configuredBaseUrl)
        => BuildServiceBaseUri(configuredBaseUrl, "rest/v1/");

    private static Uri BuildServiceBaseUri(string configuredBaseUrl, string servicePath)
    {
        var baseUrl = configuredBaseUrl.Trim();
        var normalizedBaseUrl = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : $"{baseUrl}/";
        return new Uri(new Uri(normalizedBaseUrl, UriKind.Absolute), servicePath);
    }
}
