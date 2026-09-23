namespace MoiCalendar.Core;

public enum CalendarInteractionType
{
    MoveEvent,
    ResizeEvent,
    CreateFromSelection,
    ChangeDate
}

public enum CalendarInteractionStatus
{
    Committed,
    SelectionReady,
    RecurrenceScopeRequired,
    NoChange,
    Rejected,
    Failed
}

public sealed record CalendarInteractionRequest(
    CalendarInteractionType Type,
    Guid? EventId,
    DateTimeOffset? OriginalStartUtc,
    DateTimeOffset? OriginalEndUtc,
    DateTime? NewStartLocal,
    DateTime? NewEndLocal,
    DateOnly? TargetDate,
    string InteractionTimeZoneId)
{
    public static CalendarInteractionRequest MoveToDate(
        Guid eventId,
        DateTimeOffset originalStartUtc,
        DateTimeOffset originalEndUtc,
        DateOnly targetDate,
        string interactionTimeZoneId) =>
        new(
            CalendarInteractionType.ChangeDate,
            eventId,
            originalStartUtc,
            originalEndUtc,
            null,
            null,
            targetDate,
            interactionTimeZoneId);

    public static CalendarInteractionRequest MoveTimed(
        Guid eventId,
        DateTimeOffset originalStartUtc,
        DateTimeOffset originalEndUtc,
        DateTime newStartLocal,
        DateTime newEndLocal,
        string interactionTimeZoneId)
    {
        var duration = newEndLocal - newStartLocal;
        var snappedStart = CalendarInteractionGeometry.SnapLocalDateTime(newStartLocal);
        return new(
            CalendarInteractionType.MoveEvent,
            eventId,
            originalStartUtc,
            originalEndUtc,
            snappedStart,
            snappedStart + duration,
            DateOnly.FromDateTime(snappedStart),
            interactionTimeZoneId);
    }

    public static CalendarInteractionRequest Resize(
        Guid eventId,
        DateTimeOffset originalStartUtc,
        DateTimeOffset originalEndUtc,
        DateTime newEndLocal,
        string interactionTimeZoneId) =>
        new(
            CalendarInteractionType.ResizeEvent,
            eventId,
            originalStartUtc,
            originalEndUtc,
            null,
            CalendarInteractionGeometry.SnapLocalDateTime(newEndLocal),
            DateOnly.FromDateTime(CalendarInteractionGeometry.SnapLocalDateTime(newEndLocal)),
            interactionTimeZoneId);

    public static CalendarInteractionRequest CreateSelection(
        DateTime startLocal,
        DateTime endLocal,
        string interactionTimeZoneId) =>
        new(
            CalendarInteractionType.CreateFromSelection,
            null,
            null,
            null,
            CalendarInteractionGeometry.SnapLocalDateTime(startLocal),
            CalendarInteractionGeometry.SnapLocalDateTime(endLocal),
            DateOnly.FromDateTime(CalendarInteractionGeometry.SnapLocalDateTime(startLocal)),
            interactionTimeZoneId);
}

public sealed record CalendarInteractionResult(
    CalendarInteractionStatus Status,
    CalendarInteractionRequest Request,
    string Message,
    CalendarEvent? UpdatedEvent = null)
{
    public bool Succeeded => Status is CalendarInteractionStatus.Committed or CalendarInteractionStatus.SelectionReady;
}

public static class CalendarInteractionGeometry
{
    public const int DefaultSnapMinutes = 15;
    public const int MinimumTimedDurationMinutes = 15;
    public const double DefaultMovementThresholdPixels = 6;

