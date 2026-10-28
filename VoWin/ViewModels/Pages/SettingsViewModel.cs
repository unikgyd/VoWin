using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Appearance;
using VoWin.Models;
using VoWin.Services;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.ObjectModel;
using VoSharp.Kernel.SipGateway;
using VoSharp.Modem;
namespace VoWin.ViewModels.Pages
{
    public partial class SettingsViewModel : ObservableObject, INavigationAware
    {
        private bool _isInitialized = false;
        private bool _callSettingsLoaded;
        private readonly IVoKernelService _kernel;
        private readonly SipGatewaySettingsStore _sipSettings;
        private bool _sipSettingsLoaded;

        public SettingsViewModel(IVoKernelService kernel, SipGatewaySettingsStore? sipSettings = null, ISmsApiService? smsApi = null)
        {
            _kernel = kernel;
            _sipSettings = sipSettings ?? new();
            _smsApi = smsApi;
            if (_smsApi != null) _smsApi.StatusChanged += OnSmsApiStatusChanged;
            SipOutboundLineChoices.Add(new(null, "每次指定线路（X-VoWin-Line）"));
            SelectedSipOutboundLine = SipOutboundLineChoices[0];
        }

        [ObservableProperty]
        private string _appVersion = "VoWin";

        [ObservableProperty]
        private ApplicationTheme _currentTheme = ApplicationTheme.Unknown;

        [ObservableProperty]
        private string _coreEngineInfo = "VoSharp .NET 10 · QMI / AT · Host IMS / VoWiFi · 模组语音桥接 · 可选 SIP 网关 · eSIM";

        [ObservableProperty]
        private bool _autoConnectKnownModem = true;
        [ObservableProperty] private ModemControlPreference _selectedControlPreference = ModemControlPreference.QmiPreferred;
        public IReadOnlyList<ModemControlOption> ControlPreferences { get; } = new[]
        {
            new ModemControlOption(ModemControlPreference.QmiPreferred, "QMI 优先"),
            new ModemControlOption(ModemControlPreference.AtOnly, "AT 兼容")
        };

        [ObservableProperty] private string _telephonySettingsStatus = string.Empty;

        [ObservableProperty] private bool _autoAnswerEnabled;
        [ObservableProperty] private bool _volteAudioPrewarmEnabled = true;
        [ObservableProperty] private bool _saveCallRecordings = true;
        [ObservableProperty] private int _autoAnswerDelaySeconds = 30;
        [ObservableProperty] private string _autoAnswerMessagePath = string.Empty;
        [ObservableProperty] private string _recordingDirectory = string.Empty;
        [ObservableProperty] private string _callSettingsStatus = string.Empty;
        [ObservableProperty] private string _sipGatewayBindAddress = string.Empty;
        [ObservableProperty] private int _sipGatewayPort = 5062;
        [ObservableProperty] private string _sipGatewayExtension = "";
        [ObservableProperty] private string _sipGatewayPassword = string.Empty;
        [ObservableProperty] private bool _isSipGatewayRunning;
        [ObservableProperty] private string _sipGatewayStatus = "已关闭 · 可选功能，不影响本机通话。";
        private bool HasSipStatusError => SipGatewayStatus.StartsWith("操作失败") || SipGatewayStatus.StartsWith("配置未保存") ||
            SipGatewayStatus.StartsWith("无法") || SipGatewayStatus.StartsWith("部署配置未导入") ||
            SipGatewayStatus.StartsWith("SIP 配置读取失败") || SipGatewayStatus.Contains("配置保存失败");
        public VoWin.Models.GatewayStatusItem SipStatusIndicator => new("SIP", SipGatewayStatus, SipGatewayStatus)
        {
            Light = IsSipGatewayBusy ? VoWin.Models.StatusLight.Busy : HasSipStatusError ? VoWin.Models.StatusLight.Error
                : IsSipGatewayRunning ? VoWin.Models.StatusLight.Ready : VoWin.Models.StatusLight.Inactive,
            ShortStatus = IsSipGatewayBusy ? "处理中" : HasSipStatusError ? "操作失败" : null
        };
        public string SipOperationNotice => HasSipStatusError ? "操作失败，请展开连接详情。"
            : SipGatewayStatus.StartsWith("已保存") ? "配置已保存。"
            : SipGatewayStatus.StartsWith("已导入") ? "配置已导入，尚未启用。" : string.Empty;
        partial void OnSipGatewayStatusChanged(string value)
        { OnPropertyChanged(nameof(SipStatusIndicator)); OnPropertyChanged(nameof(SipOperationNotice)); }
        [ObservableProperty] private bool _sipGatewayUseTrunk;
        [ObservableProperty] private string _sipGatewayRealm = "vowin.local";
        [ObservableProperty] private string _sipGatewayServerAddress = string.Empty;
        [ObservableProperty] private int _sipGatewayServerPort = 5060;
        [ObservableProperty] private string _sipGatewayTrunkUsername = "";
        [ObservableProperty] private string _sipGatewayIncomingTarget = "";
        [ObservableProperty] private bool _sipGatewayAllowOutboundCalls;
        [ObservableProperty] private bool _sipGatewayRestoreOnStartup;
        [ObservableProperty] private bool _isSipGatewayBusy;
        public ObservableCollection<SipModuleRouteItem> SipModuleRoutes { get; } = new();
        public ObservableCollection<SipOutboundLineChoice> SipOutboundLineChoices { get; } = new();
        [ObservableProperty] private SipOutboundLineChoice? _selectedSipOutboundLine;

