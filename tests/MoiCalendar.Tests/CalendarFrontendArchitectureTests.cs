using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarFrontendArchitectureTests
{
    [Fact]
    public void MonthDateDoubleClick_OpensDayInsteadOfCreatingEvent()
    {
        var root = FindRepositoryRoot();
        var cell = File.ReadAllText(Path.Combine(root, "src", "MoiCalendar.App", "Components", "MonthDayCell.razor"));
        var home = File.ReadAllText(Path.Combine(root, "src", "MoiCalendar.App", "Pages", "Home.razor"));
        Assert.Contains("@ondblclick=\"() => OpenDay.InvokeAsync(Day.Date.Date)\"", cell);
        Assert.DoesNotContain("CreateEvent", cell);
        Assert.Contains("OpenDay=\"OpenMonthDay\"", home);
        Assert.Contains("ShowDisplayMode(CalendarViewMode.Day);", home);
    }

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
        var sources = new[] { "calendarUi.js", "calendarInteraction.js", "calendarOverlay.js" }
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
    public void V4DesignSystem_DefinesTheConstrainedSemanticTokens()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.App",
            "wwwroot",
            "css",
            "v4.css"));

        foreach (var token in new[]
        {
            "--v4-canvas",
            "--v4-surface",
            "--v4-elevated",
            "--v4-ink",
            "--v4-line",
            "--v4-primary",
            "--v4-signal",
            "--space-1",
            "--space-7",
            "--radius-event",
            "--radius-control",
            "--radius-overlay",
            "--motion-fast",
            "--motion-major"
        })
        {
            Assert.Contains(token, css, StringComparison.Ordinal);
        }

        Assert.Contains("prefers-reduced-motion", css, StringComparison.Ordinal);
        Assert.DoesNotContain("linear-gradient", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backdrop-filter", css, StringComparison.OrdinalIgnoreCase);
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
    public void TimeGrid_UsesBoundTimeZoneAndSharedScrollbarWidth()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(
            root, "src", "MoiCalendar.App", "Pages", "Home.razor"));
        var css = File.ReadAllText(Path.Combine(
            root, "src", "MoiCalendar.App", "wwwroot", "css", "v4.css"));

        Assert.Equal(2, home.Split("InteractionTimeZoneId=\"@displayTimeZoneId\"").Length - 1);
        Assert.DoesNotContain("InteractionTimeZoneId=\"displayTimeZoneId\"", home, StringComparison.Ordinal);
        Assert.Contains("padding-right: var(--time-grid-scrollbar-width, 0px)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void MonthView_UsesCachedFivePanelContinuousLocalStream()
    {
        var root = FindRepositoryRoot();
        var component = File.ReadAllText(Path.Combine(
            root, "src", "MoiCalendar.App", "Components", "ContinuousMonthView.razor"));
        var script = File.ReadAllText(Path.Combine(
            root, "src", "MoiCalendar.App", "wwwroot", "calendarUi.js"));
        var program = File.ReadAllText(Path.Combine(
            root, "src", "MoiCalendar.App", "Program.cs"));
        var home = File.ReadAllText(Path.Combine(
            root, "src", "MoiCalendar.App", "Pages", "Home.razor"));

        Assert.Contains("PreviousLayout", component, StringComparison.Ordinal);
        Assert.Contains("NextLayout", component, StringComparison.Ordinal);
        Assert.Contains("PreviousPreviousLayout", component, StringComparison.Ordinal);
        Assert.Contains("NextNextLayout", component, StringComparison.Ordinal);
        Assert.Contains("NavigationVersion", component, StringComparison.Ordinal);
        Assert.Contains("initializeMonthStream", script, StringComparison.Ordinal);
        Assert.Contains("ChangeVisibleMonth", script, StringComparison.Ordinal);
        Assert.Contains("SetDisplayedMonth", script, StringComparison.Ordinal);
        Assert.Contains("panels.length !== 5", script, StringComparison.Ordinal);
        Assert.Contains("panelTop(currentPanels[3])", script, StringComparison.Ordinal);
        Assert.Contains("panelTop(currentPanels[1])", script, StringComparison.Ordinal);
        Assert.Contains("top >= nextBoundary - 2", script, StringComparison.Ordinal);
        Assert.Contains("top <= previousBoundary + 2", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ignoreUntil", script, StringComparison.Ordinal);
        Assert.DoesNotContain("distances.indexOf", script, StringComparison.Ordinal);
        Assert.Contains("monthEventCache", home, StringComparison.Ordinal);
        Assert.Contains("monthStreamNavigationVersion++", home, StringComparison.Ordinal);
        Assert.Contains("showLoading: showLoading && !seamless", home, StringComparison.Ordinal);
        Assert.Contains("NavigateToMonth=\"month => ShowMonthAsync(month, showLoading: false)\"", home, StringComparison.Ordinal);
        Assert.Contains("<MonthNavigationRail", component, StringComparison.Ordinal);
        Assert.Contains("ChineseCalendarObservanceProvider", program, StringComparison.Ordinal);
    }

    [Fact]
    public void CalendarOverlays_UseOneHostForEscapeOutsideClickAndFocusTrap()
    {
        var root = FindRepositoryRoot();
        var componentDirectory = Path.Combine(root, "src", "MoiCalendar.App", "Components");
        var host = File.ReadAllText(Path.Combine(componentDirectory, "CalendarOverlayHost.razor"));
        var overlayScript = File.ReadAllText(Path.Combine(root, "src", "MoiCalendar.App", "wwwroot", "calendarOverlay.js"));
        var home = File.ReadAllText(Path.Combine(root, "src", "MoiCalendar.App", "Pages", "Home.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src", "MoiCalendar.App", "wwwroot", "css", "v4.css"));

        Assert.Contains("@onclick=\"Close\"", host, StringComparison.Ordinal);
        Assert.Contains("args.Key == \"Escape\"", host, StringComparison.Ordinal);
        Assert.Contains("moicalendarOverlay.activate", host, StringComparison.Ordinal);
        Assert.Contains("event.key !== \"Tab\"", overlayScript, StringComparison.Ordinal);
        Assert.Contains("[autofocus], [data-overlay-initial-focus]", overlayScript, StringComparison.Ordinal);
        Assert.Contains("private CalendarOverlayState overlayState", home, StringComparison.Ordinal);
        Assert.DoesNotContain("private CalendarEventDraft? quickCreateDraft;", home, StringComparison.Ordinal);
        Assert.DoesNotContain("private CalendarEvent? selectedEvent;", home, StringComparison.Ordinal);
        Assert.DoesNotContain("private DateOnly? overflowDate;", home, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 720px)", css, StringComparison.Ordinal);
        Assert.Contains(".recurrence-scope-popover", css, StringComparison.Ordinal);
    }

    [Fact]
    public void V4Frontend_HasOneStylesheetAndOneHighFrequencyPointerPipeline()
    {
        var root = FindRepositoryRoot();
        var wwwroot = Path.Combine(root, "src", "MoiCalendar.App", "wwwroot");
        var index = File.ReadAllText(Path.Combine(wwwroot, "index.html"));
        var interaction = File.ReadAllText(Path.Combine(wwwroot, "calendarInteraction.js"));
        var css = File.ReadAllText(Path.Combine(wwwroot, "css", "v4.css"));
        var timeGrid = File.ReadAllText(Path.Combine(
            root, "src", "MoiCalendar.App", "Components", "CalendarTimeGrid.razor"));

        Assert.Contains("css/v4.css", index, StringComparison.Ordinal);
        Assert.DoesNotContain("css/app.css", index, StringComparison.Ordinal);
        Assert.DoesNotContain("css/typography.css", index, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(wwwroot, "css", "app.css")));
        Assert.False(File.Exists(Path.Combine(wwwroot, "css", "typography.css")));
        Assert.Contains("requestAnimationFrame", interaction, StringComparison.Ordinal);
        Assert.Contains("beginTimeGridInteraction", interaction, StringComparison.Ordinal);
        Assert.Contains("if (!session.active)", interaction, StringComparison.Ordinal);
        Assert.Contains("session.active = true;", interaction, StringComparison.Ordinal);
        Assert.True(
            interaction.IndexOf("session.active = true;", StringComparison.Ordinal) <
            interaction.IndexOf("surface.setPointerCapture", StringComparison.Ordinal));
        Assert.DoesNotContain("bounds.top + scroll.scrollTop", interaction, StringComparison.Ordinal);
        Assert.Contains("config.pointerStartMinute - config.eventStartMinute", interaction, StringComparison.Ordinal);
        Assert.Contains("session.preview = preview.preview", interaction, StringComparison.Ordinal);
        Assert.Contains("targetDay.append(session.preview)", interaction, StringComparison.Ordinal);
        Assert.Contains("--event-top", interaction, StringComparison.Ordinal);
        Assert.Contains("--event-height", interaction, StringComparison.Ordinal);
        Assert.Contains("session.preview?.remove()", interaction, StringComparison.Ordinal);
        Assert.Contains(".calendar-interaction-preview", css, StringComparison.Ordinal);
        Assert.DoesNotContain("HandlePointerMoveAsync", timeGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("CommitPointerInteractionAsync", timeGrid, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserViewPreference_AllowsEveryExposedCalendarView()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.Storage",
            "wwwroot",
            "indexedDbEventRepository.js"));

        Assert.Contains("[\"Month\", \"Week\", \"Day\", \"Agenda\"]", source, StringComparison.Ordinal);
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
        Assert.Contains("time-grid-line", timeGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("当天暂无事件", timeGrid, StringComparison.Ordinal);
        Assert.Contains("!IsDayView && !VisibleDays.Any", timeGrid, StringComparison.Ordinal);
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

    [Fact]
    public void MonthView_PutsMonthLabelsInDayOneCellsAndUsesSplitToolbarTypography()
    {
        var root = FindRepositoryRoot();
        var components = Path.Combine(root, "src", "MoiCalendar.App", "Components");
        var monthPanel = File.ReadAllText(Path.Combine(components, "MonthPanel.razor"));
        var dayCell = File.ReadAllText(Path.Combine(components, "MonthDayCell.razor"));
        var toolbar = File.ReadAllText(Path.Combine(components, "CalendarToolbar.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src", "MoiCalendar.App", "wwwroot", "css", "v4.css"));

        Assert.DoesNotContain("month-panel-title", monthPanel, StringComparison.Ordinal);
        Assert.Contains("Day.Date.Date.Day == 1", dayCell, StringComparison.Ordinal);
        Assert.Contains("month-marker", dayCell, StringComparison.Ordinal);
        Assert.Contains("period-number", toolbar, StringComparison.Ordinal);
        Assert.Contains("period-unit", toolbar, StringComparison.Ordinal);
        Assert.Contains("Bahnschrift SemiCondensed", css, StringComparison.Ordinal);
        Assert.Contains(".month-period-title .period-unit", css, StringComparison.Ordinal);
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
