using CommunityToolkit.Mvvm.ComponentModel;

namespace VoWin.Models
{
    public partial class ModulePreferenceModel : ObservableObject
    {
        [ObservableProperty]
        private string _id = string.Empty;

        [ObservableProperty]
        private string? _imei;

        [ObservableProperty]
        private string? _portName;

        [ObservableProperty]
        private string? _customName;

        [ObservableProperty]
        private bool _defaultFlightMode = false;

        [ObservableProperty]
        private bool _defaultVoWifi = true;

        [ObservableProperty]
        private bool _defaultCellularData = true;

        [ObservableProperty]
        private bool _defaultDataRoaming = true;

        [ObservableProperty]
        private string? _defaultProxyUrl;

        [ObservableProperty]
        private int _baudRate = 115200;

        [ObservableProperty]
        private DateTime _lastSeenAt = DateTime.Now;
    }
}