        public string SipGatewayActionText => IsSipGatewayRunning ? "关闭 SIP 对接" : "启用 SIP 对接";
        public bool CanEditSipGateway => !IsSipGatewayRunning && !IsSipGatewayBusy && SipServiceIsStopped();
        private bool SipServiceIsStopped()
        {
            try { return _kernel.GetSipGatewayStatus()?.IsRunning != true; }
            catch { return false; } // A status failure is not permission to mutate a possibly live listener.
        }
        public bool CanToggleSipGateway => !IsSipGatewayBusy;
        public Visibility SipTrunkVisibility => SipGatewayUseTrunk ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SipDirectVisibility => SipLocalVisibility;

        public async Task OnNavigatedToAsync()
        {
            LoadSmsApiSettings();
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
            await LoadSipSettingsAsync();
            RefreshSipModuleRoutesCore();
            var telephony = await _kernel.GetTelephonySettingsAsync();
            AutoConnectKnownModem = telephony.AutoDiscoverModems;
            SelectedControlPreference = telephony.ControlPreference;
        }

        public Task OnNavigatedFromAsync() => Task.CompletedTask;

        [RelayCommand]
        private async Task SaveStartupSettingsAsync()
        {
            try
            {
                var settings = await _kernel.GetTelephonySettingsAsync();
                await _kernel.SaveTelephonySetupAsync(AutoConnectKnownModem, settings.WinDivertDllPath);
                await _kernel.SetModemControlPreferenceAsync(SelectedControlPreference);
                TelephonySettingsStatus = "已保存";
            }
            catch (Exception ex) { TelephonySettingsStatus = $"设置未保存：{ex.Message}"; }
        }

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
            if (IsSipGatewayBusy) return;
            IsSipGatewayBusy = true;
            try
            {
                if (_kernel.GetSipGatewayStatus()?.IsRunning == true)
                {
                    await _kernel.StopSipGatewayAsync();
                    RefreshSipGatewayStatus();
                    await _sipSettings.MarkDisabledAsync();
                    return;
                }
                // Persist the exact receiver incarnation before the listener can
                // capture any private MT intent. A failed save must not start it.
                await PersistSipConnectionAsync(enabled: false);
                var options = BuildSipGatewayOptions();
                await _kernel.StartSipGatewayAsync(options);
                RefreshSipGatewayStatus();
                try { await PersistSipConnectionAsync(enabled: true); }
                catch { SipGatewayStatus = "SIP 已启用，但启用状态保存失败；下次启动可能保持关闭。"; return; }
                RefreshSipGatewayStatus();
            }
            catch (Exception ex)
            {
                IsSipGatewayRunning = _kernel.GetSipGatewayStatus()?.IsRunning == true;
                SipGatewayStatus = $"操作失败：{ex.Message}";
            }
            finally { IsSipGatewayBusy = false; }
        }

        internal SipGatewayOptions BuildSipGatewayOptions()
        {
            RefreshSipModuleRoutesCore();
            return SipGatewayDeploymentProfile.BuildOptions(SnapshotSipSettings(), SnapshotSipCredentials());
        }

