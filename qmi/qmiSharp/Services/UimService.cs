using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;

namespace qmiSharp.Services;

public enum UimCardState : byte
{
    Absent = 0,
    Present = 1,
    Error = 2
}

public readonly record struct UimCardStatus(UimCardState State, byte NumCards)
{
    /// <summary>The selected GW primary application is a SIM or USIM.</summary>
    public bool HasSubscriptionApplication { get; init; }
    /// <summary>Zero-based QMI card index selected for the GW primary subscription.</summary>
    public byte? SelectedCardIndex { get; init; }
    public byte? SelectedApplicationIndex { get; init; }
    public IReadOnlyList<UimCardApplication> SelectedCardApplications { get; init; } = [];
    [Obsolete("The QMI field is a card count, not a physical slot count. Use NumCards.")]
    public byte NumSlots => NumCards;
}

public sealed record UimCardApplication(byte Type, byte State, byte[] Aid);

public readonly record struct UimSlotStatus(byte PhysicalSlotNumber, bool IsActive, bool CardPresent);

public sealed class UimService : IAsyncDisposable
{
    private readonly QmiClient _client;
    private byte _clientId;

    public UimService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.UIM, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<UimCardStatus> GetCardStatusAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // UIM GET_CARD_STATUS (0x002F)
        var req = new QmiPacket(QmiServiceType.UIM, _clientId, 0, (ushort)UimMessageId.GetCardStatus);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x10)
            ?? throw new QmiException("UIM card-status response omitted TLV 0x10");
        return ParseCardStatus(tlv.Value.Span);
    }

    internal static UimCardStatus ParseCardStatus(ReadOnlySpan<byte> value)
    {
        // Four 16-bit application indexes precede the card count. The high and
        // low bytes of the GW primary index select the card and application.
        if (value.Length < 9)
            throw new FormatException("UIM card-status TLV is truncated before the card count.");
        var count = value[8];
        if (count == 0)
            throw new FormatException("UIM card-status TLV reported no physical card slots.");
        var selectedGwPrimary = BinaryPrimitives.ReadUInt16LittleEndian(value[..2]);
        var selectedCard = (byte)(selectedGwPrimary >> 8);
        var selectedApp = (byte)selectedGwPrimary;
        var hasSelection = selectedGwPrimary != ushort.MaxValue && selectedCard < count;
        var offset = 9;
        var allAbsent = true;
        UimCardState? selectedState = null;
        var hasSubscription = false;
        var selectedApplications = new List<UimCardApplication>();
        for (var card = 0; card < count; card++)
        {
            Require(value, offset, 6, "card header");
            var state = (UimCardState)value[offset];
            if (!Enum.IsDefined(state))
                throw new FormatException("UIM card-status TLV reported an unknown card state.");
            if (state != UimCardState.Absent) allAbsent = false;
            var applications = value[offset + 5];
            offset += 6;
            if (hasSelection && card == selectedCard) selectedState = state;
            for (var app = 0; app < applications; app++)
            {
                // Type, state, personalization fields, AID length, then seven
                // PIN fields. Traverse every app so a later selected app is safe.
                Require(value, offset, 7, "application header");
                var type = value[offset];
                var appState = value[offset + 1];
                var aidLength = value[offset + 6];
                offset += 7;
                Require(value, offset, aidLength + 7, "application AID or PIN status");
                if (hasSelection && card == selectedCard)
                    selectedApplications.Add(new UimCardApplication(type, appState,
                        value.Slice(offset, aidLength).ToArray()));
                offset += aidLength + 7;
                if (hasSelection && card == selectedCard && app == selectedApp &&
                    state == UimCardState.Present && type is 1 or 2 && appState is >= 1 and <= 7)
                    hasSubscription = true;
            }
        }
        var reportedState = selectedState ?? (allAbsent ? UimCardState.Absent : UimCardState.Error);
        return new UimCardStatus(reportedState, count)
        {
            HasSubscriptionApplication = hasSubscription,
            SelectedCardIndex = hasSelection ? selectedCard : null,
            SelectedApplicationIndex = hasSelection ? selectedApp : null,
            SelectedCardApplications = selectedApplications
        };

    }

    private static void Require(ReadOnlySpan<byte> value, int offset, int length, string part)
    {
        if (value.Length - offset < length)
            throw new FormatException($"UIM card-status TLV is truncated within the {part}.");
    }

    /// <summary>
    /// Reads EF-ICCID (0x2FE2) from the SIM card to obtain the ICCID string.
    /// </summary>
    public async Task<string> GetIccidAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // UIM READ_TRANSPARENT (0x0020)
        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, new byte[] { 0x00, 0x00 })
            .AddBytes(0x02, new byte[] { 0xE2, 0x2F, 0x02, 0x3F, 0x00 })
            .AddBytes(0x03, new byte[] { 0x00, 0x00, 0x0A, 0x00 })
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.ReadTransparent, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x11);
        if (tlv == null || tlv.Value.Length < 2)
        {
            return string.Empty;
        }

        var span = tlv.Value.Span;
        ushort dataLength = BinaryPrimitives.ReadUInt16LittleEndian(span[..2]);
        int end = Math.Min(span.Length, 2 + dataLength);
        var sb = new StringBuilder(dataLength * 2);
        for (int i = 2; i < end; i++)
        {
            byte b = span[i];
            int low = b & 0x0F;
            int high = (b >> 4) & 0x0F;
            if (low <= 9) sb.Append(low);
            if (high <= 9) sb.Append(high);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Verifies the PIN for the SIM card (0x0026).
    /// </summary>
    public async Task<byte> VerifyPinAsync(byte pinId, string pinValue, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] pinBytes = Encoding.ASCII.GetBytes(pinValue);
        var pinInfo = new byte[2 + pinBytes.Length];
        pinInfo[0] = pinId; // 1 = PIN1, 2 = PIN2
        pinInfo[1] = (byte)pinBytes.Length;
        Array.Copy(pinBytes, 0, pinInfo, 2, pinBytes.Length);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, new byte[] { 0x00, 0x00 }) // Session info: Primary GW
            .AddBytes(0x02, pinInfo)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.VerifyPin, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var retriesTlv = resp.GetTlv(0x10);
        return retriesTlv != null && retriesTlv.Value.Length >= 1 ? retriesTlv.AsByte(0) : (byte)0;
    }

    /// <summary>
    /// Unblocks the PIN using the PUK (0x0027).
    /// </summary>
    public async Task<byte> UnblockPinAsync(byte pinId, string pukValue, string newPinValue, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] pukBytes = Encoding.ASCII.GetBytes(pukValue);
        byte[] newPinBytes = Encoding.ASCII.GetBytes(newPinValue);

        var info = new byte[3 + pukBytes.Length + newPinBytes.Length];
        info[0] = pinId;
        info[1] = (byte)pukBytes.Length;
        Array.Copy(pukBytes, 0, info, 2, pukBytes.Length);
        int offset = 2 + pukBytes.Length;
        info[offset++] = (byte)newPinBytes.Length;
        Array.Copy(newPinBytes, 0, info, offset, newPinBytes.Length);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, new byte[] { 0x00, 0x00 })
            .AddBytes(0x02, info)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.UnblockPin, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var retriesTlv = resp.GetTlv(0x10);
        return retriesTlv != null && retriesTlv.Value.Length >= 1 ? retriesTlv.AsByte(0) : (byte)0;
    }

    /// <summary>
    /// Changes the SIM PIN (0x0028).
    /// </summary>
    public async Task<byte> ChangePinAsync(byte pinId, string oldPinValue, string newPinValue, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] oldBytes = Encoding.ASCII.GetBytes(oldPinValue);
        byte[] newBytes = Encoding.ASCII.GetBytes(newPinValue);

        var info = new byte[3 + oldBytes.Length + newBytes.Length];
        info[0] = pinId;
        info[1] = (byte)oldBytes.Length;
        Array.Copy(oldBytes, 0, info, 2, oldBytes.Length);
        int offset = 2 + oldBytes.Length;
        info[offset++] = (byte)newBytes.Length;
        Array.Copy(newBytes, 0, info, offset, newBytes.Length);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, new byte[] { 0x00, 0x00 })
            .AddBytes(0x02, info)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.ChangePin, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var retriesTlv = resp.GetTlv(0x10);
        return retriesTlv != null && retriesTlv.Value.Length >= 1 ? retriesTlv.AsByte(0) : (byte)0;
    }

    /// <summary>
    /// Enables or disables PIN protection (0x0025).
    /// </summary>
    public async Task<byte> SetPinProtectionAsync(byte pinId, bool enable, string pinValue, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] pinBytes = Encoding.ASCII.GetBytes(pinValue);
        var info = new byte[3 + pinBytes.Length];
        info[0] = pinId;
        info[1] = enable ? (byte)1 : (byte)0;
        info[2] = (byte)pinBytes.Length;
        Array.Copy(pinBytes, 0, info, 3, pinBytes.Length);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, new byte[] { 0x00, 0x00 })
            .AddBytes(0x02, info)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.SetPinProtection, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var retriesTlv = resp.GetTlv(0x10);
        return retriesTlv != null && retriesTlv.Value.Length >= 1 ? retriesTlv.AsByte(0) : (byte)0;
    }

    /// <summary>Opens ADF.AID on a logical UIM channel (0x0042).</summary>
    public async Task<byte> OpenLogicalChannelAsync(byte slot, ReadOnlyMemory<byte> aid,
        CancellationToken cancellationToken = default)
    {
        if (aid.IsEmpty || aid.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(aid));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var aidTlv = new byte[1 + aid.Length];
        aidTlv[0] = checked((byte)aid.Length);
        aid.Span.CopyTo(aidTlv.AsSpan(1));
        var request = new TlvBuilder().AddByte(0x01, slot).AddBytes(0x10, aidTlv).Build();
        var response = await _client.SendMessageAsync(QmiServiceType.UIM,
            (ushort)UimMessageId.OpenLogicalChannel, request, cancellationToken).ConfigureAwait(false);
        response.CheckResult();
        var channel = response.GetTlv(0x10)
            ?? throw new FormatException("UIM open-logical-channel response omitted channel ID.");
        if (channel.Value.Length != 1 || channel.AsByte() is < 1 or > 19)
            throw new FormatException("UIM open-logical-channel response returned an invalid channel ID.");
        var cardResult = response.GetTlv(0x11);
        if (cardResult is not null && (cardResult.Value.Length != 2 ||
            cardResult.Value.Span[0] != 0x90 || cardResult.Value.Span[1] != 0x00))
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await CloseLogicalChannelAsync(slot, channel.AsByte(), cleanup.Token).ConfigureAwait(false); }
            catch { /* Preserve the card rejection; channel cleanup is best effort. */ }
            throw new QmiException("UIM card rejected the logical-channel AID selection.");
        }
        return channel.AsByte();
    }

    /// <summary>Closes a previously opened UIM logical channel (0x003F).</summary>
    public async Task CloseLogicalChannelAsync(byte slot, byte channelId,
        CancellationToken cancellationToken = default)
    {
        if (channelId is < 1 or > 19)
            throw new ArgumentOutOfRangeException(nameof(channelId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var request = new TlvBuilder().AddByte(0x01, slot).AddByte(0x11, channelId).Build();
        var response = await _client.SendMessageAsync(QmiServiceType.UIM,
            (ushort)UimMessageId.LogicalChannel, request, cancellationToken).ConfigureAwait(false);
        response.CheckResult();
    }

    /// <summary>Sends a raw APDU on the basic channel (0x003B).</summary>
    public Task<byte[]> SendApduAsync(byte slot, ReadOnlyMemory<byte> apduCommand,
        CancellationToken cancellationToken = default) =>
        SendApduAsync(slot, 0, apduCommand, cancellationToken);

    /// <summary>Sends a raw APDU on a selected UIM logical channel (0x003B).</summary>
    public async Task<byte[]> SendApduAsync(byte slot, byte channelId, ReadOnlyMemory<byte> apduCommand,
        CancellationToken cancellationToken = default)
    {
        if (channelId > 19) throw new ArgumentOutOfRangeException(nameof(channelId));
        if (apduCommand.IsEmpty || apduCommand.Length > ushort.MaxValue - 2)
            throw new ArgumentOutOfRangeException(nameof(apduCommand));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var apduTlv = new byte[2 + apduCommand.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(apduTlv.AsSpan(0, 2), (ushort)apduCommand.Length);
        apduCommand.Span.CopyTo(apduTlv.AsSpan(2));

        var reqTlv = new TlvBuilder()
            .AddByte(0x01, slot)
            .AddBytes(0x02, apduTlv)
            .AddByte(0x10, channelId)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.SendApdu, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var respTlv = resp.GetTlv(0x10);
        if (respTlv is null)
            throw new FormatException("UIM send-APDU response omitted TLV 0x10.");
        try { return ParseApduResponse(respTlv.Value.Span); }
        finally
        {
            // An ISIM AUTHENTICATE response can contain RES, CK and IK. The
            // caller owns the returned copy; clear the parsed QMI TLV copy.
            if (MemoryMarshal.TryGetArray(respTlv.Value, out var segment))
                CryptographicOperations.ZeroMemory(segment.AsSpan());
        }
    }

    internal static byte[] ParseApduResponse(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2)
            throw new FormatException("UIM APDU response is truncated before its length.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(value[..2]);
        if (length < 2 || length != value.Length - 2)
            throw new FormatException("UIM APDU response length is invalid or truncated.");
        return value[2..].ToArray();
    }

    /// <summary>
    /// Gets physical slot status (0x0047).
    /// </summary>
    public async Task<IReadOnlyList<UimSlotStatus>> GetSlotStatusAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.GetSlotStatus, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var list = new List<UimSlotStatus>();
        var tlv = resp.GetTlv(0x10);
        if (tlv != null && tlv.Value.Length >= 1)
        {
            byte numSlots = tlv.AsByte(0);
            int offset = 1;
            for (byte i = 0; i < numSlots && offset + 10 <= tlv.Value.Length; i++)
            {
                uint cardState = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span.Slice(offset, 4));
                uint slotState = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span.Slice(offset + 4, 4));
                byte iccidLength = tlv.AsByte(offset + 9);
                if (offset + 10 + iccidLength > tlv.Value.Length) break;
                list.Add(new UimSlotStatus((byte)(i + 1), slotState == 1, cardState == 2));
                offset += 10 + iccidLength;
            }
        }
        return list;
    }

    /// <summary>
    /// Switches the active physical SIM slot (0x0046).
    /// </summary>
    public async Task SwitchSlotAsync(byte physicalSlot, CancellationToken cancellationToken = default)
        => await SwitchSlotAsync(physicalSlot, 1, cancellationToken).ConfigureAwait(false);

    /// <summary>Maps a physical SIM slot to a logical slot (0x0046).</summary>
    public async Task SwitchSlotAsync(byte physicalSlot, byte logicalSlot, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddByte(0x01, logicalSlot)
            .AddUInt32(0x02, physicalSlot)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.SwitchSlot, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Powers off the SIM card in the given slot (0x0030).
    /// </summary>
    public async Task PowerOffSimAsync(byte slot, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder().AddByte(0x01, slot).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.PowerOffSim, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Powers on the SIM card in the given slot (0x0031).
    /// </summary>
    public async Task PowerOnSimAsync(byte slot, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder().AddByte(0x01, slot).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.UIM, (ushort)UimMessageId.PowerOnSim, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.UIM, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
