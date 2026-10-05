using MoiCalendar.App.Components;

namespace MoiCalendar.Tests;

public sealed class CalendarOverlayClickPolicyTests
{
    [Theory]
    [InlineData(CalendarOverlayKind.None, true)]
    [InlineData(CalendarOverlayKind.EventDetails, true)]
    [InlineData(CalendarOverlayKind.Search, false)]
    [InlineData(CalendarOverlayKind.QuickCreate, false)]
    [InlineData(CalendarOverlayKind.FullEditor, false)]
    [InlineData(CalendarOverlayKind.MoreEvents, false)]
    [InlineData(CalendarOverlayKind.RecurrenceScope, false)]
    public void CalendarClick_OnlyOpensDetailsOutsideTaskOverlays(CalendarOverlayKind kind, bool expected)
        => Assert.Equal(expected, CalendarOverlayTransitions.CanOpenEventFromCalendar(kind));
}