    public static int SnapMinute(double minute, int snapMinutes = DefaultSnapMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapMinutes);
        var snapped = (int)Math.Round(minute / snapMinutes, MidpointRounding.AwayFromZero) * snapMinutes;
        return Math.Clamp(snapped, 0, 24 * 60);
    }

    public static DateTime SnapLocalDateTime(
        DateTime value,
        int snapMinutes = DefaultSnapMinutes)
    {
        var date = value.Date;
        var minute = SnapMinute(value.TimeOfDay.TotalMinutes, snapMinutes);
        return DateTime.SpecifyKind(date.AddMinutes(minute), DateTimeKind.Unspecified);
    }

    public static int MinuteFromPointer(
        double pointerY,
        double timelineTop,
        double timelineHeight,
        int snapMinutes = DefaultSnapMinutes)
    {
        if (timelineHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineHeight));
        }

        var relative = Math.Clamp((pointerY - timelineTop) / timelineHeight, 0, 1);
        return SnapMinute(relative * 24 * 60, snapMinutes);
    }

    public static int DateIndexFromPointer(
        double pointerX,
        double gridLeft,
        double gridWidth,
        int dateCount)
    {
        if (gridWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridWidth));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dateCount);
        var relative = Math.Clamp((pointerX - gridLeft) / gridWidth, 0, 0.999999999);
        return Math.Min(dateCount - 1, (int)(relative * dateCount));
    }

    public static int MonthDateIndexFromPointer(
        double pointerX,
        double pointerY,
        double gridLeft,
        double gridTop,
        double gridWidth,
        double gridHeight,
        int dateCount,
        int columnCount = 7)
    {
        if (gridWidth <= 0 || gridHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridWidth), "日历网格尺寸必须大于零。");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dateCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columnCount);
        var rowCount = (int)Math.Ceiling(dateCount / (double)columnCount);
        var column = DateIndexFromPointer(pointerX, gridLeft, gridWidth, columnCount);
        var relativeY = Math.Clamp((pointerY - gridTop) / gridHeight, 0, 0.999999999);
        var row = Math.Min(rowCount - 1, (int)(relativeY * rowCount));
        return Math.Min(dateCount - 1, row * columnCount + column);
    }

    public static bool HasExceededMovementThreshold(
        double startX,
        double startY,
        double currentX,
        double currentY,
        double thresholdPixels = DefaultMovementThresholdPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdPixels);
        var x = currentX - startX;
        var y = currentY - startY;
        return x * x + y * y >= thresholdPixels * thresholdPixels;
    }
}

public sealed class CalendarInteractionService(CalendarEventService eventService)
{
    public async Task<CalendarInteractionResult> ExecuteAsync(
        CalendarInteractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Type == CalendarInteractionType.CreateFromSelection)
        {
            return ValidateSelection(request);
        }

        if (request.EventId is not Guid eventId)
        {
            return Rejected(request, "交互缺少事件标识。");
        }

        try
        {
            var existing = await eventService.GetByIdAsync(eventId, cancellationToken);
            if (existing is null)
            {
                return Rejected(request, "事件已不存在，请刷新日历后重试。");
            }

            if (!string.IsNullOrWhiteSpace(existing.RecurrenceRule))
            {
                return new CalendarInteractionResult(
                    CalendarInteractionStatus.RecurrenceScopeRequired,
                    request,
                    "重复事件需要先在编辑器中确认修改范围，尚未更改日历。");
            }

            if (request.OriginalStartUtc != existing.StartUtc || request.OriginalEndUtc != existing.EndUtc)
            {
                return Rejected(request, "事件已在其他位置更新，请刷新后重试。");
            }

            var draft = eventService.CreateDraft(existing);
            var validation = ApplyInteraction(request, existing, draft);
            if (validation is not null)
            {
                return validation;
            }

            if (DraftMatchesEvent(draft, existing))
            {
                return new CalendarInteractionResult(
                    CalendarInteractionStatus.NoChange,
                    request,
                    "事件时间没有变化。");
            }

            var updated = await eventService.UpdateAsync(eventId, draft, cancellationToken);
            return new CalendarInteractionResult(
                CalendarInteractionStatus.Committed,
                request,
                request.Type == CalendarInteractionType.ResizeEvent ? "事件时长已更新。" : "事件时间已更新。",
                updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or EventRepositoryException or SyncOperationException)
        {
            return new CalendarInteractionResult(
                CalendarInteractionStatus.Failed,
                request,
                $"未能保存更改：{exception.Message}");
        }
    }

