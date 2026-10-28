namespace VoSharp.Common.Utils;

/// <summary>Read-only handshake between the desktop and its matching managed bearer owner.</summary>
public sealed record HostImsOwnerProtocol(int ProtocolVersion, bool ManagedRas, bool PerModemBinding)
{
    // v2: output-pipe loss must not interrupt cleanup after the desktop exits.
    public const int CurrentVersion = 2;
    public static HostImsOwnerProtocol Current { get; } = new(CurrentVersion, true, true);
    public bool IsCompatible => ProtocolVersion == CurrentVersion && ManagedRas && PerModemBinding;
}
