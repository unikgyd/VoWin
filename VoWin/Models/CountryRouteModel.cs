using CommunityToolkit.Mvvm.ComponentModel;

namespace VoWin.Models
{
    public partial class CountryRouteModel : ObservableObject
    {
        [ObservableProperty]
        private string _countryCode = string.Empty;

        [ObservableProperty]
        private string _countryName = string.Empty;

        [ObservableProperty]
        private string _flagEmoji = "🌐";

        [ObservableProperty]
        private string _mccList = string.Empty;

        [ObservableProperty]
        private string? _proxyNodeId;

        [ObservableProperty]
        private string _proxyNodeName = "直连模式 (Direct)";

        partial void OnProxyNodeIdChanged(string? value)
        {
            OnPropertyChanged(nameof(IsDirect));
        }

        public bool IsDirect => string.IsNullOrEmpty(ProxyNodeId) || ProxyNodeId == "DIRECT";

        public string DisplayTitle
        {
            get => $"{FlagEmoji} {CountryName} ({CountryCode})";
            set { }
        }

        public string MccDisplay
        {
            get => $"MCC: {MccList}";
            set { }
        }
    }
}
