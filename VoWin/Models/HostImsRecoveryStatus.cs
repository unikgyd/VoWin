namespace VoWin.Models;

public sealed record HostImsRecoveryStatus(string State, bool KeepOnlineRequested, bool IsWaiting,
    bool IsPaused, int Attempts, DateTime? LastRecoveredAtUtc, string Message);
