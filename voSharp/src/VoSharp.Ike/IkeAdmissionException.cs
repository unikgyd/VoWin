namespace VoSharp.Ike;

/// <summary>A local send-policy failure is never proof authorizing a new route, identity or proposal.</summary>
public class IkeAdmissionException(string message, Exception? inner = null) : Exception(message, inner)
{
    internal static bool Contains(Exception error) => error is IkeAdmissionException ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(Contains) ||
        error.InnerException is { } inner && Contains(inner);
}
