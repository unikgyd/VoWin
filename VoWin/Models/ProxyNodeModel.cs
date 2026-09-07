using CommunityToolkit.Mvvm.ComponentModel;

namespace VoWin.Models
{
    public partial class ProxyNodeModel : ObservableObject
    {
        [ObservableProperty]
        private string _id = Guid.NewGuid().ToString("N");

        [ObservableProperty]
        private string _name = "默认代理";

        [ObservableProperty]
        private string _protocol = "socks5"; // socks5, http, https

        [ObservableProperty]
        private string _host = "127.0.0.1";

        [ObservableProperty]
        private int _port = 1080;

        [ObservableProperty]
        private string? _username;

        [ObservableProperty]
        private string? _password;

        [ObservableProperty]
        private bool _isEnabled = true;

        [ObservableProperty]
        private int? _latencyMs;

        partial void OnLatencyMsChanged(int? value)
        {
            OnPropertyChanged(nameof(LatencyDisplay));
        }

        public string LatencyDisplay => LatencyMs.HasValue
            ? (LatencyMs.Value >= 0 ? $"{LatencyMs.Value} ms" : "超时/不可达")
            : "未测试";

        [ObservableProperty]
        private string _status = "未测试";

        public string ToProxyUrl()
        {
            if (string.IsNullOrWhiteSpace(Host) || Port <= 0) return string.Empty;
            var proto = Protocol.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(Username))
            {
                var pass = Password ?? string.Empty;
                return $"{proto}://{Uri.EscapeDataString(Username)}:{Uri.EscapeDataString(pass)}@{Host}:{Port}";
            }
            return $"{proto}://{Host}:{Port}";
        }

        public string Url => ToProxyUrl();

        public static ProxyNodeModel FromUrl(string url, string name = "导入代理")
        {
            try
            {
                var uri = new Uri(url);
                var node = new ProxyNodeModel
                {
                    Name = name,
                    Protocol = uri.Scheme,
                    Host = uri.Host,
                    Port = uri.Port > 0 ? uri.Port : 1080
                };
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var parts = uri.UserInfo.Split(':');
                    node.Username = Uri.UnescapeDataString(parts[0]);
                    if (parts.Length > 1) node.Password = Uri.UnescapeDataString(parts[1]);
                }
                return node;
            }
            catch
            {
                return new ProxyNodeModel { Name = name };
            }
        }
    }
}
