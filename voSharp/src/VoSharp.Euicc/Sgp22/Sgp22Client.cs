using System.Text.RegularExpressions;
using VoSharp.Common.Utils;
using VoSharp.Euicc.Asn1;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Transport;

namespace VoSharp.Euicc.Sgp22;

/// <summary>
/// Client implementing the GSMA SGP.22 ES10 (LPA to eUICC interface) profile management subset.
/// Supports APDU STORE DATA commands over logical channels for ES10.GetProfilesInfo,
/// ES10.EnableProfile, ES10.DisableProfile, ES10.DeleteProfile, ES10.SetNickname, and ES10.GetEuiccInfo.
/// </summary>
public class Sgp22Client
{
    public const string IsdrAidStandard = "A0000005591010FFFFFFFF8900000100";
    public const string IsdrAidTest = "A0000005591010FFFFFFFF8900000101";

    public const uint TagGetProfilesInfo = 0xBF2D;
    public const uint TagProfileInfo = 0xE3;
    public const uint TagEnableProfile = 0xBF31;
    public const uint TagDisableProfile = 0xBF32;
    public const uint TagDeleteProfile = 0xBF33;
    public const uint TagSetNickname = 0xBF29;
    public const uint TagGetEuiccInfo1 = 0xBF20;
    public const uint TagGetEuiccInfo2 = 0xBF22;

    public const uint TagEID = 0x5A;
    public const uint TagICCID = 0x5A;
    public const uint TagISDPAID = 0x4F;
    public const uint TagProfileState = 0x9F70;
    public const uint TagProfileNickname = 0x90;
    public const uint TagServiceProviderName = 0x91;
    public const uint TagProfileName = 0x92;
    public const uint TagIconType = 0x93;
    public const uint TagProfileClass = 0x95;
    public const uint TagResultCode = 0x80;

    private readonly IEuiccTransport _transport;
    private readonly int _channel;

    public Sgp22Client(IEuiccTransport transport, int channel)
    {
        _transport = transport;
        _channel = channel;
    }

    private byte[] BuildStoreDataApdu(byte[] payload, bool includeLe = true)
    {
        byte cla = (byte)(0x80 | (_channel & 0x03));
        var apdu = new List<byte> { cla, 0xE2, 0x91, 0x00, (byte)payload.Length };
        apdu.AddRange(payload);
        if (includeLe)
        {
            apdu.Add(0x00); // Le=00 per ISO 7816 Case 4 / GSMA SGP.22
        }
        return apdu.ToArray();
    }

    private async Task<Tlv> TransmitCommandAsync(Tlv requestTlv, CancellationToken ct = default)
    {
        var reqBytes = requestTlv.Encode();
        var apdu = BuildStoreDataApdu(reqBytes, includeLe: true);

        byte[] respBytes;
        try
        {
            respBytes = await _transport.TransmitLogicalChannelAsync(_channel, apdu, ct).ConfigureAwait(false);
        }
        catch
        {
            // Retry without Le for cards with non-standard APDU case handling
            apdu = BuildStoreDataApdu(reqBytes, includeLe: false);
            respBytes = await _transport.TransmitLogicalChannelAsync(_channel, apdu, ct).ConfigureAwait(false);
        }

        if (respBytes.Length < 2)
            throw new InvalidOperationException("eUICC APDU response is too short");

        byte sw1 = respBytes[^2];
        byte sw2 = respBytes[^1];
        var data = respBytes[..^2];

        // Handle standard 61 xx (GET RESPONSE) if needed
        if (sw1 == 0x61)
        {
            byte cla = (byte)(_channel & 0x03);
            var getRespApdu = new byte[] { cla, 0xC0, 0x00, 0x00, sw2 };
            var extraResp = await _transport.TransmitLogicalChannelAsync(_channel, getRespApdu, ct).ConfigureAwait(false);
            if (extraResp.Length >= 2)
            {
                sw1 = extraResp[^2];
                sw2 = extraResp[^1];
                data = extraResp[..^2];
            }
        }

        if (sw1 != 0x90 || sw2 != 0x00)
            throw new InvalidOperationException($"eUICC returned error SW: {sw1:X2} {sw2:X2} (Data len: {data.Length})");

        if (data.Length == 0)
            throw new InvalidOperationException("eUICC response contained empty payload");

        var (resTlv, _) = Tlv.Parse(data);
        return resTlv;
    }

