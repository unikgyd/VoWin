using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace VoWin.Models;

public sealed record SmsApiSettings
{
    public bool Enabled { get; init; }
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8787;
    public string Token { get; init; } = "";
    public bool AllowLanAccess { get; init; }

    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public override string ToString() => $"SmsApiSettings {{ Enabled = {Enabled}, BindAddress = {BindAddress}, Port = {Port} }}";

    public IPAddress Validate()
    {
        if (!IPAddress.TryParse(BindAddress.Trim(), out var address))
            throw new ArgumentException("监听地址须为本机 IP，例如 127.0.0.1 或具体的局域网／VPN IP。");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        var privateAddress = address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            : address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
        if (!IPAddress.IsLoopback(address) && !privateAddress)
            throw new ArgumentException("仅支持回环或具体的内网／VPN IP，不接受 0.0.0.0、通配符及公网 IP。公网请经 HTTPS 反向代理。");
        if (!IPAddress.IsLoopback(address) && !AllowLanAccess)
            throw new ArgumentException("监听非回环 IP 前，请确认并开启「允许局域网／VPN 访问」。");
        if (Port is < 1024 or > 65535) throw new ArgumentException("端口须为 1024–65535。");
        if (Enabled && (Token.Length is < 32 or > 128 || Token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))))
            throw new ArgumentException("启用前请生成 Token，或填写 32–128 位字母、数字、下划线、连字符。");
        return address;
    }

    public string BaseUrl
    {
        get
        {
            if (!IPAddress.TryParse(BindAddress.Trim(), out var address)) return "";
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return $"http://{(address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString())}:{Port}/";
        }
    }
}

public sealed record SmsInboxQuery(DateTime SinceUtc, DateTime UntilUtc, int Limit = 20,
    string? Keyword = null, string? Sender = null, string? SlotId = null, bool OtpOnly = false);
public sealed record SmsInboxQueryResult(IReadOnlyList<SmsMessageModel> Messages, bool ScanLimited);
