namespace MoiCalendar.Tests;

public sealed class AccountDataLifecycleMigrationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string Migration = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "supabase",
        "migrations",
        "20260910000000_account_data_lifecycle.sql"));
    private static readonly string EdgeFunction = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "supabase",
        "functions",
        "delete-account",
        "index.ts"));

    [Fact]
    public void Export_IsAuthenticatedOwnerOnlyAndExcludesNonPortableTables()
    {
        Assert.Contains("v_owner_id uuid := auth.uid()", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("calendar_row.owner_id = v_owner_id", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event_row.owner_id = v_owner_id", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grant execute on function public.moicalendar_export_account_data()", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("to authenticated", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("from public.devices", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("from public.calendar_event_mutations", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", Migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", Migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwnedTables_CascadeFromVerifiedAuthUser()
    {
        var allMigrations = string.Join(
            Environment.NewLine,
            Directory.GetFiles(Path.Combine(RepositoryRoot, "supabase", "migrations"), "*.sql")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));

        Assert.Contains("references auth.users (id) on delete cascade", allMigrations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("owner_id uuid primary key references public.profiles (id) on delete cascade", allMigrations, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            CountOccurrences(allMigrations, "owner_id uuid not null references public.profiles (id) on delete cascade") >= 4,
            "所有直接归属账户的云表都应通过 profiles 级联删除。");
    }

    [Fact]
    public void EdgeDeletion_DerivesTargetOnlyFromVerifiedJwtAndRequiresRecentAuthentication()
    {
        Assert.Contains("auth/v1/user", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("verifiedUser.id", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("recentAuthenticationWindowMilliseconds", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("SUPABASE_SERVICE_ROLE_KEY", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("deleteResponse.status !== 404", EdgeFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("request.json", EdgeFunction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user_id", EdgeFunction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("console.log", EdgeFunction, StringComparison.OrdinalIgnoreCase);

        var config = File.ReadAllText(Path.Combine(RepositoryRoot, "supabase", "config.toml"));
        Assert.Contains("[functions.delete-account]", config, StringComparison.Ordinal);
        Assert.Contains("verify_jwt = true", config, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

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
