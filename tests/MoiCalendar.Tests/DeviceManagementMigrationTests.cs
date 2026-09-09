namespace MoiCalendar.Tests;

public sealed class DeviceManagementMigrationTests
{
    private static readonly string Migration = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "supabase",
        "migrations",
        "20260909000000_account_device_management.sql"));

    [Fact]
    public void DeviceFunctions_UseAuthenticatedOwnerAndKeepExistingRls()
    {
        Assert.Contains("v_owner_id uuid := auth.uid()", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("where device_row.owner_id = v_owner_id", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("devices_select_own", AllMigrations(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("using ((select auth.uid()) = owner_id)", AllMigrations(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revoke insert, update on table public.devices from authenticated", Migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateIdsAndRevokedDevices_AreRejectedAcrossEverySyncPath()
    {
        Assert.Contains("moicalendar_device_id_unavailable", Migration, StringComparison.Ordinal);
        Assert.Contains("moicalendar_device_revoked", Migration, StringComparison.Ordinal);
        Assert.Contains("calendar_event_mutations_require_active_device", Migration, StringComparison.Ordinal);
        Assert.Contains("moicalendar_pull_calendar_changes_for_device", Migration, StringComparison.Ordinal);
        Assert.Contains("moicalendar_acknowledge_device_sync", Migration, StringComparison.Ordinal);
        Assert.Contains("revoke execute on function public.moicalendar_pull_calendar_changes", Migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LastSuccessfulSync_IsUpdatedOnlyBySyncAcknowledgement()
    {
        Assert.Contains("set last_seen_at = transaction_timestamp()", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last_acknowledged_revision = greatest", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without allocating a synchronization revision", Migration, StringComparison.OrdinalIgnoreCase);
    }

    private static string AllMigrations() => string.Join(
        Environment.NewLine,
        Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "supabase", "migrations"), "*.sql")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MoiCalendar.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
