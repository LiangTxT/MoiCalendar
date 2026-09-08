using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MoiCalendar.Sync.Cloud;
using MoiCalendar.Sync.Supabase;

namespace MoiCalendar.Tests;

public sealed class BackendPortabilityTests
{
    [Fact]
    public void Provider_AcceptsArbitraryCompatibleGatewayHostAndPath()
    {
        var services = new ServiceCollection();
        services.AddSupabaseCloudBackend(
            new CloudBackendOptions
            {
                Enabled = true,
                BaseUrl = "https://calendar-backend.example.net/platform/",
                PublicKey = "sb_publishable_browser_client"
            },
            new SupabaseBackendOptions { RealtimePath = "/socket/websocket" });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var backend = scope.ServiceProvider.GetRequiredService<ICloudBackend>();

        Assert.True(backend.IsEnabled);
        Assert.Equal(new CloudBackendCapabilities(true, true, true), backend.Capabilities);
    }

    [Fact]
    public void GenericCloudOptions_DoNotContainSupabaseTransportDetails()
    {
        var propertyNames = typeof(CloudBackendOptions)
            .GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(new[] { "BaseUrl", "Enabled", "PublicKey" }, propertyNames);
        Assert.DoesNotContain(
            "Supabase",
            typeof(CloudBackendOptions).ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UiDomainAndProviderIndependentSync_DoNotReferenceSupabaseTypes()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = EnumerateSourceFiles(Path.Combine(root, "src", "MoiCalendar.Core"))
            .Concat(EnumerateSourceFiles(Path.Combine(root, "src", "MoiCalendar.App", "Pages")))
            .Concat(EnumerateSourceFiles(Path.Combine(root, "src", "MoiCalendar.App", "Components")))
            .Concat(EnumerateSourceFiles(Path.Combine(root, "src", "MoiCalendar.App", "Layout")))
            .Concat(EnumerateSourceFiles(Path.Combine(root, "src", "MoiCalendar.Sync", "Cloud")))
            .Append(Path.Combine(root, "src", "MoiCalendar.App", "App.razor"));

        Assert.All(sourceFiles, file => Assert.DoesNotContain(
            "Supabase",
            File.ReadAllText(file),
            StringComparison.OrdinalIgnoreCase));

        var syncDependencies = typeof(CloudSyncService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType.FullName ?? string.Empty);
        Assert.DoesNotContain(syncDependencies, name =>
            name.Contains("Supabase", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CommittedBrowserDefaults_PreserveLocalOnlyModeAndContainNoKey()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.App",
            "wwwroot",
            "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cloud = document.RootElement
            .GetProperty("MoiCalendar")
            .GetProperty("CloudBackend");

        Assert.False(cloud.GetProperty("Enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cloud.GetProperty("BaseUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, cloud.GetProperty("PublicKey").ValueKind);
    }

    [Fact]
    public void ReleaseBuild_ExcludesDeveloperSpecificCloudConfiguration()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.App",
            "MoiCalendar.App.csproj"));

        Assert.Contains("'$(Configuration)' == 'Release'", project, StringComparison.Ordinal);
        Assert.Contains("Content Remove=\"wwwroot\\appsettings.Local.json\"", project, StringComparison.Ordinal);
        Assert.Contains("Content Remove=\"wwwroot\\appsettings.Managed.json\"", project, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsContainCompletePortableSynchronizationFoundation()
    {
        var migrationDirectory = Path.Combine(FindRepositoryRoot(), "supabase", "migrations");
        var migrations = string.Join(
            '\n',
            Directory.GetFiles(migrationDirectory, "*.sql")
                .Order()
                .Select(File.ReadAllText));

        foreach (var table in new[]
        {
            "profiles", "sync_state", "devices", "calendars", "calendar_events",
            "calendar_event_mutations"
        })
        {
            Assert.Contains($"table public.{table}", migrations, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("enable row level security", migrations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create policy sync_state_select_own", migrations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moicalendar_next_revision", migrations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moicalendar_apply_calendar_mutation", migrations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moicalendar_pull_calendar_changes", migrations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supabase_realtime add table public.sync_state", migrations, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupabaseProviderSource_HasNoManagedHostnameOrProjectReferenceAssumption()
    {
        var providerDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.Sync",
            "Supabase");

        Assert.All(EnumerateSourceFiles(providerDirectory), file =>
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain(".supabase.co", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("project-ref", source, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".js", StringComparison.OrdinalIgnoreCase));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("无法定位 MoiCalendar 仓库根目录。");
    }
}