        private SipGatewayPreferences SnapshotSipSettings() => new()
        {
            UseTrunk = SipGatewayUseTrunk, BindAddress = SipGatewayBindAddress.Trim(), Port = SipGatewayPort,
            CredentialVersion = 2, LocalRegistrarEnabled = SipLocalRegistrarEnabled,
            IdentityDomain = string.IsNullOrWhiteSpace(SipIdentityDomain) ? null : SipIdentityDomain.Trim(),
            TrustedNetworks = SipPolicyEditor.ParseList(SipTrustedNetworks), LocalRegistrarAlgorithm = SnapshotLocalRegistrarAlgorithm(),
            PeerInboundAlgorithm = SipPeerInboundAlgorithm,
            PeerOutboundUsername = SipPeerOutboundUsername.Trim(), PeerExpectedRealm = SipPeerExpectedRealm.Trim(),
            PeerAllowedAlgorithms = (SipPeerAllowSha256 ? new List<string> { "SHA-256" } : []).Concat(SipPeerAllowMd5 ? new[] { "MD5" } : []).ToList(),
            PrimaryPolicy = SnapshotPrimaryPolicy(),
            PeerAllowOutboundCalls = SipPeerAllowOutboundCalls, PeerAllowOutboundMessages = SipPeerAllowOutboundMessages,
            PeerAllowIncomingCalls = SipPeerAllowIncomingCalls, PeerAllowIncomingMessages = SipPeerAllowIncomingMessages,
            PeerOutgoingLines = SipPolicyEditor.ParseList(SipPeerOutgoingLines), PeerSmsSendLines = SipPolicyEditor.ParseList(SipPeerSmsSendLines),
            LocalAccountEpochs = SnapshotSipRecipientEpochs(), PeerCredentialEpoch = _sipPeerRecipientEpoch,
            Realm = SipGatewayRealm.Trim(), Extension = SipGatewayExtension.Trim(), ServerAddress = SipGatewayServerAddress.Trim(),
            ServerPort = SipGatewayServerPort, TrunkUsername = SipGatewayTrunkUsername.Trim(), IncomingTarget = SipGatewayIncomingTarget.Trim(),
            AllowOutboundCalls = SipGatewayAllowOutboundCalls, DefaultOutboundLineId = SelectedSipOutboundLine?.SlotId,
            RestoreOnStartup = SipGatewayRestoreOnStartup,
            MessagesEnabled = SipMessagesEnabled, ForwardIncomingSms = SipForwardIncomingSms, AllowOutgoingSms = SipAllowOutgoingSms,
            Extensions = SipExtensions.Count == 0 ? null : SipExtensions.Select(row => row.Snapshot()).ToList(),
            DialRules = SipDialRules.Count == 0 ? null : SipDialRules.Select(row => row.Snapshot()).ToList(),
            LineRoutes = SnapshotSipModuleRoutes()
        };

        private Dictionary<string, SipGatewayLineRoute> SnapshotSipModuleRoutes() =>
            SipModuleRoutes.ToDictionary(row => row.SlotId, row => row.Snapshot(), StringComparer.OrdinalIgnoreCase);

        internal void RefreshSipModuleRoutesCore()
        {
            var selected = SelectedSipOutboundLine?.SlotId;
            var primaryLine = SipPrimaryPolicy.DefaultLineId;
            var extensionLines = SipExtensions.Select(row => (Row: row, Line: row.DefaultLineId)).ToArray();
            var ruleLines = SipDialRules.Select(row => (Row: row, Line: row.LineId)).ToArray();
            var slots = _kernel.Kernel.Pool.Slots;
            foreach (var slot in slots.Values)
                if (!SipModuleRoutes.Any(row => row.SlotId.Equals(slot.Id, StringComparison.OrdinalIgnoreCase)))
                    SipModuleRoutes.Add(new(slot.Id));
            foreach (var row in SipModuleRoutes) row.BindSlot(slots.GetValueOrDefault(row.SlotId));
            SipOutboundLineChoices.Clear();
            SipOutboundLineChoices.Add(new(null, "每次指定线路（X-VoWin-Line）"));
            foreach (var row in SipModuleRoutes) SipOutboundLineChoices.Add(new(row.SlotId, $"{row.Title} · {row.SlotId}"));
            if (selected is not null && !SipOutboundLineChoices.Any(choice => string.Equals(choice.SlotId, selected, StringComparison.OrdinalIgnoreCase)))
                SipOutboundLineChoices.Add(new(selected, $"缺失线路 {selected}（不会改用其他模组）"));
            SelectedSipOutboundLine = SipOutboundLineChoices.First(choice => string.Equals(choice.SlotId, selected, StringComparison.OrdinalIgnoreCase));
            // Clearing a bound ComboBox source can synchronously reset its
            // two-way SelectedValue. A refresh must not erase per-account routes.
            foreach (var item in extensionLines) item.Row.DefaultLineId = item.Line;
            SipPrimaryPolicy.DefaultLineId = primaryLine;
            foreach (var item in ruleLines) item.Row.LineId = item.Line;
        }

