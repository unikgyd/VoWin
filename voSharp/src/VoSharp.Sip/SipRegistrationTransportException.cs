using System.Net.Sockets;

namespace VoSharp.Sip;

/// <summary>
/// Issued only by a live REGISTER transaction's sender/waiter before any
/// correlated SIP response. Local callbacks and AKA failures cannot issue it.
/// Its owner must still finish teardown before trying another gateway.
/// </summary>
public sealed class SipRegistrationTransportException : IOException
{
    internal SipRegistrationTransportException(Exception cause)
        : base("IMS registration transport failed before any SIP response.", cause)
    {
        if (!IsNetworkFailure(cause))
            throw new ArgumentException("A recognized network failure is required.", nameof(cause));
    }

    internal static bool IsNetworkFailure(Exception error) => error switch
    {
        AggregateException group => group.InnerExceptions.Count > 0 && group.InnerExceptions.All(IsNetworkFailure),
        SocketException socket => socket.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionRefused
            or SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkDown
            or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.HostUnreachable
            or SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
            or SocketError.NotConnected or SocketError.Shutdown,
        TimeoutException timeout => timeout.InnerException is null || IsNetworkFailure(timeout.InnerException),
        EndOfStreamException => true,
        IOException { InnerException: { } inner } => IsNetworkFailure(inner),
        _ => false
    };

    public static bool HasUnmixedProof(Exception error) => error switch
    {
        SipRegistrationTransportException => true,
        AggregateException group => group.InnerExceptions.Count > 0 && group.InnerExceptions.All(HasUnmixedProof),
        _ => false
    };
}
