using Microsoft.Extensions.DependencyInjection;
using MoiCalendar.Sync.Cloud;
using MoiCalendar.Sync.Supabase;

namespace MoiCalendar.Tests;

public sealed class CloudArchitectureTests
{
    [Fact]
    public void DisabledConfiguration_RegistersInertSupabaseSkeleton()
    {
        var services = new ServiceCollection();
        var options = new CloudBackendOptions { Enabled = false };

        services.AddSupabaseCloudBackend(options);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ICloudSyncStatusService) &&
            descriptor.ImplementationType == typeof(CloudSyncStatusService));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var backend = scope.ServiceProvider.GetRequiredService<ICloudBackend>();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        var realtime = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();
        var deviceTransport = scope.ServiceProvider.GetRequiredService<ICloudDeviceTransport>();

        Assert.Same(options, provider.GetRequiredService<CloudBackendOptions>());
        Assert.Equal("Supabase", backend.ProviderId);
        Assert.False(backend.IsEnabled);
        Assert.Equal(new CloudBackendCapabilities(false, false, false), backend.Capabilities);
        Assert.False(accounts.IsAvailable);
        Assert.False(realtime.IsAvailable);
        Assert.False(deviceTransport.IsAvailable);
    }

    [Theory]
    [InlineData("https://example.supabase.co")]
    [InlineData("https://cloud.example.com/supabase/")]
    [InlineData("http://localhost:54321")]
    public void ManagedAndSelfHostedUrls_AreConfigurationOnly(string baseUrl)
    {
        var options = new CloudBackendOptions
        {
            Enabled = true,
            BaseUrl = baseUrl,
            PublicKey = "sb_publishable_test"
        };

        var services = new ServiceCollection();
        services.AddSupabaseCloudBackend(options);

        using var provider = services.BuildServiceProvider();
        Assert.Same(options, provider.GetRequiredService<CloudBackendOptions>());
        using var scope = provider.CreateScope();
        var backend = scope.ServiceProvider.GetRequiredService<ICloudBackend>();
        Assert.Equal(new CloudBackendCapabilities(true, true, true), backend.Capabilities);
    }

    [Fact]
    public void EnabledConfiguration_RequiresUrlAndPublicKey()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddSupabaseCloudBackend(new CloudBackendOptions { Enabled = true }));

        Assert.Contains("BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://cloud.example.com")]
    [InlineData("http://cloud.example.com")]
    [InlineData("https://cloud.example.com?tenant=a")]
    public void BaseUrl_RejectsInvalidEndpoint(string baseUrl)
    {
        var options = new CloudBackendOptions
        {
            Enabled = true,
            BaseUrl = baseUrl,
            PublicKey = "sb_publishable_test"
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Theory]
    [InlineData("realtime/v1/websocket")]
    [InlineData("//realtime.example.com/socket/websocket")]
    [InlineData("/realtime\\v1\\websocket")]
    [InlineData("/realtime/v1/websocket?token=secret")]
    [InlineData("https://realtime.example.com/socket/websocket")]
    public void RealtimePath_RejectsNonPathOrQueryValues(string realtimePath)
    {
        var options = new SupabaseBackendOptions
        {
            RealtimePath = realtimePath
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void SelfHostedRealtimeEndpoint_IsConfigurationOnly()
    {
        var options = new SupabaseBackendOptions
        {
            RealtimePath = "/socket/websocket"
        };

        options.Validate();

        Assert.Equal("/socket/websocket", options.RealtimePath);
    }

    [Fact]
    public void PublicKey_RejectsSecretKey()
    {
        var options = new CloudBackendOptions
        {
            Enabled = true,
            BaseUrl = "https://example.supabase.co",
            PublicKey = "sb_secret_must-not-be-shipped"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddSupabaseCloudBackend(options));

        Assert.Contains("不能使用", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sb_secret_must-not-be-shipped")]
    [InlineData("service_role_must-not-be-shipped")]
    [InlineData("supabase_admin_must-not-be-shipped")]
    public void BrowserConfiguration_RejectsKnownPrivilegedKeyFormats(string key)
    {
        var options = new CloudBackendOptions
        {
            Enabled = true,
            BaseUrl = "https://backend.example.test",
            PublicKey = key
        };

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddSupabaseCloudBackend(options));
    }

    [Fact]
    public void CloudContracts_DoNotExposeSupabaseTypes()
    {
        var contractTypes = new[]
        {
            typeof(ICloudBackend),
            typeof(IAccountService),
            typeof(IRealtimeNotifier),
            typeof(IRealtimeSyncCoordinator),
            typeof(RealtimeWakeUpEventArgs),
            typeof(RealtimeConnectionStateChangedEventArgs),
            typeof(ICloudSyncService),
            typeof(ICloudSyncStatusService),
            typeof(CloudSyncSnapshot),
            typeof(CloudSyncConflictDetails),
            typeof(ICloudSyncTransport),
            typeof(ICloudDeviceService),
            typeof(ICloudDeviceTransport),
            typeof(ICloudDeviceSyncService),
            typeof(CloudDevice),
            typeof(CloudMutationRequest),
            typeof(CloudMutationResponse),
            typeof(CloudChangeBatch),
            typeof(CloudAccount),
            typeof(CloudBackendCapabilities)
        };

        Assert.All(
            contractTypes.SelectMany(type => type.GetMembers()),
            member => Assert.DoesNotContain(
                "Supabase",
                member.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
    }
}
