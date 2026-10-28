namespace qmiSharp.Transport;

/// <summary>
/// A Windows driver that allocates QMI service CIDs itself. CTL messages must
/// not be sent through its per-service file handles.
/// </summary>
public interface IDriverManagedQmiTransport : IQmiTransport
{
    ValueTask<byte> AllocateClientIdAsync(ushort service, CancellationToken cancellationToken = default);
    ValueTask ReleaseClientIdAsync(ushort service, byte clientId, CancellationToken cancellationToken = default);
}
