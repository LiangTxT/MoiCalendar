namespace MoiCalendar.Core;

public sealed class SyncOperationException : Exception
{
    public SyncOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
