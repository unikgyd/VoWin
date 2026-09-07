namespace VoSharp.Modem.At;

/// <summary>
/// Abstraction over the AT command channel, so AKA providers can be exercised offline
/// without a serial port.
/// </summary>
public interface IAtSession
{
    bool IsOpen { get; }

    Task<AtResponse> ExecuteCommandAsync(
        string command,
        int timeoutMs = 2000,
        CancellationToken ct = default);

    Task<AtResponse> ExecutePromptCommandAsync(
        string initialCommand,
        string payload,
        int promptTimeoutMs = 3000,
        int completionTimeoutMs = 15000,
        CancellationToken ct = default);
}