    public async Task<string> GetEIDAsync(CancellationToken ct = default)
    {
        // 1. GSMA SGP.22 ES10c GetEuiccData (BF3E) requesting tag 5A (EID)
        // Request: BF 3E 03 5C 01 5A
        try
        {
            var req = new Tlv(0xBF3E);
            req.Children.Add(new Tlv(0x5C, new byte[] { 0x5A }));
            var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
            var eidTlv = resp.FindFirstRecursive(TagEID);
            if (eidTlv != null && eidTlv.Value.Length > 0)
            {
                return eidTlv.HexValue().ToUpperInvariant();
            }
        }
        catch { }

        // 2. Fallback: GetEuiccInfo1 (BF20)
        try
        {
            var req1 = new Tlv(TagGetEuiccInfo1);
            var resp1 = await TransmitCommandAsync(req1, ct).ConfigureAwait(false);
            var eidTlv = resp1.FindFirstRecursive(TagEID);
            if (eidTlv != null && eidTlv.Value.Length > 0)
            {
                return eidTlv.HexValue().ToUpperInvariant();
            }
        }
        catch { }

        // 3. Fallback: GetEuiccInfo2 (BF22)
        try
        {
            var req2 = new Tlv(TagGetEuiccInfo2);
            var resp2 = await TransmitCommandAsync(req2, ct).ConfigureAwait(false);
            var eidTlv = resp2.FindFirstRecursive(TagEID);
            if (eidTlv != null && eidTlv.Value.Length > 0)
            {
                return eidTlv.HexValue().ToUpperInvariant();
            }
        }
        catch { }

        throw new InvalidOperationException("未找到卡片 EID (卡片可能未返回 5A 标签或使用定制指令)");
    }

    public async Task<List<Profile>> GetProfilesInfoAsync(CancellationToken ct = default)
    {
        Tlv? resp = null;
        try
        {
            // 1. Try standard empty request body BF 2D 00 first (as in VoCat/lpac, universal SGP.22 compatibility)
            var reqEmpty = new Tlv(TagGetProfilesInfo);
            resp = await TransmitCommandAsync(reqEmpty, ct).ConfigureAwait(false);
        }
        catch
        {
            // 2. Fallback to TagList = 5A (iccid), 4F (isdpAid), 9F70 (state), 90 (nickname), 91 (spn), 92 (name), 93 (iconType), 95 (class)
            var tagList = new byte[] { 0x5A, 0x4F, 0x9F, 0x70, 0x90, 0x91, 0x92, 0x93, 0x95 };
            var req = new Tlv(TagGetProfilesInfo);
            req.Children.Add(new Tlv(0x5C, tagList));
            resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
        }
        var profiles = new List<Profile>();

        var profileEntries = resp.FindAllRecursive(TagProfileInfo);
        foreach (var entry in profileEntries)
        {
            var p = new Profile();

            // ICCID (5A)
            if (entry.FindFirstRecursive(TagICCID) is { } iccidTlv && iccidTlv.Value.Length > 0)
            {
                p.ICCID = Tlv.BcdToIccid(iccidTlv.Value);
            }

            // ISD-P AID (4F)
            if (entry.FindFirstRecursive(TagISDPAID) is { } aidTlv && aidTlv.Value.Length > 0)
            {
                p.ISDPAID = aidTlv.HexValue();
            }

            // Profile State (9F70)
            if (entry.FindFirstRecursive(TagProfileState) is { } stateTlv)
            {
                p.State = (ProfileState)stateTlv.IntValue();
            }

            // Nickname (90)
            if (entry.FindFirstRecursive(TagProfileNickname) is { } nickTlv)
            {
                p.Nickname = nickTlv.TextValue();
            }

            // Service Provider Name (91)
            if (entry.FindFirstRecursive(TagServiceProviderName) is { } spnTlv)
            {
                p.ServiceProviderName = spnTlv.TextValue();
            }

            // Profile Name (92)
            if (entry.FindFirstRecursive(TagProfileName) is { } nameTlv)
            {
                p.ProfileName = nameTlv.TextValue();
            }

            // Icon Type (93)
            if (entry.FindFirstRecursive(TagIconType) is { } iconTlv)
            {
                p.IconType = iconTlv.IntValue();
            }

            // Profile Class (95)
            if (entry.FindFirstRecursive(TagProfileClass) is { } classTlv)
            {
                p.ProfileClass = (ProfileClass)classTlv.IntValue();
            }

            if (!string.IsNullOrEmpty(p.ICCID) || !string.IsNullOrEmpty(p.ISDPAID))
            {
                profiles.Add(p);
            }
        }

        return profiles;
    }

