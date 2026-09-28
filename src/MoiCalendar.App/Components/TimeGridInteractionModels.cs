using Microsoft.AspNetCore.Components.Web;
using MoiCalendar.Core;

namespace MoiCalendar.App.Components;

public sealed record TimedEventPointerArgs(
    CalendarWeekTimedEvent CalendarEvent,
    DateOnly Date,
    PointerEventArgs PointerEvent);
