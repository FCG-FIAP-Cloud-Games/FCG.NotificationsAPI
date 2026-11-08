namespace FCG.Notifications.Infrastructure.Messaging;

public sealed class PermanentMessageException : Exception
{
    public PermanentMessageException(string message) : base(message)
    {
    }
}