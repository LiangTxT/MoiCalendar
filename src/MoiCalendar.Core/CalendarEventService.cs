namespace MoiCalendar.Core;

public sealed class CalendarEventService(IEventRepository repository, TimeProvider timeProvider)
{
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

        return await repository.CreateAsync(calendarEvent, cancellationToken);
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

        return await repository.UpdateAsync(updated, cancellationToken);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(id, timeProvider.GetUtcNow(), cancellationToken);

    public Task<CalendarEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(id, cancellationToken);

    public async Task<CalendarMonthEventView> GetMonthViewAsync(
        CalendarMonthView monthView,
        string displayTimeZoneId,
        CancellationToken cancellationToken = default)
    {
        var displayTimeZone = ResolveTimeZone(displayTimeZoneId);
        var firstDate = monthView.Dates[0].Date;
        var endDateExclusive = monthView.Dates[^1].Date.AddDays(1);
        var rangeStartUtc = ConvertLocalToUtc(firstDate.ToDateTime(TimeOnly.MinValue), displayTimeZone);
        var rangeEndUtc = ConvertLocalToUtc(endDateExclusive.ToDateTime(TimeOnly.MinValue), displayTimeZone);
        var calendarEvents = await repository.GetByRangeAsync(rangeStartUtc, rangeEndUtc, cancellationToken);
        var groups = monthView.Dates.ToDictionary(
            date => date.Date,
            _ => new List<CalendarEventListItem>());

        foreach (var calendarEvent in calendarEvents)
        {
            AddEventToDates(calendarEvent, displayTimeZone, firstDate, endDateExclusive, groups);
        }

        var orderedGroups = groups.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CalendarEventListItem>)pair.Value
                .OrderBy(item => item.IsAllDay ? 0 : 1)
                .ThenBy(item => item.SortTime)
                .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                .ToArray());

        return new CalendarMonthEventView(orderedGroups);
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

    private sealed record ValidatedEventValues(
        string Title,
        string Description,
        string Location,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);
}