    private static CalendarInteractionResult? ApplyInteraction(
        CalendarInteractionRequest request,
        CalendarEvent existing,
        CalendarEventDraft draft)
    {
        switch (request.Type)
        {
            case CalendarInteractionType.ChangeDate:
                if (request.TargetDate is not DateOnly targetDate)
                {
                    return Rejected(request, "移动事件时缺少目标日期。");
                }

                if (existing.IsAllDay)
                {
                    var durationDays = Math.Max(1, (draft.EndLocal.Date - draft.StartLocal.Date).Days);
                    draft.StartLocal = targetDate.ToDateTime(TimeOnly.MinValue);
                    draft.EndLocal = draft.StartLocal.AddDays(durationDays);
                    draft.SetAllDay(true);
                    return null;
                }

                var moveZone = ResolveTimeZone(request.InteractionTimeZoneId);
                var displayedStart = TimeZoneInfo.ConvertTime(existing.StartUtc, moveZone).DateTime;
                var displayedEnd = TimeZoneInfo.ConvertTime(existing.EndUtc, moveZone).DateTime;
                var duration = displayedEnd - displayedStart;
                var movedStart = targetDate.ToDateTime(TimeOnly.FromDateTime(displayedStart));
                return ApplyTimedRange(request, existing, draft, movedStart, movedStart + duration);

            case CalendarInteractionType.MoveEvent:
                if (request.NewStartLocal is not DateTime newStart || request.NewEndLocal is not DateTime newEnd)
                {
                    return Rejected(request, "移动事件时缺少目标时间。");
                }

                return ApplyTimedRange(request, existing, draft, newStart, newEnd);

            case CalendarInteractionType.ResizeEvent:
                if (existing.IsAllDay)
                {
                    return Rejected(request, "全天事件请使用事件编辑器调整日期。");
                }

                if (request.NewEndLocal is not DateTime resizedEnd)
                {
                    return Rejected(request, "调整事件时缺少结束时间。");
                }

                var interactionZone = ResolveTimeZone(request.InteractionTimeZoneId);
                var startInInteractionZone = TimeZoneInfo.ConvertTime(existing.StartUtc, interactionZone).DateTime;
                return ApplyTimedRange(request, existing, draft, startInInteractionZone, resizedEnd);

            default:
                return Rejected(request, "不支持此日历交互。");
        }
    }

    private static CalendarInteractionResult? ApplyTimedRange(
        CalendarInteractionRequest request,
        CalendarEvent existing,
        CalendarEventDraft draft,
        DateTime newStart,
        DateTime newEnd)
    {
        if (newEnd - newStart < TimeSpan.FromMinutes(CalendarInteractionGeometry.MinimumTimedDurationMinutes))
        {
            return Rejected(request, $"事件至少需要 {CalendarInteractionGeometry.MinimumTimedDurationMinutes} 分钟。");
        }

        var interactionZone = ResolveTimeZone(request.InteractionTimeZoneId);
        var eventZone = ResolveTimeZone(existing.TimeZoneId);
        draft.StartLocal = ConvertBetweenTimeZones(newStart, interactionZone, eventZone);
        draft.EndLocal = ConvertBetweenTimeZones(newEnd, interactionZone, eventZone);
        return null;
    }

    private static CalendarInteractionResult ValidateSelection(CalendarInteractionRequest request)
    {
        if (request.NewStartLocal is not DateTime start || request.NewEndLocal is not DateTime end)
        {
            return Rejected(request, "选择中缺少开始或结束时间。");
        }

        if (end - start < TimeSpan.FromMinutes(CalendarInteractionGeometry.MinimumTimedDurationMinutes))
        {
            return Rejected(request, $"请选择至少 {CalendarInteractionGeometry.MinimumTimedDurationMinutes} 分钟。");
        }

        _ = ResolveTimeZone(request.InteractionTimeZoneId);
        return new CalendarInteractionResult(
            CalendarInteractionStatus.SelectionReady,
            request,
            "已选择时间范围，请填写事件信息。");
    }

    private static bool DraftMatchesEvent(CalendarEventDraft draft, CalendarEvent existing)
    {
        var eventZone = ResolveTimeZone(existing.TimeZoneId);
        var start = TimeZoneInfo.ConvertTime(existing.StartUtc, eventZone).DateTime;
        var end = TimeZoneInfo.ConvertTime(existing.EndUtc, eventZone).DateTime;
        return draft.StartLocal == start && draft.EndLocal == end && draft.IsAllDay == existing.IsAllDay;
    }

    private static DateTime ConvertBetweenTimeZones(
        DateTime value,
        TimeZoneInfo source,
        TimeZoneInfo destination)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (source.IsInvalidTime(unspecified))
        {
            throw new ArgumentException("所选时间处于夏令时跳过区间，请选择其他时间。");
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, source);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, destination), DateTimeKind.Unspecified);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("交互时区不能为空。", nameof(timeZoneId));
        }

        try
        {
            return timeZoneId == TimeZoneInfo.Local.Id
                ? TimeZoneInfo.Local
                : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("无法识别交互时区。", nameof(timeZoneId), exception);
        }
    }

    private static CalendarInteractionResult Rejected(CalendarInteractionRequest request, string message) =>
        new(CalendarInteractionStatus.Rejected, request, message);
}
