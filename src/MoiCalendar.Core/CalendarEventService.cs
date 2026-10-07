using System.Text.Json;

namespace MoiCalendar.Core;

public sealed class CalendarEventService(
    IEventRepository repository,
    IDeviceService deviceService,
    ILocalEventChangeRepository localEventChanges,
    TimeProvider timeProvider,
    IRecurrenceExpansionService? recurrenceExpansionService = null,
    ILocalDataOperationLock? operationLock = null,
    ICalendarObservanceProvider? observanceProvider = null)
{
    private const int MaximumTitleLength = 200;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> displayIssues = new();
    public int DisplayIssueCount => displayIssues.Count;
    private const int MaximumDescriptionLength = 4_000;
    private const int MaximumLocationLength = 300;
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IRecurrenceExpansionService recurrenceExpansionService =
        recurrenceExpansionService ?? new RecurrenceExpansionService();
    private readonly ILocalDataOperationLock operationLock =
        operationLock ?? NoOpLocalDataOperationLock.Instance;
    private readonly ICalendarObservanceProvider observanceProvider =
        observanceProvider ?? EmptyCalendarObservanceProvider.Instance;

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
        await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
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
            RecurrenceRule = values.RecurrenceRule,
            ColorIndex = draft.ColorIndex,
            ReminderMinutesBeforeStart = draft.ReminderMinutesBeforeStart,
            ReminderTimeZoneId = ReminderPolicy.NormalizeTimeZone(draft),
            AllDayReminderMinuteOfDay = draft.AllDayReminderMinuteOfDay,
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
        await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
        var existing = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("找不到要更新的日历事件。");
        draft.RemovedExclusionCount = 0;
        if (draft.ExpectedUpdatedAtUtc is { } expected && expected != existing.UpdatedAtUtc)
            throw new EventRepositoryException("编辑期间日程已变化，请重新打开后再修改。", new InvalidOperationException("事件版本不匹配。"));
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
            RecurrenceRule = values.RecurrenceRule,
            ColorIndex = draft.ColorIndex,
            ReminderMinutesBeforeStart = draft.ReminderMinutesBeforeStart,
            ReminderTimeZoneId = ReminderPolicy.NormalizeTimeZone(draft),
            AllDayReminderMinuteOfDay = draft.AllDayReminderMinuteOfDay,
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };

        updated = updated with { ExcludedOccurrenceStartsUtc = RecurrenceExclusionPolicy.Remap(existing, updated, recurrenceExpansionService) };
        draft.RemovedExclusionCount = Math.Max(0, (existing.ExcludedOccurrenceStartsUtc?.Length ?? 0) - updated.ExcludedOccurrenceStartsUtc.Length);

        var operation = await CreateOperationAsync(
            updated,
            SyncOperationType.Update,
            updated.UpdatedAtUtc,
            cancellationToken);
        await localEventChanges.ApplyImportAsync([new(updated, operation, existing.Id, existing.UpdatedAtUtc)], cancellationToken);
        return updated;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
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

    public async Task<CalendarEvent> DeleteOccurrenceAsync(Guid id, DateTimeOffset occurrenceStartUtc, CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken);
        var existing = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("找不到此重复事件系列。");
        if ((existing.ExcludedOccurrenceStartsUtc?.Length ?? 0) >= 100_000)
            throw new ArgumentException("此系列已达到 100000 条单次排除记录上限，请拆分系列后再操作。");
        if (string.IsNullOrWhiteSpace(existing.RecurrenceRule)) throw new ArgumentException("此事件不是重复事件。");
        var start = occurrenceStartUtc.ToUniversalTime();
        var excluded = existing.ExcludedOccurrenceStartsUtc ?? [];
        if (excluded.Length >= 100_000) throw new ArgumentException("此系列已达到 100000 条单次排除记录上限，请拆分系列后再操作。");
        if (excluded.Contains(start)) throw new ArgumentException("这次重复事件已删除，请刷新日历后重试。");
        if (start == DateTimeOffset.MaxValue || !recurrenceExpansionService.Expand([existing], start, start.AddTicks(1)).Any(item => item.StartUtc == start))
            throw new ArgumentException("这次重复事件已不存在，请刷新日历后重试。");
        var updated = existing with
        {
            ExcludedOccurrenceStartsUtc = [.. excluded.Append(start).Order()],
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };
        var operation = await CreateOperationAsync(updated, SyncOperationType.Update, updated.UpdatedAtUtc, cancellationToken);
        await localEventChanges.ApplyImportAsync([new(updated, operation, existing.Id, existing.UpdatedAtUtc)], cancellationToken);
        return updated;
    }

    public Task<CalendarEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(id, cancellationToken);

    public async Task<IReadOnlyList<CalendarEventDisplayIssue>> GetInvalidEventsAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<CalendarEventDisplayIssue>();
        foreach (var item in await repository.GetAllIncludingDeletedAsync(cancellationToken))
        {
            if (item.DeletedAtUtc is not null) continue;
            try
            {
                _ = ResolveTimeZone(item.TimeZoneId);
                if (item.EndUtc <= item.StartUtc) throw new ArgumentException("结束时间不晚于开始时间。");
                if (!string.IsNullOrWhiteSpace(item.RecurrenceRule)) _ = RecurrenceRuleParser.Parse(item.RecurrenceRule);
            }
            catch (Exception exception) when (exception is RecurrenceRuleException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                issues.Add(new(item.Id, item.Title, exception.Message));
            }
        }
        return issues;
    }

    public CalendarEvent? GetOccurrence(CalendarEvent master, DateTimeOffset startUtc) =>
        startUtc == DateTimeOffset.MaxValue ? null : recurrenceExpansionService
            .Expand([master], startUtc, startUtc.AddTicks(1)).FirstOrDefault(item => item.StartUtc == startUtc);

    /// <summary>将一次出现独立保存；系列排除与新事件共用现有批量事务和本地变更记录。</summary>
    public async Task<CalendarEvent> UpdateOccurrenceAsync(Guid id, DateTimeOffset occurrenceStartUtc,
        CalendarEventDraft draft, CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken);
        var existing = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("找不到此重复事件系列。");
        var start = occurrenceStartUtc.ToUniversalTime();
        if (existing.DeletedAtUtc is not null || string.IsNullOrWhiteSpace(existing.RecurrenceRule) ||
            start == DateTimeOffset.MaxValue ||
            !recurrenceExpansionService.Expand([existing], start, start.AddTicks(1)).Any(item => item.StartUtc == start))
            throw new ArgumentException("这次重复事件已不存在，请刷新日历后重试。");
        var values = ValidateAndConvert(draft);
        if (values.RecurrenceRule is not null)
            throw new ArgumentException("单次编辑不能设置重复规则。");
        var now = timeProvider.GetUtcNow();
        var updated = existing with
        {
            ExcludedOccurrenceStartsUtc = [.. (existing.ExcludedOccurrenceStartsUtc ?? []).Append(start).Order()],
            UpdatedAtUtc = now
        };
        var detached = new CalendarEvent
        {
            Id = Guid.NewGuid(), Title = values.Title, Description = values.Description,
            Location = values.Location, StartUtc = values.StartUtc, EndUtc = values.EndUtc,
            TimeZoneId = draft.TimeZoneId, IsAllDay = draft.IsAllDay,
            ColorIndex = draft.ColorIndex,
            ReminderMinutesBeforeStart = draft.ReminderMinutesBeforeStart,
            ReminderTimeZoneId = ReminderPolicy.NormalizeTimeZone(draft),
            AllDayReminderMinuteOfDay = draft.AllDayReminderMinuteOfDay,
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var update = await CreateOperationAsync(updated, SyncOperationType.Update, now, cancellationToken);
        var create = await CreateOperationAsync(detached, SyncOperationType.Create, now, cancellationToken);
        await localEventChanges.ApplyImportAsync([
            new(updated, update, existing.Id, existing.UpdatedAtUtc),
            new(detached, create, null, null)
        ], cancellationToken);
        return detached;
    }

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

    public async Task<IReadOnlyDictionary<CalendarMonth, CalendarAgendaView>> GetAgendaViewsAsync(
        IReadOnlyList<CalendarMonth> months, string displayTimeZoneId, CancellationToken cancellationToken = default)
    {
        var ordered = months.Distinct().OrderBy(month => month.Year).ThenBy(month => month.Month).ToArray();
        if (ordered.Length == 0) return new Dictionary<CalendarMonth, CalendarAgendaView>();
        var first = new DateOnly(ordered[0].Year, ordered[0].Month, 1);
        var end = new DateOnly(ordered[^1].Year, ordered[^1].Month, 1).AddMonths(1);
        var groups = await GetEventGroupsAsync(first, end, displayTimeZoneId, cancellationToken);
        var daysByMonth = groups.Where(pair => pair.Value.Count > 0)
            .GroupBy(pair => CalendarMonth.FromDate(pair.Key))
            .ToDictionary(group => group.Key, group => new CalendarAgendaView(group.OrderBy(pair => pair.Key)
                .Select(pair => new CalendarAgendaDay(pair.Key, pair.Value)).ToArray()));
        return ordered.ToDictionary(month => month, month => daysByMonth.GetValueOrDefault(month, CalendarAgendaView.Empty));
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

        foreach (var observance in observanceProvider.GetObservances(firstDate, endDateExclusive))
        {
            allDayGroups[observance.Date].Add(new CalendarWeekAllDayEvent(
                observance.Id,
                observance.Title,
                CalendarColorIndex: observance.Kind == CalendarObservanceKind.Holiday ? 4 : 2,
                IsReadOnly: true));
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

        foreach (var observance in observanceProvider.GetObservances(firstDate, endDateExclusive))
        {
            groups[observance.Date].Add(new CalendarEventListItem(
                observance.Id,
                observance.Title,
                "全天",
                true,
                TimeSpan.Zero,
                CalendarColorIndex: observance.Kind == CalendarObservanceKind.Holiday ? 4 : 2,
                IsReadOnly: true));
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
        var rangeEventsTask = repository.GetByRangeAsync(rangeStartUtc, rangeEndUtc, cancellationToken);
        var recurringMastersTask = repository.GetRecurringMastersAsync(cancellationToken);
        await Task.WhenAll(rangeEventsTask, recurringMastersTask);

        var candidates = (await rangeEventsTask)
            .Concat(await recurringMastersTask)
            .GroupBy(calendarEvent => calendarEvent.Id)
            .Select(group => group.First())
            .ToArray();
        var calendarEvents = new List<CalendarEvent>();
        var candidateIds = candidates.Select(item => item.Id).ToHashSet();
        foreach (var id in displayIssues.Keys)
            if (!candidateIds.Contains(id)) displayIssues.TryRemove(id, out _);
        foreach (var candidate in candidates)
        {
            try
            {
                _ = ResolveTimeZone(candidate.TimeZoneId);
                if (candidate.EndUtc <= candidate.StartUtc) throw new RecurrenceRuleException("事件结束时间无效。");
                calendarEvents.AddRange(recurrenceExpansionService.Expand([candidate], rangeStartUtc, rangeEndUtc));
                displayIssues.TryRemove(candidate.Id, out _);
            }
            catch (Exception exception) when (exception is RecurrenceRuleException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                displayIssues[candidate.Id] = exception.Message;
            }
        }
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
                allDayGroups[date].Add(new CalendarWeekAllDayEvent(
                    calendarEvent.Id,
                    calendarEvent.Title,
                    !string.IsNullOrWhiteSpace(calendarEvent.RecurrenceRule),
                    CalendarPresentationLayout.GetSegmentPosition(date, eventFirstDate, eventLastDate),
                    calendarEvent.StartUtc,
                    calendarEvent.EndUtc,
                    eventTimeZone.Id, CalendarColorIndex: calendarEvent.ColorIndex));
                continue;
            }

            var startMinute = date == eventFirstDate
                ? GetMinuteOfDay(localStart.TimeOfDay, roundUp: false)
                : 0;
            var endMinute = date == eventLastDate && DateOnly.FromDateTime(localEnd.DateTime) == date
                ? GetMinuteOfDay(localEnd.TimeOfDay, roundUp: true)
                : CalendarMetrics.MinutesPerDay;
            endMinute = Math.Clamp(endMinute, startMinute + 1, CalendarMetrics.MinutesPerDay);
            var durationMinutes = endMinute - startMinute;
            var position = CalendarPresentationLayout.CalculateTimedPosition(
                startMinute,
                durationMinutes,
                CalendarMetrics.DefaultMinimumTimedDisplayMinutes);

            timedGroups[date].Add(new CalendarWeekTimedEvent(
                calendarEvent.Id,
                calendarEvent.Title,
                $"{FormatMinute(startMinute)}–{FormatMinute(endMinute)}",
                position.TopPercentage,
                position.HeightPercentage,
                startMinute,
                durationMinutes,
                !string.IsNullOrWhiteSpace(calendarEvent.RecurrenceRule),
                CalendarPresentationLayout.GetSegmentPosition(date, eventFirstDate, eventLastDate),
                calendarEvent.Location,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                displayTimeZone.Id, CalendarColorIndex: calendarEvent.ColorIndex));
        }
    }

    private static int GetMinuteOfDay(TimeSpan time, bool roundUp)
    {
        var minutes = time.TotalMinutes;
        return roundUp ? (int)Math.Ceiling(minutes) : (int)Math.Floor(minutes);
    }

    private static string FormatMinute(int minute)
    {
        if (minute >= CalendarMetrics.MinutesPerDay)
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
        var visibleRange = new CalendarVisibleRange(firstGridDate, endGridDateExclusive);
        var segments = MultiDayLayoutEngine.Segment(
            [new CalendarRenderEvent(
                calendarEvent.Id,
                calendarEvent.Title,
                DateTime.SpecifyKind(localStart.DateTime, DateTimeKind.Unspecified),
                DateTime.SpecifyKind(localEnd.DateTime, DateTimeKind.Unspecified),
                calendarEvent.IsAllDay)],
            visibleRange);

        foreach (var segment in segments)
        {
            var isFirstDate = !segment.ContinuesFromPreviousDay;
            var timeLabel = calendarEvent.IsAllDay
                ? "全天"
                : isFirstDate ? localStart.ToString("HH:mm") : "续";
            var sortTime = calendarEvent.IsAllDay || !isFirstDate
                ? TimeSpan.Zero
                : localStart.TimeOfDay;

            groups[segment.Date].Add(new CalendarEventListItem(
                calendarEvent.Id,
                calendarEvent.Title,
                timeLabel,
                calendarEvent.IsAllDay,
                sortTime,
                !string.IsNullOrWhiteSpace(calendarEvent.RecurrenceRule),
                segment.Position,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                eventTimeZone.Id, CalendarColorIndex: calendarEvent.ColorIndex));
        }
    }

    private static ValidatedEventValues ValidateAndConvert(CalendarEventDraft draft)
    {
        ReminderPolicy.Validate(draft.ReminderMinutesBeforeStart);
        ReminderPolicy.ValidateAllDayTime(draft.AllDayReminderMinuteOfDay);
        _ = ReminderPolicy.NormalizeTimeZone(draft);
        if (draft.ColorIndex is < 1 or > 8)
            throw new ArgumentException("请选择内置的日程颜色。", nameof(draft));
        var title = draft.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("事件标题不能为空。", nameof(draft));
        }

        var description = draft.Description.Trim();
        var location = draft.Location.Trim();
        if (title.Length > MaximumTitleLength)
        {
            throw new ArgumentException($"事件标题不能超过 {MaximumTitleLength} 个字符。", nameof(draft));
        }
        if (description.Length > MaximumDescriptionLength)
        {
            throw new ArgumentException($"事件说明不能超过 {MaximumDescriptionLength} 个字符。", nameof(draft));
        }
        if (location.Length > MaximumLocationLength)
        {
            throw new ArgumentException($"事件地点不能超过 {MaximumLocationLength} 个字符。", nameof(draft));
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
            description,
            location,
            startUtc,
            endUtc,
            draft.Recurrence.ToRecurrenceRule(startLocal, timeZone, draft.IsAllDay));
    }

    private static DateTimeOffset ConvertLocalToUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        if (timeZone.IsInvalidTime(localDateTime))
        {
            throw new ArgumentException("所选时间处于夏令时跳过区间，请选择其他时间。");
        }

        return CalendarTimeZone.ToUtc(localDateTime, timeZone);
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
            return CalendarTimeZone.Resolve(timeZoneId);
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
        DateTimeOffset EndUtc,
        string? RecurrenceRule);

    private sealed record EventRangeResult(
        TimeZoneInfo DisplayTimeZone,
        IReadOnlyList<CalendarEvent> Events);
}
