using System.Text.Json;

namespace MoiCalendar.Core;

public sealed class CalendarEventService(
    IEventRepository repository,
    IDeviceService deviceService,
    ILocalEventChangeRepository localEventChanges,
    TimeProvider timeProvider)
{
    private const int MinutesPerDay = 24 * 60;
    private const int MinimumTimedEventDisplayMinutes = 30;
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web);

    public CalendarEventDraft CreateDraft(DateOnly date, string timeZoneId)
    {
        _ = ResolveTimeZone(timeZoneId);
        return CalendarEventDraft.ForDate(date, timeZoneId);
    }

    public CalendarEventDraft CreateDraft(CalendarEvent calendarEvent) =>
        CalendarEventDraft.FromEvent(calendarEvent, ResolveTimeZone(calendarEvent.TimeZoneId));

    public async Task<CalendarEvent> CreateAsync(
        CalendarEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        var values = ValidateAndConvert(draft);
        var now = timeProvider.GetUtcNow();
        var calendarEvent = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = values.Title,
            Description = values.Description,
            Location = values.Location,
            StartUtc = values.StartUtc,
            EndUtc = values.EndUtc,
            TimeZoneId = draft.TimeZoneId,
            IsAllDay = draft.IsAllDay,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var operation = await CreateOperationAsync(
            calendarEvent,
            SyncOperationType.Create,
            now,
            cancellationToken);
        return await localEventChanges.CreateEventAsync(calendarEvent, operation, cancellationToken);
    }

    public async Task<CalendarEvent> UpdateAsync(
        Guid id,
        CalendarEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("找不到要更新的日历事件。");
        var values = ValidateAndConvert(draft);
        var updated = existing with
        {
            Title = values.Title,
            Description = values.Description,
            Location = values.Location,
            StartUtc = values.StartUtc,
            EndUtc = values.EndUtc,
            TimeZoneId = draft.TimeZoneId,
            IsAllDay = draft.IsAllDay,
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };

        var operation = await CreateOperationAsync(
            updated,
            SyncOperationType.Update,
            updated.UpdatedAtUtc,
            cancellationToken);
        return await localEventChanges.UpdateEventAsync(updated, operation, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var deleted = existing with
        {
            DeletedAtUtc = now,
            UpdatedAtUtc = now
        };
        var operation = await CreateOperationAsync(
            deleted,
            SyncOperationType.Delete,
            now,
            cancellationToken);
        return await localEventChanges.DeleteEventAsync(deleted, operation, cancellationToken);
    }

    public Task<CalendarEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(id, cancellationToken);

    public async Task<CalendarMonthEventView> GetMonthViewAsync(
        CalendarMonthView monthView,
        string displayTimeZoneId,
        CancellationToken cancellationToken = default)
    {
        var firstDate = monthView.Dates[0].Date;
        var endDateExclusive = monthView.Dates[^1].Date.AddDays(1);
        var orderedGroups = await GetEventGroupsAsync(
            firstDate,
            endDateExclusive,
            displayTimeZoneId,
            cancellationToken);

        return new CalendarMonthEventView(orderedGroups);
    }

    public async Task<CalendarAgendaView> GetAgendaViewAsync(
        CalendarMonth month,
        string displayTimeZoneId,
        CancellationToken cancellationToken = default)
    {
        var firstDate = new DateOnly(month.Year, month.Month, 1);
        var endDateExclusive = firstDate.AddMonths(1);
        var orderedGroups = await GetEventGroupsAsync(
            firstDate,
            endDateExclusive,
            displayTimeZoneId,
            cancellationToken);
        var days = orderedGroups
            .Where(pair => pair.Value.Count > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => new CalendarAgendaDay(pair.Key, pair.Value))
            .ToArray();

        return new CalendarAgendaView(days);
    }

    public async Task<CalendarWeekEventView> GetWeekViewAsync(
        CalendarWeekView weekView,
        string displayTimeZoneId,
        CancellationToken cancellationToken = default)
    {
        var firstDate = weekView.Week.StartDate;
        var endDateExclusive = weekView.Week.EndDate.AddDays(1);
        var range = await GetEventsForDateRangeAsync(
            firstDate,
            endDateExclusive,
            displayTimeZoneId,
            cancellationToken);
        var allDayGroups = weekView.Dates.ToDictionary(
            date => date.Date,
            _ => new List<CalendarWeekAllDayEvent>());
        var timedGroups = weekView.Dates.ToDictionary(
            date => date.Date,
            _ => new List<CalendarWeekTimedEvent>());

        foreach (var calendarEvent in range.Events)
        {
            AddEventToWeek(
                calendarEvent,
                range.DisplayTimeZone,
                firstDate,
                endDateExclusive,
                allDayGroups,
                timedGroups);
        }

        var days = weekView.Dates
            .Select(date => new CalendarWeekDayEvents(
                date,
                allDayGroups[date.Date]
                    .OrderBy(calendarEvent => calendarEvent.Title, StringComparer.CurrentCulture)
                    .ToArray(),
                timedGroups[date.Date]
                    .OrderBy(calendarEvent => calendarEvent.StartMinute)
                    .ThenBy(calendarEvent => calendarEvent.Title, StringComparer.CurrentCulture)
                    .ToArray()))
            .ToArray();

        return new CalendarWeekEventView(days);
    }

    private async Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<CalendarEventListItem>>> GetEventGroupsAsync(
        DateOnly firstDate,
        DateOnly endDateExclusive,
        string displayTimeZoneId,
        CancellationToken cancellationToken)
    {
        var range = await GetEventsForDateRangeAsync(
            firstDate,
            endDateExclusive,
            displayTimeZoneId,
            cancellationToken);
        var groups = Enumerable.Range(0, endDateExclusive.DayNumber - firstDate.DayNumber)
            .Select(firstDate.AddDays)
            .ToDictionary(
                date => date,
                _ => new List<CalendarEventListItem>());

        foreach (var calendarEvent in range.Events)
        {
            AddEventToDates(calendarEvent, range.DisplayTimeZone, firstDate, endDateExclusive, groups);
        }

        var orderedGroups = groups.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CalendarEventListItem>)pair.Value
                .OrderBy(item => item.IsAllDay ? 0 : 1)
                .ThenBy(item => item.SortTime)
                .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                .ToArray());

        return orderedGroups;
    }

    private async Task<EventRangeResult> GetEventsForDateRangeAsync(
        DateOnly firstDate,
        DateOnly endDateExclusive,
        string displayTimeZoneId,
        CancellationToken cancellationToken)
    {
        var displayTimeZone = ResolveTimeZone(displayTimeZoneId);
        var rangeStartUtc = ConvertLocalToUtc(firstDate.ToDateTime(TimeOnly.MinValue), displayTimeZone);
        var rangeEndUtc = ConvertLocalToUtc(endDateExclusive.ToDateTime(TimeOnly.MinValue), displayTimeZone);
        var calendarEvents = await repository.GetByRangeAsync(rangeStartUtc, rangeEndUtc, cancellationToken);
        return new EventRangeResult(displayTimeZone, calendarEvents);
    }

    private static void AddEventToWeek(
        CalendarEvent calendarEvent,
        TimeZoneInfo displayTimeZone,
        DateOnly firstVisibleDate,
        DateOnly endVisibleDateExclusive,
        IDictionary<DateOnly, List<CalendarWeekAllDayEvent>> allDayGroups,
        IDictionary<DateOnly, List<CalendarWeekTimedEvent>> timedGroups)
    {
        var eventTimeZone = calendarEvent.IsAllDay
            ? ResolveTimeZone(calendarEvent.TimeZoneId)
            : displayTimeZone;
        var localStart = TimeZoneInfo.ConvertTime(calendarEvent.StartUtc, eventTimeZone);
        var localEnd = TimeZoneInfo.ConvertTime(calendarEvent.EndUtc, eventTimeZone);
        var eventFirstDate = DateOnly.FromDateTime(localStart.DateTime);
        var eventLastDate = DateOnly.FromDateTime(localEnd.AddTicks(-1).DateTime);
        var clippedFirstDate = eventFirstDate < firstVisibleDate ? firstVisibleDate : eventFirstDate;
        var lastVisibleDate = endVisibleDateExclusive.AddDays(-1);
        var clippedLastDate = eventLastDate > lastVisibleDate ? lastVisibleDate : eventLastDate;

        for (var date = clippedFirstDate; date <= clippedLastDate; date = date.AddDays(1))
        {
            if (calendarEvent.IsAllDay)
            {
                allDayGroups[date].Add(new CalendarWeekAllDayEvent(calendarEvent.Id, calendarEvent.Title));
                continue;
            }

            var startMinute = date == eventFirstDate
                ? GetMinuteOfDay(localStart.TimeOfDay, roundUp: false)
                : 0;
            var endMinute = date == eventLastDate && DateOnly.FromDateTime(localEnd.DateTime) == date
                ? GetMinuteOfDay(localEnd.TimeOfDay, roundUp: true)
                : MinutesPerDay;
            endMinute = Math.Clamp(endMinute, startMinute + 1, MinutesPerDay);
            var durationMinutes = endMinute - startMinute;
            var displayDurationMinutes = Math.Min(
                Math.Max(durationMinutes, MinimumTimedEventDisplayMinutes),
                MinutesPerDay - startMinute);

            timedGroups[date].Add(new CalendarWeekTimedEvent(
                calendarEvent.Id,
                calendarEvent.Title,
                $"{FormatMinute(startMinute)}–{FormatMinute(endMinute)}",
                startMinute * 100d / MinutesPerDay,
                displayDurationMinutes * 100d / MinutesPerDay,
                startMinute,
                durationMinutes));
        }
    }

    private static int GetMinuteOfDay(TimeSpan time, bool roundUp)
    {
        var minutes = time.TotalMinutes;
        return roundUp ? (int)Math.Ceiling(minutes) : (int)Math.Floor(minutes);
    }

    private static string FormatMinute(int minute)
    {
        if (minute >= MinutesPerDay)
        {
            return "24:00";
        }

        return $"{minute / 60:D2}:{minute % 60:D2}";
    }

    private static void AddEventToDates(
        CalendarEvent calendarEvent,
        TimeZoneInfo displayTimeZone,
        DateOnly firstGridDate,
        DateOnly endGridDateExclusive,
        IDictionary<DateOnly, List<CalendarEventListItem>> groups)
    {
        var eventTimeZone = calendarEvent.IsAllDay
            ? ResolveTimeZone(calendarEvent.TimeZoneId)
            : displayTimeZone;
        var localStart = TimeZoneInfo.ConvertTime(calendarEvent.StartUtc, eventTimeZone);
        var localEnd = TimeZoneInfo.ConvertTime(calendarEvent.EndUtc, eventTimeZone);
        var eventFirstDate = DateOnly.FromDateTime(localStart.DateTime);
        var eventLastDate = DateOnly.FromDateTime(localEnd.AddTicks(-1).DateTime);
        var firstVisibleDate = eventFirstDate < firstGridDate ? firstGridDate : eventFirstDate;
        var lastGridDate = endGridDateExclusive.AddDays(-1);
        var lastVisibleDate = eventLastDate > lastGridDate ? lastGridDate : eventLastDate;

        for (var date = firstVisibleDate; date <= lastVisibleDate; date = date.AddDays(1))
        {
            var isFirstDate = date == eventFirstDate;
            var timeLabel = calendarEvent.IsAllDay
                ? "全天"
                : isFirstDate ? localStart.ToString("HH:mm") : "续";
            var sortTime = calendarEvent.IsAllDay || !isFirstDate
                ? TimeSpan.Zero
                : localStart.TimeOfDay;

            groups[date].Add(new CalendarEventListItem(
                calendarEvent.Id,
                calendarEvent.Title,
                timeLabel,
                calendarEvent.IsAllDay,
                sortTime));
        }
    }

    private static ValidatedEventValues ValidateAndConvert(CalendarEventDraft draft)
    {
        var title = draft.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("事件标题不能为空。", nameof(draft));
        }

        var timeZone = ResolveTimeZone(draft.TimeZoneId);
        var startLocal = DateTime.SpecifyKind(
            draft.IsAllDay ? draft.StartLocal.Date : draft.StartLocal,
            DateTimeKind.Unspecified);
        var endLocal = DateTime.SpecifyKind(
            draft.IsAllDay ? draft.EndLocal.Date : draft.EndLocal,
            DateTimeKind.Unspecified);
        var startUtc = ConvertLocalToUtc(startLocal, timeZone);
        var endUtc = ConvertLocalToUtc(endLocal, timeZone);

        if (endUtc <= startUtc)
        {
            throw new ArgumentException("结束时间必须晚于开始时间。", nameof(draft));
        }

        return new ValidatedEventValues(
            title,
            draft.Description.Trim(),
            draft.Location.Trim(),
            startUtc,
            endUtc);
    }

    private static DateTimeOffset ConvertLocalToUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        if (timeZone.IsInvalidTime(localDateTime))
        {
            throw new ArgumentException("所选时间处于夏令时跳过区间，请选择其他时间。");
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified),
            timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("时区不能为空。", nameof(timeZoneId));
        }

        if (timeZoneId == TimeZoneInfo.Local.Id)
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException("无法识别事件时区。", nameof(timeZoneId), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException("事件时区配置无效。", nameof(timeZoneId), exception);
        }
    }

    private async Task<SyncOperation> CreateOperationAsync(
        CalendarEvent calendarEvent,
        SyncOperationType operationType,
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken)
    {
        var deviceId = await deviceService.GetDeviceIdAsync(cancellationToken);
        return new SyncOperation
        {
            OperationId = Guid.NewGuid(),
            DeviceId = deviceId,
            EntityId = calendarEvent.Id,
            OperationType = operationType,
            TimestampUtc = timestampUtc.ToUniversalTime(),
            Payload = JsonSerializer.Serialize(calendarEvent, PayloadSerializerOptions),
            Status = SyncOperationStatus.Pending
        };
    }

    private sealed record ValidatedEventValues(
        string Title,
        string Description,
        string Location,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    private sealed record EventRangeResult(
        TimeZoneInfo DisplayTimeZone,
        IReadOnlyList<CalendarEvent> Events);
}
