using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Appearance;
using VoWin.Models;
using VoWin.Services;
using System.IO;
using System.Net;
using VoSharp.Kernel.SipGateway;
namespace VoWin.ViewModels.Pages
{
    public partial class SettingsViewModel : ObservableObject, INavigationAware
    {
        private bool _isInitialized = false;
        private bool _callSettingsLoaded;
        private readonly IVoKernelService _kernel;

        public SettingsViewModel(IVoKernelService kernel) => _kernel = kernel;

        [ObservableProperty]
        private string _appVersion = "VoWin";

        [ObservableProperty]
        private ApplicationTheme _currentTheme = ApplicationTheme.Unknown;

        [ObservableProperty]
        private string _coreEngineInfo = "VoSharp .NET 10 纯C# telephony 协议栈 (IKEv2 / ESP / SIP / AMR-WB / GSMA ES10)";

        [ObservableProperty]
        private bool _autoConnectKnownModem = true;

        [ObservableProperty]
        private bool _preferVowifiCalling = true;

        [ObservableProperty] private bool _autoAnswerEnabled;
        [ObservableProperty] private bool _volteAudioPrewarmEnabled = true;
        [ObservableProperty] private bool _saveCallRecordings = true;
        [ObservableProperty] private int _autoAnswerDelaySeconds = 30;
        [ObservableProperty] private string _autoAnswerMessagePath = string.Empty;
        [ObservableProperty] private string _recordingDirectory = string.Empty;
        [ObservableProperty] private string _callSettingsStatus = string.Empty;
        [ObservableProperty] private string _sipGatewayBindAddress = string.Empty;
        [ObservableProperty] private int _sipGatewayPort = 5060;
        [ObservableProperty] private string _sipGatewayExtension = "1001";
        [ObservableProperty] private string _sipGatewayPassword = string.Empty;
        [ObservableProperty] private bool _isSipGatewayRunning;
        [ObservableProperty] private string _sipGatewayStatus = "未启动";

        public string SipGatewayActionText => IsSipGatewayRunning ? "停止 SIP 网关" : "启动 SIP 网关";

        public async Task OnNavigatedToAsync()
        {
            if (!_isInitialized)
                InitializeViewModel();
            if (!_callSettingsLoaded)
            {
                var settings = await _kernel.GetCallExperienceSettingsAsync();
                VolteAudioPrewarmEnabled = settings.VolteAudioPrewarmEnabled;
                SaveCallRecordings = settings.SaveCallRecordings;
                AutoAnswerEnabled = settings.AutoAnswerEnabled;
                AutoAnswerDelaySeconds = settings.AutoAnswerDelaySeconds;
                AutoAnswerMessagePath = settings.AutoAnswerMessagePath ?? string.Empty;
                RecordingDirectory = settings.RecordingDirectory ?? GetDefaultRecordingDirectory();
                _callSettingsLoaded = true;
            }
            RefreshSipGatewayStatus();
        }

        public Task OnNavigatedFromAsync() => Task.CompletedTask;

        private void InitializeViewModel()
        {
            CurrentTheme = ApplicationThemeManager.GetAppTheme();
            AppVersion = $"VoWin v{GetAssemblyVersion()}";
            _isInitialized = true;
        }

        private string GetAssemblyVersion()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version?.ToString(3) ?? "0.1.0";
        }

        [RelayCommand]
        private void OnChangeTheme(string parameter)
        {
            switch (parameter)
            {
                case "theme_light":
                    if (CurrentTheme == ApplicationTheme.Light)
                        break;

                    ApplicationThemeManager.Apply(ApplicationTheme.Light);
                    App.ApplyBrandTheme();
                    CurrentTheme = ApplicationTheme.Light;
                    App.SaveThemePreference(CurrentTheme);
                    break;

                default:
                    if (CurrentTheme == ApplicationTheme.Dark)
                        break;

                    ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                    App.ApplyBrandTheme();
                    CurrentTheme = ApplicationTheme.Dark;
                    App.SaveThemePreference(CurrentTheme);
                    break;
            }
        }

