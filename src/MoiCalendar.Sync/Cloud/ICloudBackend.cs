namespace MoiCalendar.Sync.Cloud;

public interface ICloudBackend
{
    string ProviderId { get; }

    bool IsEnabled { get; }

    CloudBackendCapabilities Capabilities { get; }
}

public sealed record CloudBackendCapabilities(
    bool Accounts,
    bool Realtime,
    bool CalendarSynchronization);
