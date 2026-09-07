using VoSharp.Common.Aka;
using VoSharp.Sim.Pcsc;

namespace VoSharp.Sim;

/// <summary>Answers USIM AKA challenges through a PC/SC smartcard reader.</summary>
public sealed class PcscAkaProvider : IAkaProvider, IDisposable
{
    /// <summary>USIM application AID (3GPP TS 31.102).</summary>
    private const string UsimAid = "A0000000871002";

    private readonly PcscReader _reader;
    private bool _disposed;

    public PcscAkaProvider(string? readerName = null)
    {
        _reader = new PcscReader();

        if (!_reader.Initialize())
            throw new InvalidOperationException(
                "Could not establish a PC/SC context. Is the Smart Card service running?");

        var readers = _reader.ListReaders();
        if (readers.Length == 0)
            throw new InvalidOperationException("No PC/SC readers found.");

        var target = string.IsNullOrWhiteSpace(readerName) ? readers[0] : readerName;
        if (!_reader.Connect(target))
            throw new InvalidOperationException($"Could not connect to PC/SC reader '{target}'.");

        ReaderName = target;
    }

    public string ReaderName { get; }

    public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default)
    {
        // Selecting ADF.USIM proves a USIM is reachable. An ICCID check would require reading
        // EF-ICCID (2FE2), which is deliberately left out of the readiness path.
        try
        {
            SelectUsimApplication();
            return Task.FromResult(true);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        SelectUsimApplication();

        var apdu = HardwareAka.BuildAuthenticateApdu(challenge.Rand, challenge.Autn);
        var response = HardwareAka.TransmitWithChaining(_reader.TransmitApdu, apdu, cla: 0x00);

        if (response is null || response.Length == 0)
            return Task.FromResult(AkaResult.Failed("Card returned no APDU response for USIM AUTHENTICATE."));

        return Task.FromResult(HardwareAka.ParseAuthenticateResponse(response).ToAkaResult());
    }

    /// <summary>
    /// SELECT by ADF.USIM AID. Most UICCs default to a different application, and issuing
    /// AUTHENTICATE against the wrong one yields the misleading status word 0x9862.
    /// </summary>
    private void SelectUsimApplication()
    {
        var aid = Convert.FromHexString(UsimAid);

        var select = new byte[5 + aid.Length + 1];
        select[0] = 0x00;                  // CLA
        select[1] = 0xA4;                  // INS: SELECT
        select[2] = 0x04;                  // P1: select by name
        select[3] = 0x04;                  // P2: first/only occurrence, return FCI
        select[4] = (byte)aid.Length;      // Lc
        aid.CopyTo(select, 5);
        select[^1] = 0x00;                 // Le

        var response = _reader.TransmitApdu(select);
        if (response is null || response.Length < 2)
            throw new InvalidOperationException("No response while selecting ADF.USIM.");

        var sw = (ushort)((response[^2] << 8) | response[^1]);
        if (sw != 0x9000 && response[^2] != 0x61 && response[^2] != 0x9F)
            throw new InvalidOperationException(
                $"Selecting ADF.USIM failed (SW=0x{sw:X4}). Is a USIM inserted?");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
    }
}