        [RelayCommand]
        private void BrowseAutoAnswerMessage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "录音文件|*.wav;*.mp3;*.wma;*.aac|所有文件|*.*",
                Title = "选择自动接听后播放的录音"
            };
            if (dialog.ShowDialog() == true)
                AutoAnswerMessagePath = dialog.FileName;
        }

        [RelayCommand]
        private void BrowseRecordingDirectory()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择通话录音保存文件夹",
                InitialDirectory = Directory.Exists(RecordingDirectory) ? RecordingDirectory : GetDefaultRecordingDirectory()
            };
            if (dialog.ShowDialog() == true)
                RecordingDirectory = dialog.FolderName;
        }

        [RelayCommand]
        private async Task SaveCallSettingsAsync()
        {
            var path = string.IsNullOrWhiteSpace(AutoAnswerMessagePath) ? null : AutoAnswerMessagePath.Trim();
            if (AutoAnswerEnabled && (path == null || !File.Exists(path)))
            {
                CallSettingsStatus = "启用自动接听前，请选择存在的录音文件。";
                return;
            }
            var recordingDirectory = string.IsNullOrWhiteSpace(RecordingDirectory)
                ? GetDefaultRecordingDirectory()
                : RecordingDirectory.Trim();
            if (SaveCallRecordings)
            {
                try
                {
                    Directory.CreateDirectory(recordingDirectory);
                }
                catch (Exception ex)
                {
                    CallSettingsStatus = $"无法使用录音保存文件夹: {ex.Message}";
                    return;
                }
            }
            await _kernel.SaveCallExperienceSettingsAsync(new CallExperienceSettings
            {
                VolteAudioPrewarmEnabled = VolteAudioPrewarmEnabled,
                SaveCallRecordings = SaveCallRecordings,
                RecordingDirectory = recordingDirectory,
                AutoAnswerEnabled = AutoAnswerEnabled,
                AutoAnswerDelaySeconds = AutoAnswerDelaySeconds,
                AutoAnswerMessagePath = path
            });
            CallSettingsStatus = "通话与来电设置已保存。";
        }

        [RelayCommand]
        private async Task ToggleSipGatewayAsync()
        {
            if (IsSipGatewayRunning)
            {
                await _kernel.StopSipGatewayAsync();
                RefreshSipGatewayStatus();
                return;
            }
            if (!IPAddress.TryParse(SipGatewayBindAddress.Trim(), out var bindAddress))
            {
                SipGatewayStatus = "请输入 WireGuard 网卡的 IP，例如 10.66.66.1。";
                return;
            }
            if (string.IsNullOrWhiteSpace(SipGatewayExtension) || string.IsNullOrEmpty(SipGatewayPassword))
            {
                SipGatewayStatus = "分机号和密码不能为空。";
                return;
            }
            try
            {
                await _kernel.StartSipGatewayAsync(new SipGatewayOptions
                {
                    BindAddress = bindAddress,
                    SipPort = SipGatewayPort,
                    Realm = "vowin.local",
                    Accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [SipGatewayExtension.Trim()] = SipGatewayPassword
                    }
                });
                RefreshSipGatewayStatus();
            }
            catch (Exception ex)
            {
                SipGatewayStatus = $"启动失败：{ex.Message}";
            }
        }

        private void RefreshSipGatewayStatus()
        {
            var status = _kernel.GetSipGatewayStatus();
            IsSipGatewayRunning = status?.IsRunning == true;
            SipGatewayStatus = IsSipGatewayRunning
                ? $"监听 {status!.LocalEndPoint} · 已注册 {status.Registrations.Count} 台终端 · 活跃 {status.ActiveDialogs} 通"
                : "未启动（仅允许绑定 WireGuard、回环或其他私网地址）";
        }

        [RelayCommand]
        private void RefreshSipGateway() => RefreshSipGatewayStatus();

        partial void OnIsSipGatewayRunningChanged(bool value) => OnPropertyChanged(nameof(SipGatewayActionText));

        private static string GetDefaultRecordingDirectory() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Recordings");
    }
}
