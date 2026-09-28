using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarFrontendArchitectureTests
{
    [Fact]
    public void ProjectReferences_PreserveCoreAndInfrastructureBoundaries()
    {
        var root = FindRepositoryRoot();
        var coreProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.Core",
            "MoiCalendar.Core.csproj"));
        var storageProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.Storage",
            "MoiCalendar.Storage.csproj"));
        var syncProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.Sync",
            "MoiCalendar.Sync.csproj"));

        Assert.DoesNotContain("ProjectReference", coreProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MoiCalendar.Core.csproj", storageProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MoiCalendar.App.csproj", storageProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MoiCalendar.Sync.csproj", storageProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MoiCalendar.Core.csproj", syncProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MoiCalendar.App.csproj", syncProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MoiCalendar.Storage.csproj", syncProject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalendarEvent_DoesNotContainPresentationGeometry()
    {
        var forbiddenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PixelTop",
            "PixelLeft",
            "WidthPercent",
            "ColumnIndex",
            "TopPercentage",
            "HeightPercentage",
            "Column",
            "ColumnCount"
        };

        var domainProperties = typeof(CalendarEvent)
            .GetProperties()
            .Select(property => property.Name);

        Assert.DoesNotContain(domainProperties, forbiddenProperties.Contains);
    }

    [Fact]
    public void CalendarUi_DoesNotReferencePersistenceImplementations()
    {
        var root = FindRepositoryRoot();
        var files = Directory
            .EnumerateFiles(
                Path.Combine(root, "src", "MoiCalendar.App", "Components"),
                "*.razor",
                SearchOption.AllDirectories)
            .Append(Path.Combine(root, "src", "MoiCalendar.App", "Pages", "Home.razor"));

        var forbiddenTerms = new[]
        {
            "MoiCalendar.Storage",
            "IndexedDbConnection",
            "IndexedDbEventRepository",
            "ISyncOutboxRepository",
            "IndexedDbSyncOutboxRepository",
            "Supabase",
            "IRealtimeNotifier",
            "OneDriveSyncStorageProvider",
            "WebDavSyncStorageProvider"
        };

        Assert.All(files, file =>
        {
            var source = File.ReadAllText(file);
            Assert.All(forbiddenTerms, term => Assert.DoesNotContain(
                term,
                source,
                StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void CalendarJavascript_DoesNotOwnPersistenceOrNetworkRules()
    {
        var wwwroot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.App",
            "wwwroot");
        var sources = new[] { "calendarUi.js", "calendarInteraction.js" }
            .Select(name => File.ReadAllText(Path.Combine(wwwroot, name)));

        foreach (var forbiddenTerm in new[]
        {
            "indexedDB",
            "localStorage",
            "XMLHttpRequest",
            "fetch(",
            "Supabase"
        })
        {
            Assert.All(sources, source => Assert.DoesNotContain(
                    forbiddenTerm,
                    source,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void InteractionEngine_ExposesExplicitIntentTypes()
    {
        var intentType = typeof(CalendarInteractionIntent);
        var intentTypes = new[]
        {
            typeof(MoveEventIntent),
            typeof(ResizeEventIntent),
            typeof(SelectDateRangeIntent),
            typeof(SelectTimeRangeIntent)
        };

        Assert.All(intentTypes, type => Assert.True(type.IsAssignableTo(intentType)));
    }

    [Fact]
    public void PointerComponents_EmitIntentsWithoutCallingApplicationServices()
    {
        var components = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.App",
            "Components");
        var sources = new[] { "CalendarTimeGrid.razor", "MonthView.razor", "MonthDayCell.razor" }
            .Select(name => File.ReadAllText(Path.Combine(components, name)));

        Assert.All(sources, source =>
        {
            Assert.DoesNotContain("CalendarEventService", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CalendarInteractionService", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecuteAsync", source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MonthComponents_ConsumePrecomputedLayoutInsteadOfReimplementingIt()
    {
        var componentDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.App",
            "Components");
        var files = new[]
        {
            "MonthView.razor",
            "MonthWeekRow.razor",
            "MonthDayCell.razor",
            "MonthDayEvents.razor",
            "MonthEventSegment.razor",
            "MoreEventsTrigger.razor"
        };

        Assert.All(files, name =>
        {
            var source = File.ReadAllText(Path.Combine(componentDirectory, name));
            Assert.DoesNotContain("MonthLayoutEngine", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MultiDayLayoutEngine", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CalendarPresentationLayout", source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void WeekAndDayViews_UseTheSameTimeGridFoundation()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.App",
            "Pages",
            "Home.razor"));
        var timeGrid = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.App",
            "Components",
            "CalendarTimeGrid.razor"));

        Assert.Equal(2, home.Split("<MoiCalendar.App.Components.CalendarTimeGrid", StringSplitOptions.None).Length - 1);
        Assert.Contains("VisibleDate=\"selectedDate\"", home, StringComparison.Ordinal);
        Assert.Contains("<TimeGridHeader", timeGrid, StringComparison.Ordinal);
        Assert.Contains("<AllDayPanel", timeGrid, StringComparison.Ordinal);
        Assert.Contains("<TimedEventBlock", timeGrid, StringComparison.Ordinal);
        Assert.Contains("<CurrentTimeIndicator", timeGrid, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentTimeIndicator_DoesNotLoadOrPersistCalendarData()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.App",
            "Components",
            "CurrentTimeIndicator.razor"));

        foreach (var forbiddenTerm in new[]
        {
            "CalendarEventService",
            "Repository",
            "IndexedDB",
            "SyncOutbox",
            "IJSRuntime"
        })
        {
            Assert.DoesNotContain(forbiddenTerm, source, StringComparison.OrdinalIgnoreCase);
        }
    }

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
