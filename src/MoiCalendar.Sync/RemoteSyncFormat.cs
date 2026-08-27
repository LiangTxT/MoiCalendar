namespace MoiCalendar.Sync;

public static class RemoteSyncFormat
{
    public const int CurrentVersion = 1;
    public const string FileName = "moicalendar.sync.json";
    public const string MediaType = "application/json";
    public const string OperationsDirectory = "MyCalendar/operations";

    public static string GetOperationPath(Guid operationId) =>
        $"{OperationsDirectory}/{operationId:D}.json";
}
