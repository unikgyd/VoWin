namespace VoSharp.Common.Events;

public class LogEmittedEventArgs : EventArgs
{
    public string Level { get; }
    public string Source { get; }
    public string Message { get; }
    public Exception? Exception { get; }
    public DateTime Timestamp { get; }

    public LogEmittedEventArgs(string level, string source, string message, Exception? exception = null, DateTime? timestamp = null)
    {
        Level = level;
        Source = source;
        Message = message;
        Exception = exception;
        Timestamp = timestamp ?? DateTime.UtcNow;
    }
}

public class SystemErrorEventArgs : EventArgs
{
    public string Source { get; }
    public string ErrorMessage { get; }
    public Exception? Exception { get; }
    public DateTime Timestamp { get; }

    public SystemErrorEventArgs(string source, string errorMessage, Exception? exception = null, DateTime? timestamp = null)
    {
        Source = source;
        ErrorMessage = errorMessage;
        Exception = exception;
        Timestamp = timestamp ?? DateTime.UtcNow;
    }
}