    public async Task EnableProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        var identTlv = BuildProfileIdentifierTlv(iccidOrAid);
        var req = new Tlv(TagEnableProfile);
        req.Children.Add(identTlv);
        req.Children.Add(new Tlv(0x81, [refresh ? (byte)0xFF : (byte)0x00]));

        try
        {
            var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
            CheckResultCode(resp, "EnableProfile");
        }
        catch (Exception ex)
        {
            // On SIM REFRESH, card reset may occur before response is received
            if (ex.Message.Contains("channel") || ex.Message.Contains("closed") || ex.Message.Contains("61"))
                return;
            throw;
        }
    }

    public async Task DisableProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        var identTlv = BuildProfileIdentifierTlv(iccidOrAid);
        var req = new Tlv(TagDisableProfile);
        req.Children.Add(identTlv);
        req.Children.Add(new Tlv(0x81, [refresh ? (byte)0xFF : (byte)0x00]));

        try
        {
            var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
            CheckResultCode(resp, "DisableProfile");
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("channel") || ex.Message.Contains("closed") || ex.Message.Contains("61"))
                return;
            throw;
        }
    }

    public async Task DeleteProfileAsync(string iccidOrAid, CancellationToken ct = default)
    {
        var identTlv = BuildProfileIdentifierTlv(iccidOrAid);
        var req = new Tlv(TagDeleteProfile);
        req.Children.Add(identTlv);

        var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
        CheckResultCode(resp, "DeleteProfile");
    }

    public async Task SetNicknameAsync(string iccidOrAid, string nickname, CancellationToken ct = default)
    {
        var identTlv = BuildProfileIdentifierTlv(iccidOrAid);
        var req = new Tlv(TagSetNickname);
        req.Children.Add(identTlv);
        req.Children.Add(new Tlv(TagProfileNickname, nickname));

        var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
        CheckResultCode(resp, "SetNickname");
    }

    public async Task<EuiccInfo> GetEuiccInfoAsync(CancellationToken ct = default)
    {
        var req = new Tlv(TagGetEuiccInfo1);
        var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);

        var info = new EuiccInfo();
        if (resp.FindFirstRecursive(TagEID) is { } eidTlv)
            info.EID = eidTlv.HexValue();

        if (resp.FindFirstRecursive(0x82) is { } verTlv)
            info.FirmwareVer = verTlv.TextValue();

        if (resp.FindFirstRecursive(0x83) is { } nvramTlv)
            info.FreeNvramBytes = nvramTlv.IntValue();

        return info;
    }

    private static Tlv BuildProfileIdentifierTlv(string iccidOrAid)
    {
        var clean = iccidOrAid.Trim().Replace(" ", "").Replace("-", "");
        Tlv inner;

        // If numeric 16..20 chars -> ICCID BCD
        if (Regex.IsMatch(clean, @"^\d{16,20}$"))
        {
            inner = new Tlv(TagICCID, Tlv.IccidToBcd(clean));
        }
        else if (Regex.IsMatch(clean, @"^[0-9A-Fa-f]{10,32}$"))
        {
            inner = new Tlv(TagISDPAID, HexUtils.FromHexString(clean));
        }
        else
        {
            inner = new Tlv(TagICCID, Tlv.IccidToBcd(clean));
        }

        // GSMA SGP.22 ES10b: profileIdentifier is defined as [0] CHOICE (Tag 0xA0)
        var a0 = new Tlv(0xA0);
        a0.Children.Add(inner);
        return a0;
    }

    private static void CheckResultCode(Tlv resp, string opName)
    {
        var resCodeTlv = resp.FindFirstRecursive(TagResultCode);
        if (resCodeTlv != null && resCodeTlv.Value.Length > 0)
        {
            int code = resCodeTlv.IntValue();
            if (code == 0) return;
            // In SGP.22 EnableProfile, code 2 means profileNotInDisabledState (already enabled)
            if (opName == "EnableProfile" && code == 2)
                return;
            throw new InvalidOperationException($"eUICC {opName} operation returned error code: {code} (0x{code:X2})");
        }
    }
}
