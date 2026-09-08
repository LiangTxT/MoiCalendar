namespace MoiCalendar.Sync;

public class SyncStorageException : Exception
{
    public SyncStorageException(string message) : base(message)
    {
    }

    public SyncStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SyncContentTooLargeException(string message) : SyncStorageException(message);
