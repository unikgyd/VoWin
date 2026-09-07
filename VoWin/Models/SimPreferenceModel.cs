using CommunityToolkit.Mvvm.ComponentModel;

namespace VoWin.Models
{
    public partial class SimPreferenceModel : ObservableObject
    {
        [ObservableProperty]
        private string _iccid = string.Empty;

        [ObservableProperty]
        private string? _imsi;

        [ObservableProperty]
        private string? _cardNickname;

        [ObservableProperty]
        private bool _defaultFlightMode = false;

        [ObservableProperty]
        private bool _defaultVoWifi = true;

        [ObservableProperty]
        private bool _defaultCellularData = true;

        [ObservableProperty]
        private bool _defaultDataRoaming = true;

        [ObservableProperty]
        private string? _dedicatedProxyUrl;

        [ObservableProperty]
        private string? _customEpdg;

        [ObservableProperty]
        private DateTime _lastSeenAt = DateTime.Now;
    }
}
