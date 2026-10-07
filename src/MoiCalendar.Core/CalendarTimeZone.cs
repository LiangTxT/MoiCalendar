namespace MoiCalendar.Core;

/// <summary>Resolve standard Windows/IANA aliases without silently changing an unknown zone to UTC.</summary>
public static class CalendarTimeZone
{
    public static TimeZoneInfo Resolve(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
                return TimeZoneInfo.FindSystemTimeZoneById(iana);
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows))
                return TimeZoneInfo.FindSystemTimeZoneById(windows);
            throw;
        }
    }

    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) throw new ArgumentException("时间落在时区切换导致的无效本地时间内。");
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
