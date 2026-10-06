namespace MoiCalendar.Core;

public enum CalendarInteractionStatus
{
    Committed,
    SelectionReady,
    RecurrenceScopeRequired,
    NoChange,
    Rejected,
    Failed
}

public abstract record CalendarInteractionIntent;

public interface ICalendarEventInteractionIntent
{
    Guid EventId { get; }

    DateTimeOffset OriginalStartUtc { get; }

    DateTimeOffset OriginalEndUtc { get; }

    bool IsReadOnly { get; }
}

public sealed record MoveEventIntent(
    Guid EventId,
    DateTimeOffset OriginalStartUtc,
    DateTimeOffset OriginalEndUtc,
    DateOnly? SourceDate,
    DateOnly? TargetDate,
    DateTime? NewStartLocal,
    DateTime? NewEndLocal,
    string InteractionTimeZoneId,
    bool IsReadOnly = false) : CalendarInteractionIntent, ICalendarEventInteractionIntent
{
    public static MoveEventIntent ToDate(
        Guid eventId,
        DateTimeOffset originalStartUtc,
        DateTimeOffset originalEndUtc,
        DateOnly sourceDate,
        DateOnly targetDate,
        string interactionTimeZoneId) =>
        new(
            eventId,
            originalStartUtc,
            originalEndUtc,
            sourceDate,
            targetDate,
            null,
            null,
            interactionTimeZoneId);

    public static MoveEventIntent ToTime(
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
            eventId,
            originalStartUtc,
            originalEndUtc,
            null,
            DateOnly.FromDateTime(snappedStart),
            snappedStart,
            snappedStart + duration,
            interactionTimeZoneId);
    }
}

public sealed record ResizeEventIntent(
    Guid EventId,
    DateTimeOffset OriginalStartUtc,
    DateTimeOffset OriginalEndUtc,
    DateTime NewEndLocal,
    string InteractionTimeZoneId,
    bool IsReadOnly = false,
    DateTime? NewStartLocal = null) : CalendarInteractionIntent, ICalendarEventInteractionIntent
{
    public ResizeEventIntent Snapped() => this with
    {
        NewEndLocal = CalendarInteractionGeometry.SnapLocalDateTime(NewEndLocal),
        NewStartLocal = NewStartLocal is { } start ? CalendarInteractionGeometry.SnapLocalDateTime(start) : null
    };
}

public sealed record SelectTimeRangeIntent(
    DateTime StartLocal,
    DateTime EndLocal,
    string InteractionTimeZoneId) : CalendarInteractionIntent
{
    public SelectTimeRangeIntent Snapped() => this with
    {
        StartLocal = CalendarInteractionGeometry.SnapLocalDateTime(StartLocal),
        EndLocal = CalendarInteractionGeometry.SnapLocalDateTime(EndLocal)
    };
}

public sealed record SelectDateRangeIntent(
    DateOnly StartDate,
    DateOnly EndDateExclusive) : CalendarInteractionIntent
{
    public static SelectDateRangeIntent SingleDay(DateOnly date) => new(date, date.AddDays(1));
}

