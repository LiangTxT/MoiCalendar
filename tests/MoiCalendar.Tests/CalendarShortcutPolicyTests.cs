using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarShortcutPolicyTests
{
    [Theory]
    [InlineData("n", CalendarShortcutAction.NewEvent)]
    [InlineData("T", CalendarShortcutAction.Today)]
    [InlineData("m", CalendarShortcutAction.MonthView)]
    [InlineData("w", CalendarShortcutAction.WeekView)]
    [InlineData("d", CalendarShortcutAction.DayView)]
    [InlineData("a", CalendarShortcutAction.AgendaView)]
    [InlineData("ArrowLeft", CalendarShortcutAction.PreviousPeriod)]
    [InlineData("ArrowRight", CalendarShortcutAction.NextPeriod)]
    [InlineData("Escape", CalendarShortcutAction.CloseOverlay)]
    [InlineData("Enter", CalendarShortcutAction.OpenSelected)]
    [InlineData("Delete", CalendarShortcutAction.DeleteSelected)]
    public void Resolve_MapsSupportedCalendarKeys(string key, CalendarShortcutAction expected)
    {
        Assert.Equal(expected, CalendarShortcutPolicy.Resolve(key, false, false, false, false));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("textarea")]
    [InlineData("select")]
    [InlineData("contenteditable")]
    public void Resolve_IgnoresShortcutsInsideTextEntryControls(string _)
    {
        Assert.Null(CalendarShortcutPolicy.Resolve("n", true, false, false, false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Resolve_DoesNotOverrideModifiedBrowserShortcuts(bool control, bool alt, bool meta)
    {
        Assert.Null(CalendarShortcutPolicy.Resolve("n", false, control, alt, meta));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Resolve_MapsPlatformSearchShortcut(bool control, bool meta)
    {
        Assert.Equal(
            CalendarShortcutAction.Search,
            CalendarShortcutPolicy.Resolve("k", false, control, false, meta));
    }

    [Fact]
    public void Resolve_DoesNotOpenSearchWhileTyping() =>
        Assert.Null(CalendarShortcutPolicy.Resolve("k", true, true, false, false));
}