        private void ApplySipModuleRoutes(SipGatewayPreferences settings)
        {
            foreach (var row in SipModuleRoutes) row.BindSlot(null);
            SipModuleRoutes.Clear();
            foreach (var pair in settings.LineRoutes) SipModuleRoutes.Add(new(pair.Key, pair.Value));
            SelectedSipOutboundLine = new(settings.DefaultOutboundLineId, "");
            RefreshSipModuleRoutesCore();
        }

        [RelayCommand]
        private void RefreshSipModuleRoutes()
        {
            if (CanEditSipGateway) RefreshSipModuleRoutesCore();
        }

        internal async Task LoadSipSettingsAsync()
        {
            if (_sipSettingsLoaded) return;
            _sipSettingsLoaded = true;
            try
            {
                var settings = await _sipSettings.LoadAsync();
                SipGatewayUseTrunk = settings.UseTrunk;
                SipGatewayBindAddress = settings.BindAddress;
                SipGatewayPort = settings.Port;
                SipGatewayRealm = settings.Realm;
                SipGatewayExtension = settings.Extension;
                SipGatewayServerAddress = settings.ServerAddress;
                SipGatewayServerPort = settings.ServerPort;
                SipGatewayTrunkUsername = settings.TrunkUsername;
                SipGatewayIncomingTarget = settings.IncomingTarget;
                SipGatewayAllowOutboundCalls = settings.AllowOutboundCalls;
                SipGatewayRestoreOnStartup = settings.RestoreOnStartup;
                ApplySipModuleRoutes(settings);
                ApplySipServerSettings(settings);
                if (settings.RestoreOnStartup)
                {
                    ApplySipCredentials(await _sipSettings.LoadCredentialsAsync(settings));
                    if (!IsSipGatewayRunning) SipGatewayStatus = settings.UseTrunk &&
                        (string.IsNullOrEmpty(SipPeerInboundPassword) || string.IsNullOrEmpty(SipPeerOutboundPassword) || string.IsNullOrWhiteSpace(SipPeerExpectedRealm))
                        ? "已加载中继草稿；请补齐独立凭据与预期 Realm，再保存启用。旧共享密码不用于新认证。"
                        : "已加载记住的配置和密码 · 当前 SIP 关闭，可手动启用。";
                }
            }
            catch { SipGatewayStatus = "SIP 配置读取失败或凭据不匹配；已读设置保留供编辑，未自动启用。"; }
        }

        [RelayCommand]
        private async Task SaveSipGatewaySettingsAsync()
        {
            if (!CanEditSipGateway) return;
            try
            {
                RefreshSipModuleRoutesCore();
                await PersistSipConnectionAsync(enabled: false);
                SipGatewayStatus = SipGatewayRestoreOnStartup
                    ? "配置已记住，密码由当前 Windows 用户加密保护；SIP 当前关闭，下次启动也保持关闭。"
                    : "配置已保存，SIP 对接仍关闭；密码不记住，启动恢复已关闭。";
            }
            catch (Exception ex) { SipGatewayStatus = $"配置未保存：{ex.Message}"; }
        }

        [RelayCommand]
        private void DetectSipBindAddress()
        {
            if (!CanEditSipGateway) return;
            try
            {
                if (!IPAddress.TryParse(SipGatewayServerAddress.Trim(), out var server) || SipGatewayServerPort is < 1 or > 65535)
                    throw new InvalidOperationException("请先填写有效的 SIP 服务器 IP 和端口。");
                using var route = new UdpClient(server.AddressFamily);
                route.Connect(new IPEndPoint(server, SipGatewayServerPort)); // Select route only: no packet is sent.
                SipGatewayBindAddress = ((IPEndPoint)route.Client.LocalEndPoint!).Address.ToString();
                SipGatewayStatus = "已选择到服务器的本机网卡地址；SIP 仍关闭，请同步核对服务器的 Windows 地址。";
            }
            catch (Exception ex) { SipGatewayStatus = $"无法选择网卡地址：{ex.Message}"; }
        }