public sealed record CalendarInteractionResult(
    CalendarInteractionStatus Status,
    CalendarInteractionIntent Intent,
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
        return Math.Clamp(snapped, 0, CalendarMetrics.MinutesPerDay);
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
        int snapMinutes = DefaultSnapMinutes,
        TimeGridMetrics? metrics = null)
    {
        if (timelineHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineHeight));
        }

        var relativePercentage = Math.Clamp(
            (pointerY - timelineTop) / timelineHeight * 100d,
            0,
            100);
        return SnapMinute(
            TimeGridCoordinateConverter.PercentageToMinute(relativePercentage, metrics),
            snapMinutes);
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
    /// <summary>保留原出现标识，生成拖动或拉伸后的单次编辑草稿；此处不写入数据。</summary>
    public CalendarEventDraft PrepareOccurrenceInteractionDraft(CalendarEvent series, CalendarInteractionIntent intent)
    {
        if (intent is not ICalendarEventInteractionIntent occurrence || occurrence.EventId != series.Id || occurrence.IsReadOnly ||
            occurrence.OriginalEndUtc <= occurrence.OriginalStartUtc)
            throw new ArgumentException("无法调整此次重复事件。");

        var instance = series with
        {
            StartUtc = occurrence.OriginalStartUtc,
            EndUtc = occurrence.OriginalEndUtc,
            RecurrenceRule = null
        };
        var draft = eventService.CreateDraft(instance);
        var invalid = ApplyInteraction(intent, instance, draft);
        if (invalid is not null) throw new ArgumentException(invalid.Message);
        return draft;
    }

    /// <summary>把单次出现的拖动差值映射到系列起点；只生成草稿，不写入数据。</summary>
    public CalendarEventDraft PrepareSeriesInteractionDraft(CalendarEvent series, CalendarInteractionIntent intent)
    {
        if (intent is not ICalendarEventInteractionIntent eventIntent || eventIntent.EventId != series.Id || eventIntent.IsReadOnly)
            throw new ArgumentException("无法调整此重复事件系列。");
        var zoneId = intent switch
        {
            MoveEventIntent move => move.InteractionTimeZoneId,
            ResizeEventIntent resize => resize.InteractionTimeZoneId,
            _ => throw new ArgumentException("不支持此重复事件操作。")
        };
        var zone = ResolveTimeZone(zoneId);
        var seriesStart = TimeZoneInfo.ConvertTime(series.StartUtc, zone).DateTime;
        var seriesEnd = TimeZoneInfo.ConvertTime(series.EndUtc, zone).DateTime;
        CalendarInteractionIntent mapped = intent switch
        {
            MoveEventIntent { SourceDate: not null, TargetDate: not null } => intent,
            MoveEventIntent { NewStartLocal: { } start, NewEndLocal: { } end } move => move with
            {
                NewStartLocal = seriesStart + (start - TimeZoneInfo.ConvertTime(move.OriginalStartUtc, zone).DateTime),
                NewEndLocal = seriesEnd + (end - TimeZoneInfo.ConvertTime(move.OriginalEndUtc, zone).DateTime)
            },
            ResizeEventIntent resize => resize.Snapped() with
            {
                NewStartLocal = resize.NewStartLocal is { } start
                    ? seriesStart + (CalendarInteractionGeometry.SnapLocalDateTime(start) - TimeZoneInfo.ConvertTime(resize.OriginalStartUtc, zone).DateTime)
                    : seriesStart,
                NewEndLocal = seriesEnd + (CalendarInteractionGeometry.SnapLocalDateTime(resize.NewEndLocal) - TimeZoneInfo.ConvertTime(resize.OriginalEndUtc, zone).DateTime)
            },
            _ => throw new ArgumentException("重复事件缺少目标日期或时间。")
        };
        var draft = eventService.CreateDraft(series);
        var originalDraftStart = draft.StartLocal;
        var invalid = ApplyInteraction(mapped, series, draft);
        if (invalid is not null) throw new ArgumentException(invalid.Message);
        var offset = (draft.StartLocal.Date - originalDraftStart.Date).Days;
        if (intent is MoveEventIntent && offset != 0 &&
            (draft.Recurrence.RepeatOption == CalendarEventRepeatOption.Weekly ||
             draft.Recurrence.RepeatOption == CalendarEventRepeatOption.Custom && draft.Recurrence.CustomFrequency == RecurrenceFrequency.Weekly))
        {
            var weekdays = draft.Recurrence.SelectedWeekdays.ToArray();
            foreach (var weekday in weekdays) draft.Recurrence.SetWeekdaySelected(weekday, false);
            foreach (var weekday in weekdays) draft.Recurrence.SetWeekdaySelected((DayOfWeek)(((int)weekday + offset % 7 + 7) % 7), true);
        }
        return draft;
    }

    public async Task<CalendarInteractionResult> ExecuteAsync(
        CalendarInteractionIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (intent is SelectTimeRangeIntent timeSelection)
        {
            return ValidateTimeSelection(timeSelection.Snapped());
        }

        if (intent is SelectDateRangeIntent dateSelection)
        {
            return ValidateDateSelection(dateSelection);
        }

        if (intent is not ICalendarEventInteractionIntent eventIntent || eventIntent.EventId == Guid.Empty)
        {
            return Rejected(intent, "交互缺少事件标识。");
        }

        if (eventIntent.IsReadOnly)
        {
            return Rejected(intent, "只读事件不能移动或调整时长。");
        }

        try
        {
            var eventId = eventIntent.EventId;
            var existing = await eventService.GetByIdAsync(eventId, cancellationToken);
            if (existing is null)
            {
                return Rejected(intent, "事件已不存在，请刷新日历后重试。");
            }

            if (!string.IsNullOrWhiteSpace(existing.RecurrenceRule))
            {
                return new CalendarInteractionResult(
                    CalendarInteractionStatus.RecurrenceScopeRequired,
                    intent,
                    "重复事件需要先在编辑器中确认修改范围，尚未更改日历。");
            }

            var originalStart = intent switch
            {
                MoveEventIntent move => move.OriginalStartUtc,
                ResizeEventIntent resize => resize.OriginalStartUtc,
                _ => default
            };
            var originalEnd = intent switch
            {
                MoveEventIntent move => move.OriginalEndUtc,
                ResizeEventIntent resize => resize.OriginalEndUtc,
                _ => default
            };
            if (originalStart != existing.StartUtc || originalEnd != existing.EndUtc)
            {
                return Rejected(intent, "事件已在其他位置更新，请刷新后重试。");
            }

            var draft = eventService.CreateDraft(existing);
            var validation = ApplyInteraction(intent, existing, draft);
            if (validation is not null)
            {
                return validation;
            }

            if (DraftMatchesEvent(draft, existing))
            {
                return new CalendarInteractionResult(
                    CalendarInteractionStatus.NoChange,
                    intent,
                    "事件时间没有变化。");
            }

            var updated = await eventService.UpdateAsync(eventId, draft, cancellationToken);
            return new CalendarInteractionResult(
                CalendarInteractionStatus.Committed,
                intent,
                intent is ResizeEventIntent ? "事件时长已更新。" : "事件时间已更新。",
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
                intent,
                $"未能保存更改：{exception.Message}");
        }
    }

    private static CalendarInteractionResult? ApplyInteraction(
        CalendarInteractionIntent intent,
        CalendarEvent existing,
        CalendarEventDraft draft)
    {
        switch (intent)
        {
            case MoveEventIntent { SourceDate: DateOnly sourceDate, TargetDate: DateOnly targetDate } moveToDate:
                var dayOffset = targetDate.DayNumber - sourceDate.DayNumber;
                if (dayOffset == 0)
                {
                    return null;
                }

                if (existing.IsAllDay)
                {
                    var durationDays = Math.Max(1, (draft.EndLocal.Date - draft.StartLocal.Date).Days);
                    draft.StartLocal = draft.StartLocal.Date.AddDays(dayOffset);
                    draft.EndLocal = draft.StartLocal.AddDays(durationDays);
                    draft.SetAllDay(true);
                    return null;
                }

                var moveZone = ResolveTimeZone(moveToDate.InteractionTimeZoneId);
                var displayedStart = TimeZoneInfo.ConvertTime(existing.StartUtc, moveZone).DateTime;
                var displayedEnd = TimeZoneInfo.ConvertTime(existing.EndUtc, moveZone).DateTime;
                var duration = displayedEnd - displayedStart;
                var movedStart = displayedStart.AddDays(dayOffset);
                return ApplyTimedRange(intent, existing, draft, movedStart, movedStart + duration, moveToDate.InteractionTimeZoneId);

            case MoveEventIntent { NewStartLocal: DateTime newStart, NewEndLocal: DateTime newEnd } moveToTime:
                return ApplyTimedRange(intent, existing, draft, newStart, newEnd, moveToTime.InteractionTimeZoneId);

            case MoveEventIntent:
                return Rejected(intent, "移动事件时缺少目标日期或时间。");

            case ResizeEventIntent resize:
                if (existing.IsAllDay)
                {
                    return Rejected(intent, "全天事件请使用事件编辑器调整日期。");
                }

                var snappedResize = resize.Snapped();
                var interactionZone = ResolveTimeZone(snappedResize.InteractionTimeZoneId);
                var startInInteractionZone = snappedResize.NewStartLocal ?? TimeZoneInfo.ConvertTime(existing.StartUtc, interactionZone).DateTime;
                return ApplyTimedRange(intent, existing, draft, startInInteractionZone, snappedResize.NewEndLocal, snappedResize.InteractionTimeZoneId);

            default:
                return Rejected(intent, "不支持此日历交互。");
        }
    }

    private static CalendarInteractionResult? ApplyTimedRange(
        CalendarInteractionIntent intent,
        CalendarEvent existing,
        CalendarEventDraft draft,
        DateTime newStart,
        DateTime newEnd,
        string interactionTimeZoneId)
    {
        if (newEnd - newStart < TimeSpan.FromMinutes(CalendarInteractionGeometry.MinimumTimedDurationMinutes))
        {
            return Rejected(intent, $"事件至少需要 {CalendarInteractionGeometry.MinimumTimedDurationMinutes} 分钟。");
        }

        var interactionZone = ResolveTimeZone(interactionTimeZoneId);
        var eventZone = ResolveTimeZone(existing.TimeZoneId);
        draft.StartLocal = ConvertBetweenTimeZones(newStart, interactionZone, eventZone);
        draft.EndLocal = ConvertBetweenTimeZones(newEnd, interactionZone, eventZone);
        return null;
    }

    private static CalendarInteractionResult ValidateTimeSelection(SelectTimeRangeIntent intent)
    {
        if (intent.EndLocal - intent.StartLocal < TimeSpan.FromMinutes(CalendarInteractionGeometry.MinimumTimedDurationMinutes))
        {
            return Rejected(intent, $"请选择至少 {CalendarInteractionGeometry.MinimumTimedDurationMinutes} 分钟。");
        }

        _ = ResolveTimeZone(intent.InteractionTimeZoneId);
        return new CalendarInteractionResult(
            CalendarInteractionStatus.SelectionReady,
            intent,
            "已选择时间范围，请填写事件信息。");
    }

    private static CalendarInteractionResult ValidateDateSelection(SelectDateRangeIntent intent) =>
        intent.EndDateExclusive <= intent.StartDate
            ? Rejected(intent, "日期范围的结束日期必须晚于开始日期。")
            : new CalendarInteractionResult(
                CalendarInteractionStatus.SelectionReady,
                intent,
                "已选择日期范围。");

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

    private static CalendarInteractionResult Rejected(CalendarInteractionIntent intent, string message) =>
        new(CalendarInteractionStatus.Rejected, intent, message);
}
