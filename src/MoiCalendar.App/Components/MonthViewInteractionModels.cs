using Microsoft.AspNetCore.Components.Web;
using MoiCalendar.Core;

namespace MoiCalendar.App.Components;

public sealed record MonthDragPreviewModel(
    DateOnly TargetDate,
    string Title,
    string TimeLabel);

public sealed record MonthEventPointerArgs(
    CalendarEventListItem CalendarEvent,
    DateOnly Date,
    PointerEventArgs PointerEvent);

public sealed record MonthDatePointerArgs(
    DateOnly Date,
    PointerEventArgs PointerEvent);

public sealed record MonthDateKeyboardArgs(
    DateOnly Date,
    KeyboardEventArgs KeyboardEvent);
