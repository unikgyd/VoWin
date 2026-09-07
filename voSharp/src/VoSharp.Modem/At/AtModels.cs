namespace VoSharp.Modem.At;

public record AtResponse(
    bool Success,
    IReadOnlyList<string> Lines,
    string? ErrorCode = null,
    string? RawOutput = null
)
{
    public string FirstDataLine => Lines.FirstOrDefault(l =>
        !l.StartsWith("AT", StringComparison.OrdinalIgnoreCase) &&
        !l.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
        !l.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase) &&
        !l.StartsWith("+CMS ERROR", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
}

public record SignalQuality(
    int RssiRaw,
    int RssiDbm,
    int Bars,
    string Rat
);

public enum NetworkRegStatus
{
    NotRegistered = 0,
    Home = 1,
    Searching = 2,
    Denied = 3,
    Unknown = 4,
    Roaming = 5
}

public record NetworkRegistration(
    NetworkRegStatus Status,
    string? OperatorName,
    string? AccessTechnology,
    string? CellId
)
{
    public string State => Status.ToString();
    public string StatusDisplay => Status switch
    {
        NetworkRegStatus.Home => "已在网 (归属网)",
        NetworkRegStatus.Roaming => "已在网 (漫游)",
        NetworkRegStatus.Searching => "搜索网络中...",
        NetworkRegStatus.Denied => "网络拒绝注册",
        NetworkRegStatus.NotRegistered => "未注册网络",
        _ => "未知状态"
    };
};
