using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Appearance;
using VoWin.Models;
using VoWin.Services;
using System.IO;
namespace VoWin.ViewModels.Pages
{
    public partial class SettingsViewModel : ObservableObject, INavigationAware
    {
        private bool _isInitialized = false;
        private bool _callSettingsLoaded;
        private readonly IVoKernelService _kernel;

        public SettingsViewModel(IVoKernelService kernel) => _kernel = kernel;

        [ObservableProperty]
        private string _appVersion = "VoWin v1.0.0";

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
        [ObservableProperty] private int _autoAnswerDelaySeconds = 30;
        [ObservableProperty] private string _autoAnswerMessagePath = string.Empty;
        [ObservableProperty] private string _callSettingsStatus = string.Empty;

        public async Task OnNavigatedToAsync()
        {
            if (!_isInitialized)
                InitializeViewModel();
            if (!_callSettingsLoaded)
            {
                var settings = await _kernel.GetCallExperienceSettingsAsync();
                VolteAudioPrewarmEnabled = settings.VolteAudioPrewarmEnabled;
                AutoAnswerEnabled = settings.AutoAnswerEnabled;
                AutoAnswerDelaySeconds = settings.AutoAnswerDelaySeconds;
                AutoAnswerMessagePath = settings.AutoAnswerMessagePath ?? string.Empty;
                _callSettingsLoaded = true;
            }
        }

        public Task OnNavigatedFromAsync() => Task.CompletedTask;

        private void InitializeViewModel()
        {
            CurrentTheme = ApplicationThemeManager.GetAppTheme();
            AppVersion = $"VoWin v1.0.0 (Build {GetAssemblyVersion()})";
            _isInitialized = true;
        }

        private string GetAssemblyVersion()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "1.0.0";
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
        private async Task SaveCallSettingsAsync()
        {
            var path = string.IsNullOrWhiteSpace(AutoAnswerMessagePath) ? null : AutoAnswerMessagePath.Trim();
            if (AutoAnswerEnabled && (path == null || !File.Exists(path)))
            {
                CallSettingsStatus = "启用自动接听前，请选择存在的录音文件。";
                return;
            }
            await _kernel.SaveCallExperienceSettingsAsync(new CallExperienceSettings
            {
                VolteAudioPrewarmEnabled = VolteAudioPrewarmEnabled,
                AutoAnswerEnabled = AutoAnswerEnabled,
                AutoAnswerDelaySeconds = AutoAnswerDelaySeconds,
                AutoAnswerMessagePath = path
            });
            CallSettingsStatus = "通话与来电设置已保存。";
        }
    }
}