        [RelayCommand]
        private async Task ImportSipDeploymentAsync()
        {
            if (!CanEditSipGateway) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "VoWin Asterisk 部署配置|*.json", Title = "导入并记住 Asterisk 配置（当前 Windows 用户加密保护）" };
            if (dialog.ShowDialog() != true) return;
            try { await LoadSipDeploymentAsync(dialog.FileName); }
            catch (Exception ex) { SipGatewayStatus = $"部署配置未导入：{ex.Message}"; }
        }

        internal async Task LoadSipDeploymentAsync(string path)
        {
            if (!CanEditSipGateway) throw new InvalidOperationException("请先关闭 SIP 对接，再导入配置。");
            var profile = await SipGatewayDeploymentProfile.ReadAsync(path);
            var settings = profile.Settings;
            SipGatewayUseTrunk = settings.UseTrunk; SipGatewayBindAddress = settings.BindAddress; SipGatewayPort = settings.Port;
            SipGatewayExtension = settings.Extension;
            SipGatewayRealm = settings.Realm; SipGatewayServerAddress = settings.ServerAddress; SipGatewayServerPort = settings.ServerPort;
            SipGatewayTrunkUsername = settings.TrunkUsername; SipGatewayIncomingTarget = settings.IncomingTarget;
            SipGatewayAllowOutboundCalls = settings.AllowOutboundCalls;
            SipGatewayRestoreOnStartup = true;
            ApplySipModuleRoutes(settings);
            ApplySipServerSettings(settings);
            ApplySipCredentials(profile.Credentials);
            ResetAllSipRecipientIdentities(); // Never trust a deployment file's old receiver incarnation.
            await PersistSipConnectionAsync(enabled: false);
            SipGatewayStatus = settings.UseTrunk && settings.CredentialVersion < 2
                ? "已导入旧中继草稿，尚未启用；请补齐独立凭据与预期 Realm。原文件含密码，请私密保存。"
                : settings.UseTrunk && (string.IsNullOrEmpty(profile.Credentials.PeerInboundPassword) || string.IsNullOrEmpty(profile.Credentials.PeerOutboundPassword))
                    ? "已导入草稿，尚未启用；请补齐中继独立密码。"
                    : "已导入并加密保存，尚未启用 SIP。原部署文件含凭据，请私密保存。";
        }

        private void RefreshSipGatewayStatus()
        {
            var status = _kernel.GetSipGatewayStatus();
            IsSipGatewayRunning = status?.IsRunning == true;
            SipRegisteredDevices.Clear();
            foreach (var binding in status?.Registrations ?? [])
                SipRegisteredDevices.Add($"{binding.Extension} · {binding.RemoteEndPoint} · 到期 {binding.ExpiresAt.ToLocalTime():HH:mm:ss}");
            SipGatewayStatus = IsSipGatewayRunning
                ? status!.PeerEndPoint is not null
                    ? $"已启用 · 监听 {status.LocalEndPoint} · 本地 {status.Registrations.Count} 台 · 中继已配置（未验证）"
                    : $"已启用 · 监听 {status.LocalEndPoint} · 本地 {status.Registrations.Count} 台"
                : "已关闭 · 不监听 SIP，不影响 Host IMS / 模组语音桥接 / VoWiFi。";
            if (IsSipGatewayRunning)
                SipGatewayStatus += status!.MessagesEnabled
                    ? " · 消息已启用"
                    : " · 消息关闭";
        }

        [RelayCommand]
        private void RefreshSipGateway() => RefreshSipGatewayStatus();

        partial void OnIsSipGatewayRunningChanged(bool value)
        { OnPropertyChanged(nameof(SipGatewayActionText)); OnPropertyChanged(nameof(CanEditSipGateway)); OnPropertyChanged(nameof(SipStatusIndicator)); }
        partial void OnIsSipGatewayBusyChanged(bool value)
        { OnPropertyChanged(nameof(CanEditSipGateway)); OnPropertyChanged(nameof(CanToggleSipGateway)); OnPropertyChanged(nameof(SipStatusIndicator)); }
        partial void OnSipGatewayUseTrunkChanged(bool value)
        { OnPropertyChanged(nameof(SipTrunkVisibility)); OnPropertyChanged(nameof(SipDirectVisibility)); }

        private static string GetDefaultRecordingDirectory() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Recordings");
    }
}
