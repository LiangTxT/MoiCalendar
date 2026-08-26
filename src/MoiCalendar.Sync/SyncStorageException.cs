namespace MoiCalendar.Sync;

public sealed class SyncStorageException : Exception
{
    public SyncStorageException(string message) : base(message)
    {
    }

    public SyncStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
