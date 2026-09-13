using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VoSharp.Euicc.Models;
using System.Windows;
using System.Windows.Media;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.VoWifi;
using VoWin.Helpers;
using VoWin.Models;
using VoWin.Services;
using Wpf.Ui.Controls;
using ActivationCodeModel = VoSharp.Euicc.Models.EuiccActivationCode;

namespace VoWin.ViewModels.Pages
{
    public partial class ModemManagerViewModel : ObservableObject
    {
        private readonly IVoKernelService _kernelService;
        private int _refreshSummaryScheduled;

        // Semantic brushes resolve from the live theme so status cards update
        // consistently when the user switches between light and dark mode.
        public static Brush TokenPrimary => ThemeBrushes.Accent;
        public static Brush TokenPrimaryBg => ThemeBrushes.Get("AppAccentSoftBrush", Color.FromRgb(0xE7, 0xF0, 0xFF));
        public static Brush TokenSuccess => ThemeBrushes.Success;
        public static Brush TokenSuccessBg => ThemeBrushes.Get("AppSuccessSoftBrush", Color.FromRgb(0xE7, 0xF8, 0xF2));
        public static Brush TokenWarning => ThemeBrushes.Warning;
        public static Brush TokenWarningBg => ThemeBrushes.Get("AppWarningSoftBrush", Color.FromRgb(0xFF, 0xF5, 0xE6));
        public static Brush TokenDanger => ThemeBrushes.Danger;
        public static Brush TokenDangerBg => ThemeBrushes.Get("AppDangerSoftBrush", Color.FromRgb(0xFF, 0xF0, 0xF3));
        public static Brush TokenMuted => ThemeBrushes.Muted;
        public static Brush TokenMutedBg => ThemeBrushes.Get("AppInputBrush", Color.FromRgb(0xF8, 0xFB, 0xFF));
        private CancellationTokenSource? _flightModeChangeCts;
        private bool _isLoadingPreferences;
        private int _preferenceLoadVersion;

        public ModemManagerViewModel ViewModel => this;
        public ObservableCollection<ModemSlot> Slots => _kernelService.Slots;

        [ObservableProperty]
        private ModemSlot? _selectedSlot;

        [ObservableProperty]
        private string _newPortName = "COM6";

        [ObservableProperty]
        private int _newBaudRate = 115200;

        [ObservableProperty]
        private string _newSlotName = "EC25 4G Modem";

        [ObservableProperty]
        private string _newProxyUrl = "";

        [ObservableProperty]
        private string _atCommandInput = "ATI";

        [ObservableProperty]
        private ObservableCollection<string> _atLogEntries = new();

        [ObservableProperty]
        private string _ussdCommandInput = "*100#";

        [ObservableProperty]
        private ObservableCollection<string> _ussdLogEntries = new();

        [ObservableProperty]
        private bool _isScanning;

        [ObservableProperty]
        private bool _isBusy;

        public bool IsOperationRunning => IsScanning || IsBusy;

        partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(IsOperationRunning));
        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(IsOperationRunning));
            DownloadEuiccProfileCommand.NotifyCanExecuteChanged();
        }

        [ObservableProperty]
        private string _euiccEid = "未知 / 未检测到 eUICC";

        [ObservableProperty]
        private ObservableCollection<Profile> _euiccProfiles = new();

        [ObservableProperty]
        private Profile? _selectedProfile;

        [ObservableProperty]
        private string _newProfileNickname = string.Empty;

        [ObservableProperty]
        private string _euiccActivationCode = string.Empty;

        [ObservableProperty]
        private string _euiccConfirmationCode = string.Empty;

        [ObservableProperty]
        private string _euiccQrFileName = "未选择二维码图片";

        [ObservableProperty]
        private bool _isEuiccDownloading;

        [ObservableProperty]
        private int _euiccDownloadProgress;

        [ObservableProperty]
        private string _euiccDownloadStatus = "等待输入激活链接或选择二维码";

        [ObservableProperty]
        private string _euiccDownloadPhase = "尚未开始";

        [ObservableProperty]
        private bool _hasEuiccDownloadActivity;

        [ObservableProperty]
        private bool _hasEuiccDownloadError;

        [ObservableProperty]
        private string _euiccDownloadFailureDetails = string.Empty;

        [ObservableProperty]
        private bool _allowUntrustedEuiccTls;

        [ObservableProperty]
        private bool _allowUncertainEuiccRetry;

        public ObservableCollection<string> EuiccDownloadLog { get; } = new();

        private CancellationTokenSource? _euiccDownloadCts;
        private readonly HashSet<string> _blockedEuiccActivationFingerprints = new(StringComparer.Ordinal);
        private string _lastEuiccProgressEntry = string.Empty;

        partial void OnEuiccActivationCodeChanged(string value) => DownloadEuiccProfileCommand.NotifyCanExecuteChanged();
        partial void OnAllowUncertainEuiccRetryChanged(bool value) => DownloadEuiccProfileCommand.NotifyCanExecuteChanged();

        partial void OnIsEuiccDownloadingChanged(bool value)
        {
            DownloadEuiccProfileCommand.NotifyCanExecuteChanged();
            CancelEuiccDownloadCommand.NotifyCanExecuteChanged();
        }

        partial void OnEuiccDownloadProgressChanged(int value) => CancelEuiccDownloadCommand.NotifyCanExecuteChanged();

        [ObservableProperty]
        private string _statusMessage = "就绪";

        // Module SQLite preferences
        [ObservableProperty]
        private bool _moduleFlightMode;

        partial void OnModuleFlightModeChanged(bool value)
        {
            var slot = SelectedSlot;
            if (!_isLoadingPreferences && slot != null && slot.IsFlightMode != value)
            {
                _flightModeChangeCts?.Cancel();
                _flightModeChangeCts = new CancellationTokenSource();
                _ = ApplyFlightModeAsync(slot, value, _flightModeChangeCts.Token);
            }
        }

        private async Task ApplyFlightModeAsync(ModemSlot slot, bool enabled, CancellationToken token)
        {
            try
            {
                StatusMessage = enabled
                    ? $"正在将卡槽 [{slot.Name}] 切换为飞行模式..."
                    : $"正在将卡槽 [{slot.Name}] 退出飞行模式...";
                await slot.SetFlightModeAsync(enabled);
                token.ThrowIfCancellationRequested();
                StatusMessage = enabled
                    ? $"[{slot.Name}] 已进入飞行模式。"
                    : $"[{slot.Name}] 已退出飞行模式并刷新网络。";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                StatusMessage = $"切换飞行模式失败: {ex.Message}";
                if (ReferenceEquals(SelectedSlot, slot))
                {
                    _isLoadingPreferences = true;
                    ModuleFlightMode = slot.IsFlightMode;
                    _isLoadingPreferences = false;
                }
            }
        }

        [ObservableProperty]
        private bool _moduleVoWifi;

        [ObservableProperty]
        private string _moduleName = string.Empty;

        [ObservableProperty]
        private bool _moduleCellularData;

        [ObservableProperty]
        private bool _moduleDataRoaming;

        [ObservableProperty]
        private string _moduleProxyUrl = string.Empty;

        // SIM SQLite preferences
        [ObservableProperty]
        private string _simCardNickname = string.Empty;

        [ObservableProperty]
        private bool _simFlightMode;

        [ObservableProperty]
        private bool _simVoWifi;

        [ObservableProperty]
        private bool _simCellularData;

        [ObservableProperty]
        private bool _simDataRoaming;

        [ObservableProperty]
        private string _simProxyUrl = string.Empty;

        [ObservableProperty]
        private string _preferenceStatusMessage = "SQLite 偏好就绪";

        public ModemManagerViewModel(IVoKernelService kernelService)
        {
            _kernelService = kernelService;
            SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();

            AtLogEntries.Add($"[{DateTime.Now:HH:mm:ss}] AT 交互终端就绪。输入指令并点击发送。");
            UssdLogEntries.Add($"[{DateTime.Now:HH:mm:ss}] USSD 会话终端就绪。输入例如 *100# 查询话费/余额。");

            if (SelectedSlot != null)
            {
                _ = LoadPreferencesForSlotAsync(SelectedSlot);
            }

            Slots.CollectionChanged += (s, e) =>
            {
                if (SelectedSlot == null && Slots.Count > 0)
                {
                    SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();
                }
            };

            // Hook state changes to refresh summary and status cards
            _kernelService.Kernel.TelephonyStateChanged += (s, e) => RefreshSummaryProperties();
            _kernelService.Kernel.VoWifiStateChanged += (s, e) => RefreshSummaryProperties();
            _kernelService.Kernel.SignalQualityChanged += (s, e) => RefreshSummaryProperties();
            _kernelService.Kernel.NetworkRegistrationChanged += (s, e) => RefreshSummaryProperties();
            ThemeBrushes.ThemeResourcesRefreshed += RefreshSummaryProperties;
            _kernelService.Kernel.SimStateChanged += (s, e) =>
            {
                RefreshSummaryProperties();
                var slot = SelectedSlot;
                if (slot == null || (!string.IsNullOrEmpty(e.SlotId) &&
                    !string.Equals(e.SlotId, slot.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(SelectedSlot, slot))
                    {
                        _ = LoadPreferencesForSlotAsync(slot);
                    }
                }), System.Windows.Threading.DispatcherPriority.DataBind);
            };
            _kernelService.Kernel.FlightModeChanged += (s, e) => RefreshSummaryProperties();
        }

        [RelayCommand]
        private async Task ExecuteUssdAsync()
        {
            if (string.IsNullOrWhiteSpace(UssdCommandInput)) return;
            var code = UssdCommandInput.Trim();
            UssdLogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] >> USSD: {code}");
            try
            {
                var resp = await _kernelService.SendUssdAsync(code, SelectedSlot?.Id);
                UssdLogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] << {resp}");
            }
            catch (Exception ex)
            {
                UssdLogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] !! {ex.Message}");
                StatusMessage = $"USSD 执行失败: {ex.Message}";
            }
            TrimTerminalLog(UssdLogEntries);
        }

        [RelayCommand]
        private void SendQuickUssd(string code)
        {
            UssdCommandInput = code;
            _ = ExecuteUssdAsync();
        }

        [RelayCommand]
        private void ClearUssdHistory()
        {
            UssdLogEntries.Clear();
            UssdLogEntries.Add($"[{DateTime.Now:HH:mm:ss}] USSD 交互记录已清空。");
        }

        [RelayCommand]
        private async Task RebootModemAsync()
        {
            if (SelectedSlot == null || IsBusy) return;

            var result = System.Windows.MessageBox.Show(
                $"确认重启模组 [{SelectedSlot.Name}]？\n\n此操作将发送 AT+CFUN=1,1 软重启射频基带，该过程会中断当前蜂窝网络数据连接、VoWiFi 隧道与正在进行的通话。",
                "确认重启模组",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            IsBusy = true;
            StatusMessage = $"正在重启模组 [{SelectedSlot.Name}]...";
            try
            {
                await _kernelService.ExecuteAtCommandAsync("AT+CFUN=1,1", SelectedSlot.Id);
                StatusMessage = $"模组 [{SelectedSlot.Name}] 重启指令已发送。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"重启模组失败: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void CopyToClipboard(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "--") return;
            try
            {
                Clipboard.SetText(text.Trim());
                StatusMessage = $"已复制: {text}";
                AppToast.ShowCopySuccess(text.Trim());
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task ExecuteVoWifiActionAsync()
        {
            if (SelectedSlot == null || IsBusy) return;

            if (IsVoWifiRunning || SelectedSlotVoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering)
            {
                await StopVoWifiAsync();
            }
            else
            {
                await StartVoWifiAsync();
            }
        }

        [RelayCommand]
        private async Task StartVoWifiAsync()
        {
            if (SelectedSlot == null || IsBusy) return;
            IsBusy = true;
            StatusMessage = $"正在启动 [{SelectedSlot.Name}] 的 VoWiFi 隧道...";
            try
            {
                // Start through the application service so the effective per-SIM,
                // per-slot or MCC country route is applied to the kernel first.
                // Calling ModemSlot directly bypasses country routing and silently
                // leaves the IKE/ESP session on direct UDP.
                bool ok = await _kernelService.StartVoWifiAsync(SelectedSlot.Id);
                StatusMessage = ok ? "VoWiFi 隧道建立成功！" : "VoWiFi 隧道建立失败。";
                OnPropertyChanged(nameof(SelectedSlot));
            }
            catch (Exception ex)
            {
                StatusMessage = $"VoWiFi 启动异常: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task StopVoWifiAsync()
        {
            if (SelectedSlot == null || IsBusy) return;
            IsBusy = true;
            StatusMessage = $"正在断开 [{SelectedSlot.Name}] 的 VoWiFi 隧道...";
            try
            {
                await _kernelService.StopVoWifiAsync(SelectedSlot.Id);
                StatusMessage = "VoWiFi 隧道已断开。";
                OnPropertyChanged(nameof(SelectedSlot));
            }
            catch (Exception ex)
            {
                StatusMessage = $"VoWiFi 断开异常: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ReconnectVoWifiAsync()
        {
            if (SelectedSlot == null || IsBusy) return;
            IsBusy = true;
            StatusMessage = $"正在重连 [{SelectedSlot.Name}] 的 VoWiFi 隧道...";
            try
            {
                await _kernelService.StopVoWifiAsync(SelectedSlot.Id);
                await Task.Delay(500);
                bool ok = await _kernelService.StartVoWifiAsync(SelectedSlot.Id);
                StatusMessage = ok ? "VoWiFi 隧道重连成功！" : "VoWiFi 隧道重连失败。";
                OnPropertyChanged(nameof(SelectedSlot));
            }
            catch (Exception ex)
            {
                StatusMessage = $"VoWiFi 重连异常: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        partial void OnSelectedSlotChanged(ModemSlot? value)
        {
            DownloadEuiccProfileCommand.NotifyCanExecuteChanged();
            RefreshSummaryProperties();
            if (value != null)
            {
                _ = LoadPreferencesForSlotAsync(value);
            }
        }

        private async Task LoadPreferencesForSlotAsync(ModemSlot slot)
        {
            var loadVersion = Interlocked.Increment(ref _preferenceLoadVersion);
            try
            {
                _isLoadingPreferences = true;

                var modPref = await _kernelService.Preferences.GetModulePreferenceAsync(slot.Id, slot.Imei);
                if (loadVersion != Volatile.Read(ref _preferenceLoadVersion)) return;
                if (modPref != null)
                {
                    ModuleName = string.IsNullOrWhiteSpace(modPref.CustomName) ? slot.Name : modPref.CustomName;
                    ModuleFlightMode = modPref.DefaultFlightMode;
                    ModuleVoWifi = modPref.DefaultVoWifi;
                    ModuleCellularData = modPref.DefaultCellularData;
                    ModuleDataRoaming = modPref.DefaultDataRoaming;
                    ModuleProxyUrl = modPref.DefaultProxyUrl ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(modPref.CustomName))
                    {
                        slot.Name = modPref.CustomName.Trim();
                    }
                }
                else
                {
                    ModuleName = slot.Name;
                    ModuleFlightMode = slot.IsFlightMode;
                    ModuleVoWifi = false;
                    ModuleCellularData = false;
                    ModuleDataRoaming = false;
                    ModuleProxyUrl = slot.ProxyUrl ?? string.Empty;
                }

                if (slot.Sim != null && !string.IsNullOrEmpty(slot.Sim.Iccid))
                {
                    var simPref = await _kernelService.Preferences.GetSimPreferenceAsync(slot.Sim.Iccid);
                    if (loadVersion != Volatile.Read(ref _preferenceLoadVersion)) return;
                    if (simPref != null)
                    {
                        SimCardNickname = simPref.CardNickname ?? string.Empty;
                        SimFlightMode = simPref.DefaultFlightMode;
                        SimVoWifi = simPref.DefaultVoWifi;
                        SimCellularData = simPref.DefaultCellularData;
                        SimDataRoaming = simPref.DefaultDataRoaming;
                        SimProxyUrl = simPref.DedicatedProxyUrl ?? string.Empty;
                        slot.CardNickname = simPref.CardNickname;
                    }
                    else
                    {
                        SimCardNickname = string.Empty;
                        SimFlightMode = false;
                        SimVoWifi = false;
                        SimCellularData = false;
                        SimDataRoaming = false;
                        SimProxyUrl = string.Empty;
                        slot.CardNickname = null;
                    }
                }
                else
                {
                    SimCardNickname = string.Empty;
                    SimFlightMode = false;
                    SimVoWifi = false;
                    SimCellularData = false;
                    SimDataRoaming = false;
                    SimProxyUrl = string.Empty;
                    slot.CardNickname = null;
                }
                PreferenceStatusMessage = $"已同步 [{slot.Name}] 的 SQLite 偏好。";
            }
            catch (Exception ex)
            {
                PreferenceStatusMessage = $"加载偏好异常: {ex.Message}";
            }
            finally
            {
                if (loadVersion == Volatile.Read(ref _preferenceLoadVersion))
                {
                    _isLoadingPreferences = false;
                }
            }
        }

        [RelayCommand]
        private async Task SaveModulePreferenceAsync()
        {
            if (SelectedSlot == null) return;
            try
            {
                await _kernelService.SaveModulePreferencesAsync(
                    SelectedSlot.Id,
                    ModuleFlightMode,
                    ModuleVoWifi,
                    ModuleCellularData,
                    ModuleDataRoaming,
                    string.IsNullOrWhiteSpace(ModuleProxyUrl) ? null : ModuleProxyUrl.Trim(),
                    string.IsNullOrWhiteSpace(ModuleName) ? null : ModuleName.Trim()
                );
                PreferenceStatusMessage = $"模块 [{SelectedSlot.Name}] 偏好已持久化至 SQLite。";
                StatusMessage = PreferenceStatusMessage;
            }
            catch (Exception ex)
            {
                PreferenceStatusMessage = $"保存模块偏好失败: {ex.Message}";
                StatusMessage = PreferenceStatusMessage;
            }
        }

        [RelayCommand]
        private async Task SaveSimPreferenceAsync()
        {
            if (SelectedSlot?.Sim == null || string.IsNullOrEmpty(SelectedSlot.Sim.Iccid))
            {
                PreferenceStatusMessage = "当前卡槽未插入或未识别 SIM 卡 ICCID。";
                StatusMessage = PreferenceStatusMessage;
                return;
            }

            try
            {
                await _kernelService.SaveSimPreferencesAsync(
                    SelectedSlot.Sim.Iccid,
                    SimFlightMode,
                    SimVoWifi,
                    SimCellularData,
                    SimDataRoaming,
                    string.IsNullOrWhiteSpace(SimProxyUrl) ? null : SimProxyUrl.Trim(),
                    string.IsNullOrWhiteSpace(SimCardNickname) ? null : SimCardNickname.Trim());
                PreferenceStatusMessage = $"SIM 卡 [{SelectedSlot.Sim.Iccid}] 偏好已持久化至 SQLite。";
                StatusMessage = PreferenceStatusMessage;
            }
            catch (Exception ex)
            {
                PreferenceStatusMessage = $"保存 SIM 偏好失败: {ex.Message}";
                StatusMessage = PreferenceStatusMessage;
            }
        }

        [RelayCommand]
        private async Task SaveAllPreferencesAsync()
        {
            await SaveModulePreferenceAsync();
            if (PreferenceStatusMessage.StartsWith("保存模块偏好失败", StringComparison.Ordinal)) return;

            if (SelectedSlot?.Sim == null || string.IsNullOrEmpty(SelectedSlot.Sim.Iccid))
            {
                PreferenceStatusMessage = "模组偏好已保存；当前未识别到 SIM，未写入卡片偏好。";
                StatusMessage = PreferenceStatusMessage;
                return;
            }

            await SaveSimPreferenceAsync();
            if (PreferenceStatusMessage.StartsWith("保存 SIM 偏好失败", StringComparison.Ordinal)) return;
            PreferenceStatusMessage = "模组与当前 SIM 卡偏好已成功保存至 SQLite。";
            StatusMessage = PreferenceStatusMessage;
        }

        [RelayCommand]
        private async Task ExitFlightModeAsync()
        {
            if (SelectedSlot == null) return;
            StatusMessage = $"正在将 [{SelectedSlot.Name}] 退出飞行模式...";
            await SelectedSlot.SetFlightModeAsync(false);
            ModuleFlightMode = false;
            StatusMessage = $"[{SelectedSlot.Name}] 已退出飞行模式并唤醒 SIM 芯片。";
        }

        [RelayCommand]
        private async Task DiscoverSlotsAsync()
        {
            if (IsScanning || IsBusy) return;
            IsScanning = true;
            StatusMessage = "正在自动扫描串口设备...";
            try
            {
                // Must run on background thread so it doesn't block WPF UI Dispatcher 
                // while opening serial ports synchronously.
                var found = await _kernelService.DiscoverSlotsAsync();

                StatusMessage = $"扫描完成，发现 {found.Count} 个可用卡槽设备。";
                if (SelectedSlot == null && Slots.Count > 0)
                {
                    SelectedSlot = Slots.First();
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"扫描异常: {ex.Message}";
            }
            finally
            {
                IsScanning = false;
            }
        }

        [RelayCommand]
        private async Task AddSlotAsync()
        {
            if (IsBusy) return;
            if (string.IsNullOrWhiteSpace(NewPortName))
            {
                StatusMessage = "请指定有效的 COM 串口名称 (如 COM6)";
                return;
            }

            IsBusy = true;
            StatusMessage = $"正在连接 {NewPortName}...";
            try
            {
                var slot = await _kernelService.AddSlotAsync(
                    NewPortName.Trim(),
                    NewBaudRate,
                    string.IsNullOrWhiteSpace(NewSlotName) ? null : NewSlotName.Trim(),
                    string.IsNullOrWhiteSpace(NewProxyUrl) ? null : NewProxyUrl.Trim());

                if (slot != null)
                {
                    SelectedSlot = slot;
                    StatusMessage = $"卡槽 {slot.Name} 登记成功。";
                }
                else
                {
                    StatusMessage = $"连接 {NewPortName} 失败，调制解调器无响应。";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"添加卡槽错误: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RemoveSlotAsync(string? slotId)
        {
            if (IsBusy) return;
            var id = slotId ?? SelectedSlot?.Id;
            if (string.IsNullOrEmpty(id)) return;

            IsBusy = true;
            try
            {
                bool ok = await _kernelService.RemoveSlotAsync(id);
                StatusMessage = ok ? $"卡槽 {id} 已成功移除。" : $"卡槽 {id} 移除失败。";
                SelectedSlot = Slots.FirstOrDefault();
            }
            catch (Exception ex)
            {
                StatusMessage = $"移除卡槽错误: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }


        [RelayCommand]
        private async Task RefreshSlotMetricsAsync(string? slotId)
        {
            if (IsBusy) return;
            var id = slotId ?? SelectedSlot?.Id;
            IsBusy = true;
            StatusMessage = "正在刷新信号强度与网络注册信息...";
            try
            {
                await _kernelService.RefreshMetricsAsync(id);
                StatusMessage = "卡槽指标刷新完毕。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"刷新指标错误: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ExecuteAtCommandAsync()
        {
            if (string.IsNullOrWhiteSpace(AtCommandInput)) return;
            var cmd = AtCommandInput.Trim();
            AtLogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] >> {cmd}");

            try
            {
                var resp = await _kernelService.ExecuteAtCommandAsync(cmd, SelectedSlot?.Id);
                AtLogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] << {resp}");
            }
            catch (Exception ex)
            {
                AtLogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] !! {ex.Message}");
                StatusMessage = $"AT 指令执行失败: {ex.Message}";
            }
            TrimTerminalLog(AtLogEntries);
        }

        private static void TrimTerminalLog(ObservableCollection<string> entries)
        {
            while (entries.Count > 500)
            {
                entries.RemoveAt(entries.Count - 1);
            }
        }

        [RelayCommand]
        private void SendQuickAt(string cmd)
        {
            AtCommandInput = cmd;
            _ = ExecuteAtCommandAsync();
        }

        [RelayCommand]
        private void ClearAtHistory()
        {
            AtLogEntries.Clear();
            AtLogEntries.Add($"[{DateTime.Now:HH:mm:ss}] 终端日志已清空。");
        }

        [RelayCommand]
        private async Task LoadEuiccDataAsync()
        {
            if (IsBusy) return;
            if (SelectedSlot == null)
            {
                StatusMessage = "请先选择一个设备。";
                return;
            }

            IsBusy = true;
            StatusMessage = $"正在读取 [{SelectedSlot.Name}] 的 eUICC / eSIM 芯片信息...";
            bool eidLoaded = false;
            try
            {
                var eid = await _kernelService.GetEuiccEidAsync(SelectedSlot.Id);
                if (!string.IsNullOrWhiteSpace(eid))
                {
                    EuiccEid = eid;
                    eidLoaded = true;
                }
            }
            catch
            {
                EuiccEid = "正在读取 Profiles...";
            }

            try
            {
                var profiles = await _kernelService.GetEuiccProfilesAsync(SelectedSlot.Id);
                EuiccProfiles.Clear();
                foreach (var p in profiles)
                {
                    EuiccProfiles.Add(p);
                }
                StatusMessage = $"eUICC 读取完成，共 {profiles.Count} 个 Profile。";
                if (!eidLoaded && profiles.Count > 0)
                {
                    EuiccEid = "已成功识别 eUICC 卡";
                }
            }
            catch (Exception ex)
            {
                if (!eidLoaded)
                {
                    StatusMessage = $"读取 eUICC 失败: {ex.Message}";
                    EuiccEid = "读取失败 / 未检测到 eUICC 应用";
                }
                else
                {
                    StatusMessage = $"已读取 EID，但 Profile 列表读取受限: {ex.Message}";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task SwitchEuiccProfileAsync(Profile? profile)
        {
            var target = profile ?? SelectedProfile;
            if (target == null) return;

            StatusMessage = $"正在启用 Profile: {target.ProfileName ?? target.ICCID}...";
            try
            {
                bool ok = await _kernelService.SwitchEuiccProfileAsync(target.ICCID, SelectedSlot?.Id);
                StatusMessage = ok
                    ? "Profile 切换成功，新卡 ICCID / IMSI / 号码已重新读取并校验。"
                    : "Profile 切换失败；VoWiFi 已保持停止。";
                await LoadEuiccDataAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"切换 Profile 失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task DisableEuiccProfileAsync(Profile? profile)
        {
            var target = profile ?? SelectedProfile;
            if (target == null) return;

            StatusMessage = $"正在禁用 Profile: {target.ProfileName ?? target.ICCID}...";
            try
            {
                bool ok = await _kernelService.DisableEuiccProfileAsync(target.ICCID, SelectedSlot?.Id);
                StatusMessage = ok ? "Profile 禁用成功。" : "Profile 禁用失败。";
                await LoadEuiccDataAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"禁用 Profile 失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task RenameEuiccProfileAsync()
        {
            if (SelectedProfile == null || string.IsNullOrWhiteSpace(NewProfileNickname))
            {
                StatusMessage = "请先选择 Profile 并输入新别名";
                return;
            }

            try
            {
                bool ok = await _kernelService.RenameEuiccProfileAsync(SelectedProfile.ICCID, NewProfileNickname.Trim(), SelectedSlot?.Id);
                StatusMessage = ok ? "Profile 别名修改成功！" : "修改别名失败。";
                await LoadEuiccDataAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"修改别名失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private void SelectEuiccQrImage()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 eSIM 激活二维码",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var decoded = EuiccQrCodeReader.Read(dialog.FileName);
                var parsed = ActivationCodeModel.Parse(decoded);
                EuiccActivationCode = parsed.CanonicalCode;
                EuiccQrFileName = Path.GetFileName(dialog.FileName);
                EuiccDownloadStatus = parsed.ConfirmationCodeRequired
                    ? $"二维码已验证，SM-DP+：{parsed.SmdpAddress}；请填写运营商确认码"
                    : $"二维码已验证，SM-DP+：{parsed.SmdpAddress}";
                StatusMessage = "已从二维码读取并验证 eSIM 激活码。";
            }
            catch (Exception ex)
            {
                EuiccQrFileName = "二维码读取失败";
                EuiccDownloadStatus = ex.Message;
                StatusMessage = $"无法读取 eSIM 二维码：{ex.Message}";
            }
        }

        private bool CanStartEuiccDownload()
            => !IsBusy &&
               !IsEuiccDownloading &&
               SelectedSlot != null &&
               !string.IsNullOrWhiteSpace(EuiccActivationCode) &&
               (!IsEuiccActivationBlocked(EuiccActivationCode) || AllowUncertainEuiccRetry);

        [RelayCommand(CanExecute = nameof(CanStartEuiccDownload))]
        private async Task DownloadEuiccProfileAsync()
        {
            var slot = SelectedSlot;
            if (slot == null) return;

            ActivationCodeModel parsed;
            try
            {
                parsed = ActivationCodeModel.Parse(EuiccActivationCode);
                EuiccActivationCode = parsed.CanonicalCode;
            }
            catch (Exception ex)
            {
                EuiccDownloadStatus = ex.Message;
                StatusMessage = $"eSIM 激活码无效：{ex.Message}";
                return;
            }

            var allowUntrustedTlsForThisDownload = AllowUntrustedEuiccTls;
            var allowAuthorizedRetryForThisDownload = AllowUncertainEuiccRetry;
            if (allowUntrustedTlsForThisDownload)
            {
                var confirmation = System.Windows.MessageBox.Show(
                    $"即将允许 {parsed.SmdpAddress} 使用 Windows 不信任的根证书。\n\n" +
                    "这可能让中间人截获或篡改一次性 eSIM 下载事务。该设置只对本次下载生效；域名不匹配、过期或撤销的证书仍会被拒绝。\n\n仍要继续吗？",
                    "危险：允许未受信任的根证书",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);
                if (confirmation != System.Windows.MessageBoxResult.Yes) return;
            }

            if (allowAuthorizedRetryForThisDownload)
            {
                var confirmation = System.Windows.MessageBox.Show(
                    "只有在已经重新读取卡片、确认没有新增 Profile，并且卡商明确允许重新安装时才能继续。\n\n" +
                    "继续会清除该激活码的 VoWin 本地事务锁定，并向 SM-DP+ 建立新的下载事务。仍要继续吗？",
                    "服务商授权重新安装",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);
                if (confirmation != System.Windows.MessageBoxResult.Yes) return;
            }

            _euiccDownloadCts?.Dispose();
            _euiccDownloadCts = new CancellationTokenSource();
            IsBusy = true;
            IsEuiccDownloading = true;
            HasEuiccDownloadActivity = true;
            HasEuiccDownloadError = false;
            EuiccDownloadFailureDetails = string.Empty;
            EuiccDownloadLog.Clear();
            _lastEuiccProgressEntry = string.Empty;
            RecordEuiccProgress(new EuiccDownloadProgress(1, "正在启动安全下载流程"));
            if (allowUntrustedTlsForThisDownload)
                AddEuiccLog($"警告 · 本次下载将允许 {parsed.SmdpAddress} 使用未受信任的根证书");
            if (allowAuthorizedRetryForThisDownload)
                AddEuiccLog("警告 · 服务商已授权重新安装，本次将清除匹配的本地事务锁定");
            StatusMessage = $"正在向 [{slot.Name}] 写入 eSIM Profile，请勿拔卡或断电。";

            var progress = new Progress<EuiccDownloadProgress>(RecordEuiccProgress);

            try
            {
                var result = await _kernelService.DownloadEuiccProfileAsync(
                    EuiccActivationCode,
                    EuiccConfirmationCode,
                    progress,
                    slot.Id,
                    _euiccDownloadCts.Token,
                    allowUntrustedTlsForThisDownload,
                    allowAuthorizedRetryForThisDownload);

                var profiles = await _kernelService.GetEuiccProfilesAsync(slot.Id);
                EuiccProfiles.Clear();
                foreach (var profile in profiles) EuiccProfiles.Add(profile);

                RecordEuiccProgress(new EuiccDownloadProgress(100, result.InstalledWithWarning
                    ? result.Warning ?? "Profile 已写入，但运营商确认有警告。"
                    : $"下载完成并已核验，ICCID：{result.Iccid}"));
                EuiccDownloadPhase = result.InstalledWithWarning ? "写入完成（有警告）" : "写入完成";
                StatusMessage = result.InstalledWithWarning
                    ? $"eSIM 已写入，需留意确认警告：{result.Warning}"
                    : $"eSIM Profile 已安全写入，ICCID：{result.Iccid}";

                EuiccActivationCode = string.Empty;
                EuiccConfirmationCode = string.Empty;
                EuiccQrFileName = "未选择二维码图片";
            }
            catch (OperationCanceledException)
            {
                BlockCurrentEuiccActivation();
                EuiccDownloadStatus = "下载被取消，事务结果需要重新核验；已禁止重复使用当前激活码";
                EuiccDownloadPhase = "操作已取消";
                SetEuiccFailure("用户取消了下载。请先重新读取 Profile，确认卡片状态后再继续。", "OperationCanceledException");
                StatusMessage = "eSIM 下载已取消。请先重新读取 Profile，不能直接再次使用同一二维码。";
            }
            catch (EuiccDownloadUncertainException ex)
            {
                BlockCurrentEuiccActivation();
                EuiccDownloadStatus = ex.Message;
                SetEuiccFailure(ex.Message, ex.GetType().Name);
                StatusMessage = "eSIM 写卡结果不确定，已锁定当前激活码以防重复消耗。请重新读取卡片。";
            }
            catch (Exception ex)
            {
                var details = FlattenExceptionMessages(ex);
                EuiccDownloadStatus = $"失败于 {GetEuiccPhase(EuiccDownloadProgress)}：{details}";
                SetEuiccFailure(details, ex.GetType().Name);
                StatusMessage = $"eSIM 下载失败（{EuiccDownloadProgress}% · {GetEuiccPhase(EuiccDownloadProgress)}）：{details}";
            }
            finally
            {
                IsEuiccDownloading = false;
                IsBusy = false;
                _euiccDownloadCts?.Dispose();
                _euiccDownloadCts = null;
                AllowUntrustedEuiccTls = false;
                AllowUncertainEuiccRetry = false;
            }
        }

        private bool CanCancelEuiccDownload()
            => IsEuiccDownloading && EuiccDownloadProgress < 30;

        [RelayCommand(CanExecute = nameof(CanCancelEuiccDownload))]
        private void CancelEuiccDownload()
        {
            EuiccDownloadStatus = "正在安全取消下载会话...";
            AddEuiccLog("正在请求安全取消；取消后会重新核验卡片状态");
            _euiccDownloadCts?.Cancel();
        }

        [RelayCommand]
        private void CopyEuiccDiagnostics()
        {
            if (string.IsNullOrWhiteSpace(EuiccDownloadFailureDetails)) return;
            Clipboard.SetText(EuiccDownloadFailureDetails);
            StatusMessage = "eSIM 失败诊断已复制到剪贴板。";
        }

        private void RecordEuiccProgress(EuiccDownloadProgress value)
        {
            EuiccDownloadProgress = Math.Clamp(value.Percent, 0, 100);
            EuiccDownloadPhase = GetEuiccPhase(EuiccDownloadProgress);
            EuiccDownloadStatus = value.Status;
            AddEuiccLog($"{EuiccDownloadProgress}% · {EuiccDownloadPhase} · {value.Status}");
            CancelEuiccDownloadCommand.NotifyCanExecuteChanged();
        }

        private void AddEuiccLog(string message)
        {
            if (string.Equals(message, _lastEuiccProgressEntry, StringComparison.Ordinal)) return;
            _lastEuiccProgressEntry = message;
            EuiccDownloadLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (EuiccDownloadLog.Count > 16) EuiccDownloadLog.RemoveAt(0);
        }

        private void SetEuiccFailure(string message, string exceptionType)
        {
            HasEuiccDownloadError = true;
            var phase = GetEuiccPhase(EuiccDownloadProgress);
            EuiccDownloadFailureDetails =
                $"失败位置：{EuiccDownloadProgress}% · {phase}\n" +
                $"错误信息：{message}\n" +
                $"诊断类型：{exceptionType}\n" +
                "处理建议：先点击“读取 eUICC 芯片”核对 Profile 列表；如果提示结果不确定，不要重复扫描或提交同一激活码。";
            AddEuiccLog($"失败 · {phase} · {message}");
        }

        private static string FlattenExceptionMessages(Exception exception)
        {
            var messages = new List<string>();
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                var message = current.Message.Trim();
                if (!string.IsNullOrEmpty(message) && !messages.Contains(message, StringComparer.Ordinal))
                    messages.Add(message);
            }
            return messages.Count == 0 ? "未返回具体错误信息" : string.Join(" → ", messages);
        }

        private static string GetEuiccPhase(int percent) => percent switch
        {
            < 5 => "准备下载",
            < 10 => "读取卡片并执行写入前检查",
            < 25 => "连接 eUICC 与 SM-DP+",
            < 40 => "SM-DP+ 下载认证",
            < 52 => "eUICC 验证服务器证书",
            < 65 => "请求 Profile 下载授权",
            < 70 => "下载 Profile 包",
            < 90 => "向 eUICC 写入 Profile",
            < 100 => "重新读取并核验写入结果",
            _ => "写入完成"
        };

        private void BlockCurrentEuiccActivation()
        {
            try
            {
                var canonical = ActivationCodeModel.Parse(EuiccActivationCode).CanonicalCode;
                _blockedEuiccActivationFingerprints.Add(ActivationFingerprint(canonical));
            }
            catch { }
            DownloadEuiccProfileCommand.NotifyCanExecuteChanged();
        }

        private bool IsEuiccActivationBlocked(string value)
        {
            try
            {
                var canonical = ActivationCodeModel.Parse(value).CanonicalCode;
                return _blockedEuiccActivationFingerprints.Contains(ActivationFingerprint(canonical));
            }
            catch
            {
                return false;
            }
        }

        private static string ActivationFingerprint(string canonicalCode)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalCode)));

        // ================= Device Health & Summary Properties =================
        public string SelectedSlotSignalEvaluation => SelectedSlotSignalBars switch
        {
            >= 5 => "极佳",
            4 => "良好",
            3 => "一般",
            2 => "较弱",
            1 => "微弱",
            _ => "无信号"
        };

        public string SelectedSlotSignalDbmText
        {
            get
            {
                var sig = SelectedSlot?.Signal;
                if (sig == null || sig.RssiRaw == 99 || sig.RssiDbm == 0 || sig.RssiDbm == 99) return "-- dBm";
                int dbm = sig.RssiDbm > 0 ? -sig.RssiDbm : sig.RssiDbm;
                return $"{dbm} dBm";
            }
        }

        public string SelectedSlotSignalSummary => $"{SelectedSlotSignalDbmText} ({SelectedSlotSignalEvaluation})";

        public int SelectedSlotSignalBars
        {
            get
            {
                var sig = SelectedSlot?.Signal;
                if (sig == null || sig.RssiRaw == 99) return 0;
                int dbm = sig.RssiDbm > 0 ? -sig.RssiDbm : sig.RssiDbm;
                if (dbm == 0 || dbm <= -113) return 0;

                return dbm switch
                {
                    >= -75 => 5,
                    >= -85 => 4,
                    >= -95 => 3,
                    >= -105 => 2,
                    > -113 => 1,
                    _ => 0
                };
            }
        }

        public VoWifiState SelectedSlotVoWifiState => SelectedSlot?.VoWifi?.State ?? VoWifiState.Disconnected;

        public string SelectedSlotVoWifiStateText => SelectedSlotVoWifiState switch
        {
            VoWifiState.ImsRegistered => "VoWiFi 已连接",
            VoWifiState.IpsecTunnelEstablished => "IPsec 隧道就绪",
            VoWifiState.ImsRegistering => "IMS 注册中...",
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka => "连接中...",
            VoWifiState.Failed => "VoWiFi 异常",
            _ => "VoWiFi 未连接"
        };

        public Brush SelectedSlotVoWifiBrush => SelectedSlotVoWifiState switch
        {
            VoWifiState.ImsRegistered => TokenSuccess,
            VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering or VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka => TokenPrimary,
            VoWifiState.Failed => TokenDanger,
            _ => TokenMuted
        };

        public Brush SelectedSlotVoWifiBg => SelectedSlotVoWifiState switch
        {
            VoWifiState.ImsRegistered => TokenSuccessBg,
            VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering or VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka => TokenPrimaryBg,
            VoWifiState.Failed => TokenDangerBg,
            _ => TokenMutedBg
        };

        public string SelectedSlotSimStatusText => !string.IsNullOrEmpty(SelectedSlot?.Sim?.Imsi) ? "SIM Ready" : "未插卡";
        public Brush SelectedSlotSimBrush => !string.IsNullOrEmpty(SelectedSlot?.Sim?.Imsi) ? TokenSuccess : TokenMuted;
        public Brush SelectedSlotSimBg => !string.IsNullOrEmpty(SelectedSlot?.Sim?.Imsi) ? TokenSuccessBg : TokenMutedBg;

        public string SelectedSlotRatText => SelectedSlot?.IsFlightMode == true
            ? "射频关闭"
            : (!string.IsNullOrEmpty(SelectedSlot?.Signal?.Rat) ? SelectedSlot.Signal.Rat : "--");
        public string SelectedSlotRegStatusText => SelectedSlot?.IsFlightMode == true
            ? "飞行模式（未搜索网络）"
            : (SelectedSlot?.Registration?.StatusDisplay ?? "网络状态未知");

        public string SelectedSlotCountryDisplay
        {
            get
            {
                return CarrierDisplayHelper.GetCountryDisplay(SelectedSlot?.Sim);
            }
        }

        public string SelectedSlotOperatorDisplay =>
            CarrierDisplayHelper.GetOperatorDisplay(SelectedSlot?.Sim);

        public string SelectedSlotPlmnDisplay =>
            !string.IsNullOrWhiteSpace(SelectedSlot?.Sim?.Mcc) ? $"{SelectedSlot.Sim.Mcc}-{SelectedSlot.Sim.Mnc}" : "--";

        // ================= 4-Stage State Machine Logic =================
        public bool Stage1Success =>
            (SelectedSlot?.VoWifiDiag != null && !string.IsNullOrEmpty(SelectedSlot.VoWifiDiag.EpdgIp)) ||
            (SelectedSlotVoWifiState >= VoWifiState.ConnectingIkev2 && SelectedSlotVoWifiState != VoWifiState.Failed && SelectedSlotVoWifiState != VoWifiState.Disconnected);

        public bool Stage2Success =>
            SelectedSlotVoWifiState >= VoWifiState.IpsecTunnelEstablished && SelectedSlotVoWifiState != VoWifiState.Failed && SelectedSlotVoWifiState != VoWifiState.Disconnected;

        public bool Stage3Success =>
            (SelectedSlot?.VoWifiDiag?.Tunnel != null) ||
            (SelectedSlotVoWifiState >= VoWifiState.IpsecTunnelEstablished && SelectedSlotVoWifiState != VoWifiState.Failed && SelectedSlotVoWifiState != VoWifiState.Disconnected);

        public bool Stage4Success => SelectedSlotVoWifiState == VoWifiState.ImsRegistered;

        public Brush Stage1Brush => Stage1Success ? TokenSuccess : (SelectedSlotVoWifiState == VoWifiState.ResolvingEpdg ? TokenPrimary : (SelectedSlotVoWifiState == VoWifiState.Failed ? TokenDanger : TokenMuted));
        public Brush Stage1Bg => Stage1Success ? TokenSuccessBg : (SelectedSlotVoWifiState == VoWifiState.ResolvingEpdg ? TokenPrimaryBg : (SelectedSlotVoWifiState == VoWifiState.Failed ? TokenDangerBg : TokenMutedBg));
        public SymbolRegular Stage1Symbol => Stage1Success ? SymbolRegular.Checkmark16 : (SelectedSlotVoWifiState == VoWifiState.ResolvingEpdg ? SymbolRegular.ArrowSync16 : (SelectedSlotVoWifiState == VoWifiState.Failed ? SymbolRegular.Dismiss16 : SymbolRegular.Circle16));
        public string Stage1StatusText => Stage1Success ? "已解析" : (SelectedSlotVoWifiState == VoWifiState.ResolvingEpdg ? "解析中" : (SelectedSlotVoWifiState == VoWifiState.Failed ? "失败" : "待执行"));

        public Brush Stage2Brush => Stage2Success ? TokenSuccess : (SelectedSlotVoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka ? TokenPrimary : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage1Success ? TokenDanger : TokenMuted));
        public Brush Stage2Bg => Stage2Success ? TokenSuccessBg : (SelectedSlotVoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka ? TokenPrimaryBg : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage1Success ? TokenDangerBg : TokenMutedBg));
        public SymbolRegular Stage2Symbol => Stage2Success ? SymbolRegular.Checkmark16 : (SelectedSlotVoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka ? SymbolRegular.ArrowSync16 : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage1Success ? SymbolRegular.Dismiss16 : SymbolRegular.Circle16));
        public string Stage2StatusText => Stage2Success ? "鉴权通过" : (SelectedSlotVoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka ? "协商中" : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage1Success ? "失败" : "待执行"));

        public Brush Stage3Brush => Stage3Success ? TokenSuccess : (SelectedSlotVoWifiState == VoWifiState.ConnectingIkev2 ? TokenPrimary : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage2Success ? TokenDanger : TokenMuted));
        public Brush Stage3Bg => Stage3Success ? TokenSuccessBg : (SelectedSlotVoWifiState == VoWifiState.ConnectingIkev2 ? TokenPrimaryBg : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage2Success ? TokenDangerBg : TokenMutedBg));
        public SymbolRegular Stage3Symbol => Stage3Success ? SymbolRegular.Checkmark16 : (SelectedSlotVoWifiState == VoWifiState.ConnectingIkev2 ? SymbolRegular.ArrowSync16 : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage2Success ? SymbolRegular.Dismiss16 : SymbolRegular.Circle16));
        public string Stage3StatusText => Stage3Success ? "隧道就绪" : (SelectedSlotVoWifiState == VoWifiState.ConnectingIkev2 ? "建立中" : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage2Success ? "失败" : "待执行"));

        public Brush Stage4Brush => Stage4Success ? TokenSuccess : (SelectedSlotVoWifiState == VoWifiState.ImsRegistering ? TokenPrimary : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage3Success ? TokenDanger : TokenMuted));
        public Brush Stage4Bg => Stage4Success ? TokenSuccessBg : (SelectedSlotVoWifiState == VoWifiState.ImsRegistering ? TokenPrimaryBg : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage3Success ? TokenDangerBg : TokenMutedBg));
        public SymbolRegular Stage4Symbol => Stage4Success ? SymbolRegular.Checkmark16 : (SelectedSlotVoWifiState == VoWifiState.ImsRegistering ? SymbolRegular.ArrowSync16 : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage3Success ? SymbolRegular.Dismiss16 : SymbolRegular.Circle16));
        public string Stage4StatusText => Stage4Success ? "IMS就绪" : (SelectedSlotVoWifiState == VoWifiState.ImsRegistering ? "注册中" : (SelectedSlotVoWifiState == VoWifiState.Failed && Stage3Success ? "失败" : "待执行"));

        public bool HasImsError => SelectedSlotVoWifiState == VoWifiState.Failed || !string.IsNullOrEmpty(SelectedSlot?.VoWifiDiag?.LastError);
        public string ImsErrorTitle => !string.IsNullOrEmpty(SelectedSlot?.VoWifiDiag?.LastError)
            ? SelectedSlot.VoWifiDiag.LastError
            : (Stage3Success ? "IMS 核心网注册失败 (403 Forbidden / Timeout)" : (Stage1Success ? "IKEv2 SA / EAP-AKA 鉴权失败" : "ePDG 域名解析失败"));
        public string ImsErrorDetail => !string.IsNullOrEmpty(SelectedSlot?.VoWifiDiag?.LastError)
            ? "核心网反馈连接故障，请检查 SIM 卡密钥配置或当前网络路由。"
            : (Stage3Success
                ? "SIP REGISTER 响应异常。请检查 SIM 卡 Ki/OPc 鉴权密钥、VoWiFi 业务开通状态或重试连接。"
                : (Stage1Success
                    ? "未能通过与 ePDG 的 EAP-AKA 身份鉴权，请检查 SIM 卡鉴权向量与接入点 APN。"
                    : "无法通过 DNS 解析运营商 ePDG 域名，请检查 Wi-Fi 网络连通性或当前代理设置。"));

        // Single Dynamic Primary Action Button
        public bool IsVoWifiRunning => SelectedSlotVoWifiState is VoWifiState.ImsRegistered or VoWifiState.IpsecTunnelEstablished;

        public string VoWifiActionText => IsBusy ? "处理中..." : (SelectedSlotVoWifiState switch
        {
            VoWifiState.ImsRegistered or VoWifiState.IpsecTunnelEstablished => "断开连接",
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => "取消连接",
            VoWifiState.Failed => "重新连接",
            _ => "连接 VoWiFi"
        });

        public string VoWifiActionAppearance => SelectedSlotVoWifiState switch
        {
            VoWifiState.ImsRegistered or VoWifiState.IpsecTunnelEstablished => "Danger",
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => "Secondary",
            _ => "Primary"
        };

        public SymbolRegular VoWifiActionIcon => SelectedSlotVoWifiState switch
        {
            VoWifiState.ImsRegistered or VoWifiState.IpsecTunnelEstablished => SymbolRegular.Stop24,
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => SymbolRegular.Dismiss24,
            VoWifiState.Failed => SymbolRegular.ArrowClockwise24,
            _ => SymbolRegular.Play24
        };

        private void RefreshSummaryProperties()
        {
            if (Interlocked.Exchange(ref _refreshSummaryScheduled, 1) != 0) return;

            App.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    OnPropertyChanged(nameof(SelectedSlot));
                    OnPropertyChanged(nameof(SelectedSlotSignalDbmText));
                    OnPropertyChanged(nameof(SelectedSlotSignalEvaluation));
                    OnPropertyChanged(nameof(SelectedSlotSignalSummary));
                    OnPropertyChanged(nameof(SelectedSlotSignalBars));
                    OnPropertyChanged(nameof(SelectedSlotVoWifiState));
                    OnPropertyChanged(nameof(SelectedSlotVoWifiStateText));
                    OnPropertyChanged(nameof(SelectedSlotVoWifiBrush));
                    OnPropertyChanged(nameof(SelectedSlotVoWifiBg));
                    OnPropertyChanged(nameof(SelectedSlotSimStatusText));
                    OnPropertyChanged(nameof(SelectedSlotSimBrush));
                    OnPropertyChanged(nameof(SelectedSlotSimBg));
                    OnPropertyChanged(nameof(SelectedSlotRatText));
                    OnPropertyChanged(nameof(SelectedSlotRegStatusText));
                    OnPropertyChanged(nameof(SelectedSlotCountryDisplay));
                    OnPropertyChanged(nameof(SelectedSlotOperatorDisplay));
                    OnPropertyChanged(nameof(SelectedSlotPlmnDisplay));
                    OnPropertyChanged(nameof(VoWifiActionText));
                    OnPropertyChanged(nameof(VoWifiActionAppearance));
                    OnPropertyChanged(nameof(VoWifiActionIcon));
                    OnPropertyChanged(nameof(IsVoWifiRunning));
                    OnPropertyChanged(nameof(Stage1Success));
                    OnPropertyChanged(nameof(Stage2Success));
                    OnPropertyChanged(nameof(Stage3Success));
                    OnPropertyChanged(nameof(Stage4Success));
                    OnPropertyChanged(nameof(Stage1Brush));
                    OnPropertyChanged(nameof(Stage2Brush));
                    OnPropertyChanged(nameof(Stage3Brush));
                    OnPropertyChanged(nameof(Stage4Brush));
                    OnPropertyChanged(nameof(Stage1Bg));
                    OnPropertyChanged(nameof(Stage2Bg));
                    OnPropertyChanged(nameof(Stage3Bg));
                    OnPropertyChanged(nameof(Stage4Bg));
                    OnPropertyChanged(nameof(Stage1Symbol));
                    OnPropertyChanged(nameof(Stage2Symbol));
                    OnPropertyChanged(nameof(Stage3Symbol));
                    OnPropertyChanged(nameof(Stage4Symbol));
                    OnPropertyChanged(nameof(Stage1StatusText));
                    OnPropertyChanged(nameof(Stage2StatusText));
                    OnPropertyChanged(nameof(Stage3StatusText));
                    OnPropertyChanged(nameof(Stage4StatusText));
                    OnPropertyChanged(nameof(HasImsError));
                    OnPropertyChanged(nameof(ImsErrorTitle));
                    OnPropertyChanged(nameof(ImsErrorDetail));
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshSummaryScheduled, 0);
                }
            }), System.Windows.Threading.DispatcherPriority.DataBind);
        }
    }
}
