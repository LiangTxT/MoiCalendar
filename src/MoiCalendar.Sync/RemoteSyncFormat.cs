namespace MoiCalendar.Sync;

public static class RemoteSyncFormat
{
    public const int MinimumSupportedVersion = 1;
    public const int CurrentVersion = 2;
    public const string FileName = "moicalendar.sync.json";
    public const string MediaType = "application/json";
    public const string OperationsDirectory = "MoiCalendar/operations";

    public static string GetOperationPath(Guid operationId) =>
        $"{OperationsDirectory}/{operationId:D}.json";
}
