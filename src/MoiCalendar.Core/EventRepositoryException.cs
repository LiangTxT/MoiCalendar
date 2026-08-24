namespace MoiCalendar.Core;

public sealed class EventRepositoryException : Exception
{
    public EventRepositoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
