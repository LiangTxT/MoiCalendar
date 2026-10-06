using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoiCalendar.App.Components;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class SyncSettingsRenderingTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SectionsAndReads_FollowPresentationFlags_WithoutStartingSync(bool cloud, bool backup)
    {
        var cloudService = new CloudStatusStub();
        var backupService = new BackupStub();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<ICloudSyncStatusService>(cloudService)
            .AddSingleton<ISyncDiagnosticsService>(backupService)
            .AddSingleton<ISyncService>(backupService)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SyncSettings>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(SyncSettings.ShowCloudManagement)] = cloud,
                    [nameof(SyncSettings.ShowBackupSync)] = backup
                }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
        Assert.Equal(cloud, html.Contains("同步管理"));
        Assert.Equal(cloud, html.Contains("Realtime 连接"));
        Assert.Equal(backup, html.Contains("可选备份提供商同步"));
        Assert.Equal(cloud ? 1 : 0, cloudService.StatusReads);
        Assert.Equal(cloud ? 1 : 0, cloudService.ConflictReads);
        Assert.Equal(backup ? 1 : 0, backupService.StatusReads);
        Assert.DoesNotContain("正在读取", html);
    }

    private sealed class CloudStatusStub : ICloudSyncStatusService
    {
        public int StatusReads { get; private set; }
        public int ConflictReads { get; private set; }
        public Task<CloudSyncSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusReads++;
            return Task.FromResult(new CloudSyncSnapshot(CloudSyncDisplayState.LocalOnly, 0, 0,
                null, null, 0, RealtimeConnectionState.Disabled, false));
        }
        public Task<IReadOnlyList<CloudSyncConflictDetails>> GetConflictsAsync(CancellationToken cancellationToken = default)
        {
            ConflictReads++;
            return Task.FromResult<IReadOnlyList<CloudSyncConflictDetails>>([]);
        }
        public Task<CloudSyncSnapshot> SynchronizeNowAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("渲染不得启动云同步");
        public Task<CloudSyncSnapshot> ResolveConflictAsync(Guid mutationId, CloudConflictResolution resolution,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("渲染不得修改冲突");
    }

    private sealed class BackupStub : ISyncService, ISyncDiagnosticsService
    {
        public int StatusReads { get; private set; }
        public Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusReads++;
            return Task.FromResult(new SyncStatus
            {
                ActiveProvider = "OneDrive", IsSyncing = false,
                PendingOperationCount = 0, FailedOperationCount = 0
            });
        }
        public Task<SyncResult> PushAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SyncResult> PullAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SyncResult> RetryFailedAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<SyncLogEntry>> GetLogEntriesAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task ClearLogAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
