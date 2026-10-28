using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using VoSharp.Euicc.Models;
using VoSharp.Ike.Transport;
using VoSharp.Kernel;
using VoSharp.Kernel.Events;
using VoSharp.Kernel.Pool;
using VoSharp.Kernel.SipGateway;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using VoWin.Helpers;
using VoWin.Models;

namespace VoWin.Services
{
    public partial class VoKernelService : IVoKernelService, INotifyPropertyChanged, ICellularSipMediaProvider
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isInitialScanRunning = true;
        public bool IsInitialScanRunning
        {
            get => _isInitialScanRunning;
            private set
            {
                if (_isInitialScanRunning == value) return;
                _isInitialScanRunning = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInitialScanRunning)));
            }
        }

        private string _initialScanStatus = "正在准备设备扫描…";
        public string InitialScanStatus
        {
            get => _initialScanStatus;
            private set
            {
                if (_initialScanStatus == value) return;
                _initialScanStatus = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InitialScanStatus)));
            }
        }

        private readonly ConcurrentDictionary<string, byte> _autoVoWifiStarts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingDialCancellations = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _voWifiIntentVersions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _voWifiDesiredStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _profileSwitchTasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _runtimePreferenceGate = new(1, 1);
        public IVoKernel Kernel { get; }

        public ObservableCollection<ModemSlot> Slots { get; } = new();
        public ModemSlot? ActiveSlot => Kernel.GetActiveSlot();
        public ObservableCollection<SmsConversationModel> Conversations { get; } = new();
        public ObservableCollection<CallRecordModel> CallHistory { get; } = new();
        public ObservableCollection<ProxyNodeModel> ProxyPresets { get; } = new();
        public ObservableCollection<IccidRouteModel> IccidRoutes { get; } = new();
        public ObservableCollection<CountryRouteModel> CountryRoutes { get; } = new();
        public ObservableCollection<LogEntryModel> Logs { get; } = new();

        public TelephonyState StateMachineState => Kernel.StateMachine.CurrentState;
        public SignalQuality? CurrentSignal => Kernel.Pool.ActiveSlot?.Signal;
        public NetworkRegistration? CurrentRegistration => Kernel.Pool.ActiveSlot?.Registration;
        public SimIdentity? CurrentSim => Kernel.CurrentSim;
        public VoWifiDiagnosticInfo? VoWifiDiag
        {
            get
            {
                try
                {
                    return Kernel.GetVoWifiDiagnostics();
                }
                catch
                {
                    return null;
                }
            }
        }
        public CallSession? ActiveCall => Kernel.ActiveCall;
        public CallState CurrentCallState { get; private set; } = CallState.Idle;
        public string? CurrentCallNumber { get; private set; }
        public bool HasIncomingCall { get; private set; }
        public string? IncomingCallerNumber { get; private set; }
        public bool IsPresentedCallEvent(string callId) =>
            !string.IsNullOrWhiteSpace(callId) &&
            string.Equals(_presentedCallId, callId, StringComparison.OrdinalIgnoreCase);
        public event Action<string>? CallMediaStatusChanged;
        public event Action<SmsMessageModel>? IncomingSmsReceived;
        public event Action<string, string?>? IncomingCallReceived;
        public event Action? EgressRoutesChanged;

        public IPreferenceDatabaseService Preferences { get; }
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource? _usbDebounceCts;
        private readonly object _usbDebounceGate = new();
        private readonly ConcurrentQueue<LogEntryModel> _pendingUiLogs = new();
        private readonly Channel<string> _logLines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        private readonly CancellationTokenSource _logWriterCts = new();
        private readonly Task _logWriterTask;
        private readonly SemaphoreSlim _slotDiscoveryGate = new(1, 1);
        private readonly CancellationTokenSource _discoveryShutdown = new();
        private int _logDrainScheduled;
        private int _slotSyncScheduled;
        private int _slotSyncRequestVersion;

        // Microphone capture
        private NAudio.Wave.WaveIn? _waveIn;
        private DesktopMicrophoneSession? _microphoneSession;
        private readonly CallAlerting _callAlerting = new();
        private readonly CallExperienceSettingsStore _callExperienceStore = new();
        private string? _desktopAudioCallId;
        private string? _desktopAudioSlotId;
        private enum DesktopAudioKind { None, Microphone, CellularUsb }
        private DesktopAudioKind _desktopAudioKind;
        private string? _presentedCallId;
        private string? _shownIncomingCallId;
        private readonly ConcurrentDictionary<string, (string Number, DateTime Started, CallDirection Direction)> _callEventMetadata = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CallConnectedEventArgs> _connectedCallEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _recordedCallEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _desktopAudioTransition = new(1, 1);
        private CancellationTokenSource? _incomingAutoAnswerCts;
        private readonly SemaphoreSlim _incomingAnswerGate = new(1, 1);
        private CallExperienceSettings _callExperience = new();
        private readonly AutoAnswerMessageRequests _autoAnswerMessages = new();
        private CancellationTokenSource? _autoAnswerPlaybackCts;
        private Task? _autoAnswerPlaybackTask;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _syncingSlots = new();
        private readonly ConcurrentDictionary<string, CellularSipReservation> _cellularSipReservations = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _cellularSipReservationGate = new();

        private static string CallEventKey(string? slotId, string callId) => $"{slotId ?? "fallback"}\0{callId}";

        private void PresentIncomingCall(string callId, string callerNumber, string? slotId)
        {
            _presentedCallId = callId;
            HasIncomingCall = true;
            IncomingCallerNumber = callerNumber;
            CurrentCallNumber = callerNumber;
            CurrentCallState = CallState.Incoming;
            if (Kernel.LocalSipGateway?.RoutesIncomingCall(slotId) == true)
            {
                AddLog("INFO", "SipGateway", "Incoming call rings on this desktop and SIP endpoints; first pickup owns the call.");
            }
            if (string.Equals(_shownIncomingCallId, callId, StringComparison.OrdinalIgnoreCase)) return;
            _shownIncomingCallId = callId;

            var slotName = !string.IsNullOrEmpty(slotId)
                ? Slots.FirstOrDefault(slot => slot.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))?.Name
                : null;
            Views.Windows.IncomingCallFloatingWindow.ShowIncoming(
                callerNumber,
                slotName ?? ActiveSlot?.Name ?? "SIM",
                onAnswer: async () =>
                {
                    await AnswerAsync(slotId);
                    TrayManager.RestoreMainWindow();
                },
                onReject: async () => { await RejectAsync(slotId); },
                simultaneousSip: Kernel.LocalSipGateway?.RoutesIncomingCall(slotId) == true);
            StartIncomingAlertingAndAutoAnswer(slotId);
        }

        private bool TryClaimDesktopAudio(string callId, string? slotId, DesktopAudioKind kind)
        {
            lock (_cellularSipReservationGate)
            {
                if (kind == DesktopAudioKind.CellularUsb && IsReserved(slotId))
                    return false;
                if (_desktopAudioCallId != null)
                    return false;
                _desktopAudioCallId = callId;
                _desktopAudioSlotId = slotId;
                _desktopAudioKind = kind;
                return true;
            }
        }

        private DesktopAudioKind ReleaseDesktopAudioClaim(string callId, string? slotId)
        {
            lock (_cellularSipReservationGate)
            {
                if (!string.Equals(_desktopAudioCallId, callId, StringComparison.OrdinalIgnoreCase) ||
                    (_desktopAudioSlotId != null && slotId != null &&
                     !string.Equals(_desktopAudioSlotId, slotId, StringComparison.OrdinalIgnoreCase)))
                    return DesktopAudioKind.None;
                var kind = _desktopAudioKind;
                _desktopAudioCallId = null;
                _desktopAudioSlotId = null;
                _desktopAudioKind = DesktopAudioKind.None;
                return kind;
            }
        }

        private void ReconcileRemovedCallSlot(string slotId)
        {
            _autoAnswerMessages.RemoveSlot(slotId);
            Task audioCleanup = Task.CompletedTask;
            string? audioCallId;
            lock (_cellularSipReservationGate)
                audioCallId = string.Equals(_desktopAudioSlotId, slotId, StringComparison.OrdinalIgnoreCase)
                    ? _desktopAudioCallId : null;
            if (audioCallId != null)
            {
                var kind = ReleaseDesktopAudioClaim(audioCallId, slotId);
                if (kind == DesktopAudioKind.CellularUsb)
                {
                    audioCleanup = StopDesktopCellularAudioAsync(slotId, audioCallId);
                }
                else if (kind == DesktopAudioKind.Microphone)
                    StopMicrophone();
            }
            if (IsReserved(slotId)) audioCleanup = Task.WhenAll(audioCleanup, ReleaseAsync(slotId));

            foreach (var entry in _callEventMetadata.Where(item =>
                         item.Key.StartsWith(slotId + "\0", StringComparison.OrdinalIgnoreCase)))
            {
                if (!_callEventMetadata.TryRemove(entry.Key, out var call)) continue;
                _connectedCallEvents.TryRemove(entry.Key, out _);
                if (_recordedCallEvents.TryAdd(entry.Key, DateTime.UtcNow))
                    AddCallRecord(call.Number, call.Direction, CallState.Ended,
                        call.Started, DateTime.UtcNow - call.Started, slotId: slotId);
            }

            var next = Kernel.ActiveCall;
            if (_presentedCallId == null || string.Equals(_presentedCallId, next?.CallId, StringComparison.OrdinalIgnoreCase)) return;
            StopIncomingAlerting();
            _callAlerting.StopRingback();
            if (next == null)
            {
                CurrentCallState = CallState.Ended;
                HasIncomingCall = false;
                _shownIncomingCallId = null;
                Views.Windows.IncomingCallFloatingWindow.Dismiss();
                Views.Windows.InCallFloatingWindow.Dismiss();
                return;
            }
            if (next.State == CallState.Incoming)
                PresentIncomingCall(next.CallId, next.RemoteNumber, Kernel.ActiveCallSlotId);
            else
            {
                _presentedCallId = next.CallId;
                CurrentCallState = next.State;
                CurrentCallNumber = next.RemoteNumber;
                HasIncomingCall = false;
                _shownIncomingCallId = null;
                Views.Windows.IncomingCallFloatingWindow.Dismiss();
            }
            if (next.State == CallState.Active &&
                _connectedCallEvents.TryGetValue(CallEventKey(Kernel.ActiveCallSlotId, next.CallId), out var connected))
                _ = ResumePromotedCallAudioAsync(audioCleanup, connected);
        }

        private readonly string? _diagnosticLogPath;
        private readonly Action<SmsMessageModel, string?> _smsNotificationPresenter;

        public VoKernelService(IPreferenceDatabaseService? preferences = null, TelephonySettingsStore? telephonySettingsStore = null,
            string? diagnosticLogPath = null, Action<SmsMessageModel, string?>? smsNotificationPresenter = null,
            Dispatcher? uiDispatcher = null)
        {
            _diagnosticLogPath = diagnosticLogPath;
            _smsNotificationPresenter = smsNotificationPresenter ?? ((item, slotName) =>
            {
                try { SoundEffectService.Instance.PlaySmsChime(); }
                catch (Exception ex) { AddLog("WARN", "SMS", $"Notification sound failed ({ex.GetType().Name})."); }
                Views.Windows.IncomingSmsFloatingWindow.ShowNotification(item, slotName);
            });
            var diagnosticDirectory = string.IsNullOrWhiteSpace(diagnosticLogPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Logs", "Diagnostics")
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(diagnosticLogPath))!, "Diagnostics");
            _diagnosticRecorder = new DiagnosticRecorder(diagnosticDirectory);
            _dispatcher = uiDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            Preferences = preferences ?? new PreferenceDatabaseService();
            if (telephonySettingsStore != null) _telephonySettingsStore = telephonySettingsStore;
            var ikeRetry = new IkeRetryGate(Preferences as IIkeRetryJournalStore ?? new UnavailableIkeRetryJournalStore(), new WindowsIkeRetryClock());
            Kernel = new VoKernel(ikeRetry: ikeRetry);
            Kernel.CellularSipMediaProvider = this;
            _logWriterTask = RunLogWriterAsync(_logWriterCts.Token);

            RegisterKernelEvents();
            _diagnosticSampler = SampleDiagnosticsAsync(_diagnosticSamplerCts.Token);
        }

        private static readonly HashSet<string> LegacyBundledCountryCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "CN", "US", "GB", "HK", "JP", "SG"
        };

        /// <summary>
        /// Removes only the built-in examples that older releases wrote to the
        /// database.  User-created entries are intentionally retained.  A
        /// profile which pointed at one of the examples is removed as well so a
        /// no-config installation cannot silently regain a localhost proxy.
        /// </summary>
        private async Task RemoveLegacyBundledEgressDefaultsAsync(
            List<ProxyNodeModel> savedNodes,
            List<CountryRouteModel> savedRoutes)
        {
            var bundledNodes = savedNodes.Where(IsLegacyBundledProxy).ToList();
            var bundledNodeIds = bundledNodes
                .Select(node => node.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var bundledRoutes = savedRoutes
                .Where(route =>
                    (route.IsDirect && LegacyBundledCountryCodes.Contains(route.CountryCode)) ||
                    (!string.IsNullOrWhiteSpace(route.ProxyNodeId) && bundledNodeIds.Contains(route.ProxyNodeId)))
                .ToList();

            foreach (var route in bundledRoutes)
            {
                await Preferences.DeleteCountryRouteAsync(route.CountryCode);
                savedRoutes.Remove(route);
            }

            foreach (var node in bundledNodes)
            {
                await Preferences.DeleteProxyNodeAsync(node.Id);
                savedNodes.Remove(node);
            }

            if (bundledNodes.Count != 0 || bundledRoutes.Count != 0)
            {
                AddLog("INFO", "Proxy",
                    $"已清理旧版本内置代理/分流项：{bundledNodes.Count} 个代理节点，{bundledRoutes.Count} 条路由。未配置时将保持直连。");
            }
        }

        private static bool IsLegacyBundledProxy(ProxyNodeModel node)
        {
            if (!string.IsNullOrWhiteSpace(node.Username) || !string.IsNullOrWhiteSpace(node.Password) ||
                !node.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return (node.Name.Equals("本地 Clash / v2ray (SOCKS5)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("socks5", StringComparison.OrdinalIgnoreCase) && node.Port == 7890) ||
                   (node.Name.Equals("通用本地 SOCKS5 (1080)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("socks5", StringComparison.OrdinalIgnoreCase) && node.Port == 1080) ||
                   (node.Name.Equals("本地 HTTP 代理 (7890)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && node.Port == 7890);
        }

        public async Task InitializeAsync()
        {
            IsInitialScanRunning = true;
            InitialScanStatus = "正在加载本地设置…";
            AddLog("INFO", "VoKernelService", "Initializing VoSharp core engine & SQLite database...");

            try
            {
                await Preferences.InitializeAsync();
                _telephonySettings = await _telephonySettingsStore.LoadAsync();
                Kernel.Pool.ControlPreference = _telephonySettings.ControlPreference;
                _diagnosticRecorder.SetEnabled(_telephonySettings.DetailedDiagnosticsEnabled);
                PropertyChanged?.Invoke(this, new(nameof(DetailedDiagnosticsEnabled)));
                RecordDiagnosticState("Application initialization");
                _callExperience = await _callExperienceStore.LoadAsync();
                ApplyVolteAudioPrewarmPreference();
                ApplyCallRecordingPreference();

                // 1. Load Call Records from SQLite
                var callHistory = await Preferences.GetCallRecordsAsync(300);
                await RunOnUiAsync(() =>
                {
                    CallHistory.Clear();
                    foreach (var call in callHistory)
                    {
                        CallHistory.Add(call);
                    }
                });

                // 2. Load SMS Messages & Reconstruct Conversations
                var allSms = await Preferences.GetAllSmsMessagesAsync();
                await RunOnUiAsync(() =>
                {
                    Conversations.Clear();
                    var grouped = allSms.GroupBy(m => m.SenderOrRecipient.Trim(), StringComparer.OrdinalIgnoreCase);
                    string[] colors = { "#0078D4", "#107C10", "#D13438", "#8764B8", "#FF8C00", "#008272", "#E3008C" };
                    var convList = new List<SmsConversationModel>();

                    foreach (var g in grouped)
                    {
                        var clean = g.Key;
                        int hash = Math.Abs(clean.GetHashCode());
                        var letter = clean.Length > 0 ? (char.IsLetter(clean[0]) ? clean[0].ToString().ToUpper() : clean[^1].ToString()) : "#";
                        var conv = new SmsConversationModel
                        {
                            ContactNumber = clean,
                            AvatarLetter = letter,
                            AvatarColor = colors[hash % colors.Length]
                        };
                        foreach (var m in g.OrderBy(x => x.Timestamp))
                        {
                            conv.Messages.Add(m);
                            if (!m.IsOutgoing && m.DeliveryState == SmsDeliveryState.Received)
                            {
                                conv.UnreadCount++;
                            }
                        }
                        conv.UpdateLastMessage();
                        convList.Add(conv);
                    }

                    foreach (var conv in convList.OrderByDescending(c => c.LastMessageTime))
                    {
                        Conversations.Add(conv);
                    }
                });

                // 3. Load only user-created proxy nodes and country routes.  No
                // local proxy or country route is assumed by default: an empty
                // configuration must remain direct and never inherit a TUN's
                // incidental localhost listener.
                var savedNodes = (await Preferences.GetAllProxyNodesAsync()).ToList();
                var savedRoutes = (await Preferences.GetAllCountryRoutesAsync()).ToList();
                var savedIccidRoutes = (await Preferences.GetAllIccidRoutesAsync()).ToList();
                await RemoveLegacyBundledEgressDefaultsAsync(savedNodes, savedRoutes);

                // Older releases stored a per-SIM proxy URL inside SimPreferences.
                // Promote it once into the explicit ICCID routing tier so existing
                // users keep their route and can now see/edit the rule in one place.
                var legacySimPreferences = await Preferences.GetAllSimPreferencesAsync();
                foreach (var simPreference in legacySimPreferences.Where(preference =>
                             !string.IsNullOrWhiteSpace(preference.Iccid) &&
                             !string.IsNullOrWhiteSpace(preference.DedicatedProxyUrl)))
                {
                    if (savedIccidRoutes.Any(route => string.Equals(route.Iccid, simPreference.Iccid, StringComparison.Ordinal)))
                        continue;

                    var legacyUrl = simPreference.DedicatedProxyUrl!.Trim();
                    var matchingNode = savedNodes.FirstOrDefault(node =>
                        string.Equals(node.ToProxyUrl(), legacyUrl, StringComparison.OrdinalIgnoreCase));
                    var migrated = new IccidRouteModel
                    {
                        Iccid = simPreference.Iccid.Trim(),
                        CardName = simPreference.CardNickname,
                        ImsiSnapshot = simPreference.Imsi,
                        PhoneNumber = null,
                        ProxyNodeId = matchingNode?.Id,
                        ProxyUrl = matchingNode == null ? legacyUrl : null,
                        ProxyNodeName = matchingNode?.Name ?? DescribeProxyEndpoint(legacyUrl),
                        UpdatedAt = DateTime.Now
                    };
                    savedIccidRoutes.Add(migrated);
                    await Preferences.SaveIccidRouteAsync(migrated);
                }

                if (savedNodes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        ProxyPresets.Clear();
                        foreach (var n in savedNodes) ProxyPresets.Add(n);
                    });
                }

                if (savedRoutes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        CountryRoutes.Clear();
                        foreach (var r in savedRoutes) CountryRoutes.Add(r);
                    });
                }

                if (savedIccidRoutes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        IccidRoutes.Clear();
                        foreach (var route in savedIccidRoutes) IccidRoutes.Add(route);
                    });
                }

                AddLog("INFO", "Preferences", $"SQLite initialized ({callHistory.Count} calls, {allSms.Count} messages loaded)");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Preferences", $"SQLite initialization error: {ex.Message}");
            }

            await RunOnUiAsync(SyncSlots);

            await RestoreSipGatewayOnStartupAsync().ConfigureAwait(false);

            AddLog("INFO", "VoKernelService", "Starting initial Modem scan...");
            InitialScanStatus = "正在并发扫描串口设备…";
            try
            {
                var ports = SerialPort.GetPortNames();
                AddLog("INFO", "VoKernelService", $"Ports: [{(ports.Length > 0 ? string.Join(", ", ports) : "None")}]");

                var found = _telephonySettings.AutoDiscoverModems
                    ? await DiscoverSlotsCoreAsync() : Array.Empty<ModemSlot>();
                await RunOnUiAsync(SyncSlots);
                AddLog("INFO", "VoKernelService", $"Scan finished, found {found.Count} slots");

                // The eUICC probe is read-only and happens before restoring
                // auto-VoWiFi.  This both makes eSIM support visible at launch
                // and avoids competing APDU traffic with an automatic IMS start.
                InitialScanStatus = "正在识别 eSIM 芯片…";
                foreach (var slot in found)
                {
                    await ProbeEuiccAsync(slot.Id).ConfigureAwait(false);
                }

                InitialScanStatus = "正在恢复模块设置…";
                foreach (var slot in found)
                {
                    await ApplyPreferencesToSlotAsync(slot);
                }

                // Standard USB Audio Class modems never need ADB. Only probe the
                // compatibility runtime when the modem explicitly identifies as
                // QDC507; otherwise even looking for adb.exe is unwanted work.
                if (found.Any(slot => slot.Modem?.FirmwareRevision?.Contains(
                        "QDC507", StringComparison.OrdinalIgnoreCase) == true))
                    _ = PrepareQdc507VoiceRuntimeAsync();

                if (Slots.Count == 0)
                {
                    AddLog("INFO", "VoKernelService", "No Modem found");
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "VoKernelService", $"Scan failed: {ex.Message}");
            }
            finally
            {
                InitialScanStatus = "扫描完成";
                IsInitialScanRunning = false;
            }
        }

        private void RegisterKernelEvents()
        {
            Kernel.SipIncomingCallClaimed += (_, pickup) => PostToUi(() =>
            {
                if (!string.Equals(Kernel.ActiveCall?.CallId, pickup.CallId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Kernel.ActiveCallSlotId, pickup.SlotId, StringComparison.OrdinalIgnoreCase)) return;
                StopIncomingAlerting();
                if (!pickup.IsLocal)
                {
                    HasIncomingCall = false;
                    Views.Windows.IncomingCallFloatingWindow.Dismiss();
                }
                AddLog("INFO", "SipGateway", pickup.IsLocal
                    ? "Desktop accepted the incoming call; SIP ringing branches are being cancelled."
                    : "A SIP endpoint accepted the incoming call; desktop ringing and automatic answer stopped.");
            });
            // Call Events
            Kernel.CallStateChanged += (s, e) =>
            {
                if (e.NewState is not (CallState.Ended or CallState.Idle))
                {
                    var key = CallEventKey(e.SlotId, e.CallId);
                    if (!_recordedCallEvents.ContainsKey(key))
                        _callEventMetadata.TryAdd(key,
                            (e.TargetNumber, DateTime.UtcNow, e.IsOutgoing ? CallDirection.Outgoing : CallDirection.Incoming));
                }
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (e.NewState is CallState.Ended or CallState.Idle)
                    {
                        // CallEnded owns final presentation and recording. A
                        // different slot's state transition must not dismiss
                        // the call currently shown on the desktop.
                        return;
                    }
                    if (!string.Equals(Kernel.ActiveCall?.CallId, e.CallId, StringComparison.OrdinalIgnoreCase)) return;
                    _presentedCallId = e.CallId;
                    CurrentCallState = e.NewState;
                    CurrentCallNumber = e.TargetNumber;

                    if (e.NewState == CallState.Ringing && e.IsOutgoing)
                    {
                        _callAlerting.StartRingback();
                    }
                    else if (e.IsOutgoing && e.OldState == CallState.Ringing)
                    {
                        _callAlerting.StopRingback();
                    }

                    if (e.NewState == CallState.Active)
                    {
                        HasIncomingCall = false;
                        _shownIncomingCallId = null;
                        Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    }

                    AddLog("INFO", "Calls", $"Call on slot {e.SlotId ?? "fallback"} state changed -> {e.NewState}");
                }));
            };

            Kernel.IncomingCall += (s, e) =>
            {
                var key = CallEventKey(e.SlotId, e.CallId);
                if (!_recordedCallEvents.ContainsKey(key))
                    _callEventMetadata.TryAdd(key, (e.CallerNumber, e.Timestamp, CallDirection.Incoming));
                try { IncomingCallReceived?.Invoke(e.CallerNumber, e.SlotId); } catch { }
                PostToUi(() =>
                {
                    if (!string.Equals(Kernel.ActiveCall?.CallId, e.CallId, StringComparison.OrdinalIgnoreCase)) return;
                    AddLog("INFO", "Calls", $"Incoming call on slot {e.SlotId ?? "fallback"}; caller omitted from diagnostics.");
                    PresentIncomingCall(e.CallId, e.CallerNumber, e.SlotId);
                });
            };

            Kernel.CallConnected += async (s, e) =>
            {
                var key = CallEventKey(e.SlotId, e.CallId);
                if (_recordedCallEvents.ContainsKey(key)) return;
                _callEventMetadata.TryAdd(key, (e.TargetNumber, e.ConnectedAt, CallDirection.Outgoing));
                _connectedCallEvents[key] = e;
                if (!string.Equals(Kernel.ActiveCall?.CallId, e.CallId, StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("INFO", "Calls", $"Call {e.CallId} connected on a background slot; desktop audio remains with the presented call.");
                    return;
                }
                PostToUi(() =>
                {
                    if (!string.Equals(Kernel.ActiveCall?.CallId, e.CallId, StringComparison.OrdinalIgnoreCase)) return;
                    _presentedCallId = e.CallId;
                    StopIncomingAlerting();
                    CurrentCallState = CallState.Active;
                    HasIncomingCall = false;
                    _shownIncomingCallId = null;
                    Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    AddLog("INFO", "Calls", $"Call connected on slot {e.SlotId ?? "fallback"} (Codec: {e.Codec ?? "Default"})");
                });

                await EnsureDesktopMediaForCallAsync(e);
            };

            Kernel.CallEnded += (s, e) =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    Task audioCleanup = Task.CompletedTask;
                    var desktopAudioKind = ReleaseDesktopAudioClaim(e.CallId, e.SlotId);
                    if (desktopAudioKind == DesktopAudioKind.CellularUsb)
                    {
                        audioCleanup = StopDesktopCellularAudioAsync(e.SlotId, e.CallId);
                    }
                    else if (desktopAudioKind == DesktopAudioKind.Microphone)
                    {
                        StopMicrophone();
                    }
                    else if (IsReserved(e.SlotId))
                    {
                        // The SIP gateway owns this UAC endpoint. Release only
                        // its reservation, never a different desktop call.
                        audioCleanup = ReleaseAsync(e.SlotId);
                    }

                    var key = CallEventKey(e.SlotId, e.CallId);
                    _autoAnswerMessages.Remove(e.CallId, e.SlotId);
                    _connectedCallEvents.TryRemove(key, out _);
                    var hasMetadata = _callEventMetadata.TryRemove(key, out var metadata);
                    var dur = e.Duration ?? (hasMetadata ? e.EndedAt - metadata.Started : TimeSpan.Zero);
                    if (_recordedCallEvents.TryAdd(key, e.EndedAt))
                        AddCallRecord(e.TargetNumber,
                            hasMetadata ? metadata.Direction : CallDirection.Outgoing,
                            CallState.Ended,
                            hasMetadata ? metadata.Started : e.EndedAt - dur,
                            dur, null, e.SlotId, e.WavRecordingPath);
                    if (_recordedCallEvents.Count > 4096)
                        foreach (var old in _recordedCallEvents.Where(item => item.Value < DateTime.UtcNow.AddDays(-1)))
                            _recordedCallEvents.TryRemove(old.Key, out _);

                    if (string.Equals(_presentedCallId, e.CallId, StringComparison.OrdinalIgnoreCase))
                    {
                        StopIncomingAlerting();
                        _callAlerting.StopRingback();
                        var next = Kernel.ActiveCall;
                        if (next == null)
                        {
                            CurrentCallState = CallState.Ended;
                            CurrentCallNumber = e.TargetNumber;
                            HasIncomingCall = false;
                            _shownIncomingCallId = null;
                            Views.Windows.IncomingCallFloatingWindow.Dismiss();
                            Views.Windows.InCallFloatingWindow.Dismiss();
                        }
                        else if (next.State == CallState.Incoming)
                        {
                            PresentIncomingCall(next.CallId, next.RemoteNumber, Kernel.ActiveCallSlotId);
                        }
                        else
                        {
                            _presentedCallId = next.CallId;
                            CurrentCallState = next.State;
                            CurrentCallNumber = next.RemoteNumber;
                            HasIncomingCall = false;
                            _shownIncomingCallId = null;
                            Views.Windows.IncomingCallFloatingWindow.Dismiss();
                        }
                        if (next?.State == CallState.Active &&
                            _connectedCallEvents.TryGetValue(CallEventKey(Kernel.ActiveCallSlotId, next.CallId), out var connected))
                            _ = ResumePromotedCallAudioAsync(audioCleanup, connected);
                        ApplyVolteAudioPrewarmPreference();
                    }
                    AddLog("INFO", "Calls",
                        $"Call ended on slot {e.SlotId ?? "fallback"}, duration: {dur:mm\\:ss}; reason omitted from diagnostics.");
                }));
            };

            // SMS Events
            Kernel.SmsReceived += (s, e) =>
            {
                _ = PersistIncomingSmsEventAsync(e.Message, e.SlotId, e.SimBinding);
                AddLog("INFO", "SMS", $"Received SMS on slot {e.SlotId ?? "unknown"}; body omitted from diagnostics.");
            };

            Kernel.SmsSent += (s, e) =>
            {
                AddLog("INFO", "SMS", $"SMS submission status: {e.Result.SubmissionStatus}");
            };

            Kernel.SmsStatusReportReceived += (s, e) =>
            {
                UpdateSmsDeliveryReport(e.Report);
                AddLog("INFO", "SMS", $"SMS Delivery Report for ref #{e.Report.MessageReference} -> {e.Report.DeliveryStatus}");
            };

            // Slot & Pool Events
            Kernel.SimStateChanged += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.SlotId) || !Kernel.Pool.Slots.TryGetValue(e.SlotId, out var slot)) return;
                if (IsTelephonyTransitioning(slot.Id) || slot.IsHostImsPppTransitionOrActive) return;
                if (e.Sim == null)
                {
                    slot.TelephonyEnabled = false;
                    GetModuleInstance(slot.Id).CellularStatus = null;
                    foreach (var key in _restoredTelephonyCards.Keys.Where(key => key.StartsWith(slot.Id + ":", StringComparison.Ordinal)))
                        _restoredTelephonyCards.TryRemove(key, out _);
                }
                else if (e.State.Equals("READY", StringComparison.OrdinalIgnoreCase))
                    _ = ApplyPreferencesToSlotAsync(slot);
            };
            Kernel.SlotListChanged += (s, e) =>
            {
                RequestSlotSync();
            };
            Kernel.Pool.SlotRemoved += (s, slotId) =>
            {
                if (_moduleInstances.TryRemove(slotId, out var instance)) instance.Dispose();
                _ = DisposeRemovedCellularMediaAsync(slotId);
                PostToUi(() => ReconcileRemovedCallSlot(slotId));
            };

            Kernel.ActiveSlotChanged += (s, e) =>
            {
                RequestSlotSync();
                AddLog("INFO", "ModemPool", $"Active slot changed to: {e.ActiveSlotId}");
            };

            Kernel.SlotStateChanged += (s, e) =>
            {
                RequestSlotSync();
                AddLog("INFO", "ModemPool", $"Slot {e.SlotId} state -> {e.NewState}");
                if (e.NewState == SlotState.Online)
                {
                    var slot = Slots.FirstOrDefault(x => x.Id == e.SlotId);
                    if (slot != null)
                    {
                        _ = ApplyPreferencesToSlotAsync(slot);
                    }
                }
            };

            // State Machine & Diagnostics
            Kernel.TelephonyStateChanged += (s, e) =>
            {
                AddLog("INFO", "StateMachine", $"State transition: {e.From} -> {e.To}");
            };

            Kernel.VoWifiStateChanged += (s, e) =>
            {
                AddLog("INFO", "VoWiFi", $"VoWiFi state: {e.NewState}");
                RecordDiagnosticState($"VoWiFi transition: {e.NewState}");
            };

            Kernel.LogEmitted += (s, e) =>
            {
                AddLog(e.Level, e.Source, e.Message);
                UpdateInitialStartupProgress(e.Source, e.Message);
            };

            Kernel.SystemErrorOccurred += (s, e) =>
            {
                AddLog("ERROR", e.Source, e.ErrorMessage);
                RecordDiagnosticState("System error");
            };
        }

        private void UpdateInitialStartupProgress(string source, string message)
        {
            if (!IsInitialScanRunning || !source.Equals("SIM", StringComparison.OrdinalIgnoreCase))
                return;

            string? status = null;
            if (message.Contains("startup stage=SIM readiness", StringComparison.OrdinalIgnoreCase))
            {
                status = message.Contains("ADF.USIM=selected", StringComparison.OrdinalIgnoreCase)
                    ? "SIM 已响应，正在确认 USIM 应用稳定性…"
                    : "正在等待 SIM 卡与 USIM 应用就绪…";
            }
            else if (message.Contains("startup SIM readiness timed out", StringComparison.OrdinalIgnoreCase))
            {
                status = "SIM 初始化较慢；正在保留设备并等待后续重试…";
            }

            if (status is not null)
                PostToUi(() => InitialScanStatus = status, DispatcherPriority.DataBind);
        }

        private void RequestSlotSync()
        {
            Interlocked.Increment(ref _slotSyncRequestVersion);
            ScheduleSlotSync();
        }

        private void ScheduleSlotSync()
        {
            if (Interlocked.Exchange(ref _slotSyncScheduled, 1) != 0)
            {
                return;
            }

            PostToUi(() =>
            {
                var handledVersion = Volatile.Read(ref _slotSyncRequestVersion);
                try
                {
                    SyncSlots();
                }
                finally
                {
                    Interlocked.Exchange(ref _slotSyncScheduled, 0);
                    if (handledVersion != Volatile.Read(ref _slotSyncRequestVersion))
                    {
                        ScheduleSlotSync();
                    }
                }
            }, DispatcherPriority.DataBind);
        }

        private void SyncSlots()
        {
            var kernelSlots = Kernel.GetSlots().ToList();

            var toRemove = Slots.Where(s => !kernelSlots.Any(k => k.Id == s.Id)).ToList();
            foreach (var r in toRemove) Slots.Remove(r);

            foreach (var k in kernelSlots)
            {
                if (!Slots.Any(s => s.Id == k.Id))
                {
                    Slots.Add(k);
                }
            }

            ApplyCallRecordingPreference();
        }

        private void AddLog(string level, string source, string message)
        {
            var now = DateTime.Now;
            message = DiagnosticLogRedactor.Redact(message);
            _diagnosticRecorder.Record(level, source, message);
            _pendingUiLogs.Enqueue(new LogEntryModel
            {
                Timestamp = now,
                Level = level,
                Source = source,
                Message = message
            });

            ScheduleLogDrain();
            _logLines.Writer.TryWrite($"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {message}");
        }

        private void ScheduleLogDrain()
        {
            if (Interlocked.Exchange(ref _logDrainScheduled, 1) != 0)
            {
                return;
            }

            PostToUi(DrainPendingLogs, DispatcherPriority.Background);
        }

        private void DrainPendingLogs()
        {
            const int maxBatchSize = 100;
            var drained = 0;

            while (drained < maxBatchSize && _pendingUiLogs.TryDequeue(out var entry))
            {
                Logs.Add(entry);
                drained++;
            }

            while (Logs.Count > 500)
            {
                Logs.RemoveAt(0);
            }

            Interlocked.Exchange(ref _logDrainScheduled, 0);
            if (!_pendingUiLogs.IsEmpty)
            {
                ScheduleLogDrain();
            }
        }

        private string GetLogFilePath()
        {
            if (!string.IsNullOrWhiteSpace(_diagnosticLogPath))
            {
                var path = Path.GetFullPath(_diagnosticLogPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return path;
            }
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoWin",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            return Path.Combine(logDirectory, "vowin.log");
        }

        private async Task RunLogWriterAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = new FileStream(
                    GetLogFilePath(),
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    16 * 1024,
                    useAsync: true);
                await using var writer = new StreamWriter(stream) { AutoFlush = false };

                while (await _logLines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_logLines.Reader.TryRead(out var line))
                    {
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }

                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                // Logging must never bring down the telephony UI.
            }
        }

        private void AddCallRecord(string number, CallDirection dir, CallState state, DateTime startTime, TimeSpan duration, string? codec = null, string? slotId = null, string? wavPath = null)
        {
            var record = new CallRecordModel
            {
                PhoneNumber = number,
                Direction = dir,
                FinalState = state,
                Timestamp = startTime,
                Duration = duration,
                Codec = codec,
                SlotId = slotId,
                WavRecordingPath = wavPath
            };

            PostToUi(() => CallHistory.Insert(0, record));
            _ = Preferences.SaveCallRecordAsync(record);
        }

        public void DeleteCallRecord(string id)
        {
            PostToUi(() =>
            {
                var item = CallHistory.FirstOrDefault(c => c.Id == id);
                if (item != null)
                {
                    CallHistory.Remove(item);
                    AddLog("INFO", "Calls", $"Deleted call record {id}");
                }
            });
            _ = Preferences.DeleteCallRecordAsync(id);
        }

        private void PostToUi(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _dispatcher.BeginInvoke(action, priority);
            }
        }

        private Task RunOnUiAsync(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return _dispatcher.InvokeAsync(action, priority).Task;
        }

        private void AddIncomingSms(SmsMessage msg, string? slotId) => _ = AddIncomingSmsAsync(msg, slotId);

        private async Task PersistIncomingSmsEventAsync(SmsMessage msg, string? slotId, string? simBinding)
        {
            // Capture synchronously, before any database or UI wait. Newly
            // created accounts must never receive an older unscanned message.
            IReadOnlyList<SipSmsForwardRecipient> recipients;
            var replyNumber = "";
            try
            {
                // Keep one runtime reference for both snapshots; a later
                // gateway must not supply either recipient or country policy.
                var gateway = Kernel.LocalSipGateway;
                recipients = gateway?.CaptureSmsForwardRecipients(slotId) ?? [];
                if (SmsSubscriptionIdentity.IsValid(simBinding))
                    replyNumber = gateway?.CaptureSmsReplyNumber(slotId, msg.SenderOrRecipient) ?? "";
            }
            catch (Exception ex)
            {
                recipients = [];
                AddLog("WARN", "SipGateway", $"Incoming SMS forwarding plan unavailable ({ex.GetType().Name}); keeping the local inbox path.");
            }
            try { await AddIncomingSmsWithForwardPlanAsync(msg, slotId, simBinding, recipients, replyNumber).ConfigureAwait(false); }
            catch (Exception ex)
            {
                AddLog("WARN", "SMS", $"Incoming SMS was not saved ({ex.GetType().Name}); source metadata and content omitted.");
            }
        }

        private Task AddIncomingSmsAsync(SmsMessage msg, string? slotId) =>
            AddIncomingSmsWithSourceAsync(msg, slotId, simBinding: null);

        private Task AddIncomingSmsWithSourceAsync(SmsMessage msg, string? slotId, string? simBinding) =>
            AddIncomingSmsWithForwardPlanAsync(msg, slotId, simBinding, []);

        private async Task AddIncomingSmsWithForwardPlanAsync(SmsMessage msg, string? slotId, string? simBinding,
            IReadOnlyList<SipSmsForwardRecipient> recipients, string? replyNumber = null)
        {
            var remote = !string.IsNullOrWhiteSpace(msg.SenderOrRecipient) ? msg.SenderOrRecipient : "Unknown";
            var receivedAtUtc = DateTime.UtcNow;
            var normalizedTimestamp = TimestampDisplayHelper.NormalizeIncomingNetworkTime(msg.Timestamp, receivedAtUtc);
            if (normalizedTimestamp != TimestampDisplayHelper.ToUtcStorageTime(msg.Timestamp))
            {
                AddLog("INFO", "SMS", $"Corrected network timestamp from {msg.Timestamp:o} to {normalizedTimestamp:o}");
            }

            var item = new SmsMessageModel
            {
                Index = msg.Index,
                SlotId = slotId,
                SenderOrRecipient = remote,
                Text = msg.Text,
                Timestamp = normalizedTimestamp,
                IsOutgoing = false,
                DeliveryState = SmsDeliveryState.Received,
                DeliveryStatus = msg.DeliveryStatus,
                MessageReference = msg.MessageReference,
                RawPdu = msg.RawPdu,
                SimBinding = SmsSubscriptionIdentity.IsValid(simBinding) ? simBinding : null,
                SourceMessageId = msg.Id,
                SipReplyNumberSnapshot = replyNumber
            };

            var committed = await Preferences.SaveIncomingSmsWithForwardsAsync(item, recipients).ConfigureAwait(false);
            if (!committed.IsNew) return;
            if (committed.ForwardingCapacityExceeded)
                AddLog("WARN", "SipGateway", "Incoming SMS saved; forwarding storage capacity/protection window prevented a new recipient plan; no partial plan was queued.");
            if (committed.ForwardingUnsupported)
                AddLog("WARN", "SipGateway", "Incoming SMS saved; forwarding source or content is outside supported SIP bounds.");
            if (committed.QueuedRecipients > 0) Kernel.LocalSipGateway?.NotifySmsInboxCommitted();
            try { IncomingSmsReceived?.Invoke(item); } catch { }

            await RunOnUiAsync(() =>
            {
                var conv = GetOrCreateConversation(remote);
                if (!conv.Messages.Any(m => m.Id == item.Id))
                {
                    conv.Messages.Add(item);
                    conv.UnreadCount++;
                    conv.UpdateLastMessage();
                    MoveConversationToTop(conv);
                }

                // All newly received SMS deserve a notification, not only OTPs.
                var slotName = !string.IsNullOrEmpty(slotId)
                    ? Slots.FirstOrDefault(s => s.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))?.Name ?? "SIM"
                    : ActiveSlot?.Name ?? "SIM";
                try
                {
                    _smsNotificationPresenter(item, slotName);
                }
                catch (Exception ex) { AddLog("WARN", "SMS", $"Notification failed ({ex.GetType().Name}); received SMS remains saved."); }
            }).ConfigureAwait(false);
        }

        private void UpdateSmsDeliveryReport(SmsStatusReport report)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == report.Recipient);
                if (conv != null)
                {
                    var msg = conv.Messages.LastOrDefault(m => m.IsOutgoing && m.MessageReference == report.MessageReference);
                    if (msg != null)
                    {
                        msg.DeliveryState = report.StatusCode == 0 ? SmsDeliveryState.Delivered : SmsDeliveryState.Failed;
                        msg.DeliveryStatus = report.DeliveryStatus;
                        conv.UpdateLastMessage();
                        _ = Preferences.UpdateSmsDeliveryStatusAsync(msg.Id, msg.DeliveryState, report.DeliveryStatus);
                    }
                }
            });
        }

        private SmsConversationModel GetOrCreateConversation(string number)
        {
            var clean = number.Trim();
            var conv = Conversations.FirstOrDefault(c => c.ContactNumber.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (conv == null)
            {
                string[] colors = { "#0078D4", "#107C10", "#D13438", "#8764B8", "#FF8C00", "#008272", "#E3008C" };
                int hash = Math.Abs(clean.GetHashCode());
                var letter = clean.Length > 0 ? (char.IsLetter(clean[0]) ? clean[0].ToString().ToUpper() : clean[^1].ToString()) : "#";

                conv = new SmsConversationModel
                {
                    ContactNumber = clean,
                    AvatarLetter = letter,
                    AvatarColor = colors[hash % colors.Length]
                };
                Conversations.Add(conv);
            }
            return conv;
        }

        private void MoveConversationToTop(SmsConversationModel conv)
        {
            int oldIdx = Conversations.IndexOf(conv);
            if (oldIdx > 0)
            {
                Conversations.Move(oldIdx, 0);
            }
            else if (oldIdx < 0)
            {
                Conversations.Insert(0, conv);
            }
        }

        // Operation implementations

        public async Task<IReadOnlyList<ModemSlot>> DiscoverSlotsAsync()
        {
            AddLog("INFO", "ModemPool", "Scanning system COM ports for modem hardware...");
            var slots = await DiscoverSlotsCoreAsync();
            await RunOnUiAsync(SyncSlots);
            foreach (var slot in slots)
                await ApplyPreferencesToSlotAsync(slot).ConfigureAwait(false);
            return slots;
        }

        private async Task<IReadOnlyList<ModemSlot>> DiscoverSlotsCoreAsync(CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _discoveryShutdown.Token);
            cancellationToken = linked.Token;
            await _slotDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    async () => await Kernel.DiscoverSlotsAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slotDiscoveryGate.Release();
            }
        }

        public async Task<ModemSlot?> AddSlotAsync(string portName, int baudRate = 115200, string? name = null, string? proxyUrl = null)
        {
            AddLog("INFO", "ModemPool", $"Adding modem slot on {portName} ({baudRate} bps)...");
            var slot = await Kernel.AddSlotAsync(portName, baudRate, name, proxyUrl: proxyUrl);
            await RunOnUiAsync(SyncSlots);
            return slot;
        }

        public async Task<bool> RemoveSlotAsync(string slotId)
        {
            AddLog("INFO", "ModemPool", $"Removing slot {slotId}...");
            bool ok = await Kernel.RemoveSlotAsync(slotId);
            await RunOnUiAsync(SyncSlots);
            return ok;
        }

        public bool SelectSlot(string slotId)
        {
            bool ok = Kernel.SelectSlot(slotId);
            RequestSlotSync();
            return ok;
        }

        public async Task RefreshMetricsAsync(string? slotId = null)
        {
            var slot = !string.IsNullOrWhiteSpace(slotId)
                ? Kernel.Pool.Slots.GetValueOrDefault(slotId) ?? throw new KeyNotFoundException("所选模组已断开，未刷新其他线路。")
                : Kernel.Pool.ActiveSlot;
            if (slot != null)
            {
                await slot.RefreshMetricsAsync().ConfigureAwait(false);
                return;
            }
            await Kernel.RefreshSignalAsync(slotId).ConfigureAwait(false);
            await Kernel.RefreshRegistrationAsync(slotId).ConfigureAwait(false);
        }

        public Task<SimIdentity?> RefreshSimAsync(string? slotId = null) =>
            Kernel.RefreshSimAsync(slotId);

        public async Task<string> ExecuteAtCommandAsync(string command, string? slotId = null)
        {
            var trimmed = command.Trim();
            var loggedCommand = System.Text.RegularExpressions.Regex.Replace(
                trimmed,
                @"(?i)^(AT\+CPIN\s*=).*$",
                "$1<redacted>");
            AddLog("INFO", "AT", $">> {loggedCommand}");

            try
            {
                ModemSlot? targetSlot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out targetSlot);
                }
                else
                {
                    targetSlot = Kernel.Pool.ActiveSlot;
                }

                if (targetSlot?.IsPcscReader == true)
                {
                    const string message = "PC/SC 卡槽不提供 AT 指令；可使用 eSIM/USIM 管理、VoWiFi、IMS 通话和短信。";
                    AddLog("WARN", "PC/SC", message);
                    return $"ERROR: {message}";
                }

                if (targetSlot?.Modem != null && targetSlot.Modem.IsOpen)
                {
                    var resp = await targetSlot.Modem.SendRawAtCommandAsync(trimmed, 5000);
                    var output = resp.RawOutput ?? string.Join("\r\n", resp.Lines);
                    AddLog("INFO", "AT", $"<< {output}");
                    return output;
                }
                else
                {
                    // Fallback to Kernel execute command
                    var res = await Kernel.ExecuteCommandAsync(trimmed);
                    AddLog("INFO", "AT", $"<< [{res.Success}] {res.Message}");
                    return res.Message;
                }
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "AT", $"<< ERROR: {ex.Message}");
                return $"ERROR: {ex.Message}";
            }
        }

        public async Task<string> SendUssdAsync(string code, string? slotId = null)
        {
            AddLog("INFO", "USSD", $">> Sending USSD code: {code}");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    const string message = "PC/SC 卡槽不支持 USSD；请在 VoWiFi 注册后使用 IMS 通话和短信。";
                    AddLog("WARN", "PC/SC", message);
                    return $"ERROR: {message}";
                }

                if (slot != null)
                {
                    var result = await slot.SendUssdAsync(code);
                    AddLog("INFO", "USSD", $"<< {result}");
                    return result;
                }
                return "未找到可用卡槽或调制解调器未就绪。";
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "USSD", $"<< USSD 异常: {ex.Message}");
                return $"ERROR: {ex.Message}";
            }
        }

        public async Task<bool> SetFlightModeAsync(bool enable, string? slotId = null)
        {
            AddLog("INFO", "Modem", $"设置飞行模式: {enable}");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    AddLog("WARN", "PC/SC", "PC/SC 卡槽没有蜂窝射频，无法设置飞行模式。");
                    return false;
                }

                if (slot != null)
                {
                    // Persist the user's intent before touching CFUN. Otherwise a
                    // later CGATT/metrics failure can leave the old preference in
                    // SQLite and the next modem attach will undo a confirmed toggle.
                    await PersistRuntimeIntentAsync(slot, flightMode: enable).ConfigureAwait(false);
                    var applied = await slot.SetFlightModeAsync(enable);
                    if (!applied || slot.IsFlightMode != enable)
                        await PersistRuntimeIntentAsync(slot, flightMode: slot.IsFlightMode).ConfigureAwait(false);
                    AddLog(applied ? "INFO" : "WARN", "Modem", applied
                        ? $"飞行模式已确认{(enable ? "开启 (CFUN=4)" : "关闭 (CFUN=1)")}。"
                        : $"飞行模式未生效：模组没有确认 CFUN={(enable ? 4 : 1)}。偏好未更新。");
                    return applied;
                }
                AddLog("WARN", "Modem", "设置飞行模式失败：未找到目标卡槽。");
                return false;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Modem", $"设置飞行模式失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RebootModemAsync(string? slotId = null)
        {
            AddLog("INFO", "Modem", "发送模组重启指令...");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    AddLog("WARN", "PC/SC", "PC/SC 卡槽没有可重启的 Modem。");
                    return false;
                }

                if (slot != null)
                {
                    return await slot.RebootAsync();
                }
                return false;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Modem", $"模组重启失败: {ex.Message}");
                return false;
            }
        }

        public async Task<CallInfo> DialAsync(string number, string? slotId = null, bool forceCellular = false)
        {
            AddLog("INFO", "Calls", $"Dialing on slot {slotId ?? "selected"} (ForceCellular={forceCellular}); number omitted from diagnostics.");
            var presentDial = Kernel.ActiveCall == null;
            if (presentDial)
            {
                CurrentCallNumber = number;
                CurrentCallState = CallState.Dialing;
            }

            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                else
                    slot = Kernel.Pool.FindSlotForTarget(number) ?? Kernel.Pool.ActiveSlot;

                if ((!forceCellular && slot?.VoWifi?.State == VoWifiState.ImsRegistered) ||
                    (!forceCellular && slot?.GetHostImsRegistrationStatus().IsRegistered == true) ||
                    slot?.Modem is { IsOpen: true })
                {
                    var key = slot?.Id ?? "fallback";
                    using var cancellation = new CancellationTokenSource();
                    if (!_pendingDialCancellations.TryAdd(key, cancellation))
                        throw new InvalidOperationException("此模组已有呼叫正在建立。");
                    var gate = slot == null ? null : GetModuleInstance(slot.Id).ControlGate;
                    var acquired = false;
                    try
                    {
                        if (gate != null) { await gate.WaitAsync(cancellation.Token).ConfigureAwait(false); acquired = true; }
                        return await Kernel.DialAsync(number, slot?.Id ?? slotId, forceCellular, cancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (acquired) gate!.Release();
                        _pendingDialCancellations.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cancellation));
                    }
                }
                else
                {
                    throw new InvalidOperationException("Selected slot has neither registered VoWiFi nor an open cellular modem.");
                }
            }
            catch (Exception ex)
            {
                if (presentDial && Kernel.ActiveCall == null && _presentedCallId == null)
                    CurrentCallState = CallState.Ended;
                AddLog(ex is OperationCanceledException ? "INFO" : "ERROR", "Calls",
                    ex is OperationCanceledException ? "Dial operation canceled; the INVITE reader has finished."
                        : $"Dial failed ({ex.GetType().Name}); see call status for details.");
                throw;
            }
        }

        public async Task<CallInfo?> HangupAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Hanging up current call...");
            var target = slotId ?? Kernel.ActiveCallSlotId ?? ActiveSlot?.Id;
            var res = await Kernel.HangupAsync(target);
            if (res == null && _pendingDialCancellations.TryGetValue(target ?? "fallback", out var pending))
            {
                // Before INVITE exists, cancel preparation/gate acquisition.
                // Once an INVITE exists, the call manager owns CANCEL and the
                // late-200 ACK/BYE race; do not abort that transaction's reader.
                try { pending.Cancel(); } catch (ObjectDisposedException) { }
                return null;
            }
            if (res == null && string.Equals(target, Kernel.ActiveCallSlotId, StringComparison.OrdinalIgnoreCase) &&
                CurrentCallState is not (CallState.Idle or CallState.Ended))
                throw new InvalidOperationException("Call hangup was not confirmed by the modem or IMS session.");
            // The matching CallEnded event owns UI/media cleanup and history.
            // A caller may hang up a background slot while another call remains.
            return res;
        }

        public Task<CallInfo?> AnswerAsync(string? slotId = null) => AnswerCoreAsync(slotId);

        private async Task<CallInfo?> AnswerCoreAsync(string? slotId, string? expectedCallId = null,
            string? messagePath = null, CancellationToken cancellationToken = default)
        {
            await _incomingAnswerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var effectiveSlotId = expectedCallId == null ? slotId ?? Kernel.ActiveCallSlotId : slotId;
            VoSharp.Kernel.SipGateway.SipGateway? localPickupGateway = null;
            string? localPickupCallId = null;
            try
            {
                if (expectedCallId != null && (cancellationToken.IsCancellationRequested ||
                    Kernel.ActiveCall?.State != CallState.Incoming ||
                    !string.Equals(Kernel.ActiveCall?.CallId, expectedCallId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Kernel.ActiveCallSlotId, effectiveSlotId, StringComparison.OrdinalIgnoreCase))) return null;
                if (Kernel.LocalSipGateway is { } gateway && gateway.RoutesIncomingCall(effectiveSlotId))
                {
                    var current = Kernel.ActiveCall;
                    if (current?.State != CallState.Incoming ||
                        !string.Equals(Kernel.ActiveCallSlotId, effectiveSlotId, StringComparison.OrdinalIgnoreCase) ||
                        !gateway.TryClaimLocalIncomingCall(current.CallId, effectiveSlotId))
                    {
                        AddLog("INFO", "SipGateway", "Desktop pickup skipped: this incoming call was already claimed or ended.");
                        return null;
                    }
                    localPickupGateway = gateway;
                    localPickupCallId = current.CallId;
                }
                // Manual pickup and the countdown cannot answer the same call concurrently.
                if (string.Equals(effectiveSlotId, Kernel.ActiveCallSlotId, StringComparison.OrdinalIgnoreCase))
                    CancelIncomingAutoAnswer();
                if (expectedCallId != null && messagePath != null)
                    _autoAnswerMessages.Queue(expectedCallId, effectiveSlotId, messagePath);
                AddLog("INFO", "Calls", "Answering incoming call...");
                try
                {
                    var result = await Kernel.AnswerAsync(effectiveSlotId);
                    if (result == null && localPickupCallId != null)
                        localPickupGateway?.ReleaseLocalIncomingCallClaim(localPickupCallId, effectiveSlotId);
                    if (result == null && expectedCallId != null)
                        _autoAnswerMessages.Remove(expectedCallId, effectiveSlotId);
                    return result;
                }
                catch (Exception ex)
                {
                    if (localPickupCallId != null)
                        localPickupGateway?.ReleaseLocalIncomingCallClaim(localPickupCallId, effectiveSlotId);
                    if (expectedCallId != null) _autoAnswerMessages.Remove(expectedCallId, effectiveSlotId);
                    AddLog("ERROR", "Calls", $"Answer failed ({ex.GetType().Name}).");
                    throw;
                }
            }
            finally { _incomingAnswerGate.Release(); }
        }

        public async Task<CallInfo?> RejectAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Rejecting incoming call...");
            var effectiveSlotId = slotId ?? Kernel.ActiveCallSlotId;
            var current = Kernel.ActiveCall;
            if (current?.State == CallState.Incoming &&
                string.Equals(effectiveSlotId, Kernel.ActiveCallSlotId, StringComparison.OrdinalIgnoreCase))
            {
                var key = CallEventKey(effectiveSlotId, current.CallId);
                _callEventMetadata.AddOrUpdate(key,
                    (current.RemoteNumber, current.StartTime, CallDirection.Missed),
                    (_, old) => (old.Number, old.Started, CallDirection.Missed));
            }

            try
            {
                if (Kernel.LocalSipGateway is { } gateway && gateway.RoutesIncomingCall(effectiveSlotId))
                {
                    if (current != null && string.Equals(effectiveSlotId, Kernel.ActiveCallSlotId, StringComparison.OrdinalIgnoreCase))
                        gateway.CancelIncomingCallRinging(current.CallId, effectiveSlotId);
                    // Hangup also covers a simultaneous SIP pickup that has
                    // already sent carrier 200 and is waiting for its ACK.
                    return await Kernel.HangupAsync(effectiveSlotId);
                }
                return await Kernel.RejectAsync(effectiveSlotId);
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Calls", $"Reject failed ({ex.GetType().Name}).");
                throw;
            }
        }

        public async Task<bool> SendDtmfAsync(char digit, string? slotId = null)
        {
            AddLog("INFO", "Calls", "Sending DTMF digit; value omitted from diagnostics.");
            return await Kernel.SendDtmfAsync(digit, slotId);
        }

        public async Task<SmsSubmitResult> SendSmsAsync(string recipient, string text, bool requestStatusReport = true, string? slotId = null, bool forceVowifi = false, bool forceCellular = false)
        {
            var smsSlot = !string.IsNullOrEmpty(slotId) ? Kernel.Pool.Slots.GetValueOrDefault(slotId)
                : Kernel.Pool.FindSlotForTarget(recipient) ?? ActiveSlot;
            if (smsSlot == null) throw new InvalidOperationException("所选通信模组已断开，请重新选择模组 / SIM。");
            slotId = smsSlot.Id;
            AddLog("INFO", "SMS", $"Sending SMS on slot {slotId}; preferred={smsSlot.PreferredTelephonyMode}; cellularOwner={smsSlot.CellularImsMode}; forceVoWiFi={forceVowifi}; forceCellular={forceCellular}; recipient and body omitted from diagnostics.");

            SmsConversationModel conv = null!;
            SmsMessageModel model = null!;
            await RunOnUiAsync(() =>
            {
                conv = GetOrCreateConversation(recipient);
                model = new SmsMessageModel
                {
                    SlotId = slotId,
                    SenderOrRecipient = recipient,
                    Text = text,
                    Timestamp = DateTime.UtcNow,
                    IsOutgoing = true,
                    DeliveryState = SmsDeliveryState.Sending
                };
                conv.Messages.Add(model);
                conv.UpdateLastMessage();
                MoveConversationToTop(conv);
            });

            await Preferences.SaveSmsMessageAsync(model);

            try
            {
                // Host IMS currently confirms submission with RP-ACK; delivery
                // reports are not yet part of the validated desktop path.
                if (!forceVowifi && !forceCellular && smsSlot?.PreferredTelephonyMode == TelephonyMode.HostIms)
                    requestStatusReport = false;
                var result = await Kernel.SendSmsAsync(recipient, text, requestStatusReport, slotId, forceVowifi, forceCellular);
                await RunOnUiAsync(() =>
                {
                    model.DeliveryState = result.AllPartsAccepted ? SmsDeliveryState.Sent
                        : result.PartsAccepted > 0 ? SmsDeliveryState.PartiallySent : SmsDeliveryState.Failed;
                    model.DeliveryStatus = result.SubmissionStatus;
                    model.MessageReference = result.ConcatReference;
                    conv.UpdateLastMessage();
                });
                await Preferences.UpdateSmsDeliveryStatusAsync(model.Id, model.DeliveryState, model.DeliveryStatus);
                return result;
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() =>
                {
                    model.DeliveryState = SmsDeliveryState.Failed;
                    model.DeliveryStatus = ex.Message;
                    conv.UpdateLastMessage();
                });
                await Preferences.UpdateSmsDeliveryStatusAsync(model.Id, SmsDeliveryState.Failed, ex.Message);
                AddLog("ERROR", "SMS", $"Send SMS failed ({ex.GetType().Name}); recipient and body omitted from diagnostics.");
                throw;
            }
        }

        public void DeleteConversation(string contactNumber)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == contactNumber);
                if (conv != null)
                {
                    Conversations.Remove(conv);
                    AddLog("INFO", "SMS", $"Deleted conversation with {contactNumber}");
                }
            });
            _ = Preferences.DeleteConversationAsync(contactNumber);
        }

        public void ClearConversation(string contactNumber)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == contactNumber);
                if (conv != null)
                {
                    conv.Messages.Clear();
                    conv.UpdateLastMessage();
                    AddLog("INFO", "SMS", $"Cleared messages for conversation with {contactNumber}");
                }
            });
            _ = Preferences.DeleteConversationAsync(contactNumber);
        }

        public void DeleteMessage(string conversationNumber, string messageId)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == conversationNumber);
                if (conv != null)
                {
                    var msg = conv.Messages.FirstOrDefault(m => m.Id == messageId);
                    if (msg != null)
                    {
                        conv.Messages.Remove(msg);
                        conv.UpdateLastMessage();
                    }
                }
            });
            _ = Preferences.DeleteSmsMessageAsync(messageId);
        }

        public void ClearCallHistory()
        {
            PostToUi(CallHistory.Clear);
            _ = Preferences.ClearCallRecordsAsync();
            AddLog("INFO", "Calls", "Call history cleared.");
        }

        public Task<bool> StartVoWifiAsync(string? slotId = null) => StartVoWifiWithIntentAsync(slotId);

        private async Task<bool> StartVoWifiWithIntentAsync(string? slotId, long? expectedIntent = null)
        {
            var targetSlot = !string.IsNullOrEmpty(slotId) ? Kernel.Pool.Slots.GetValueOrDefault(slotId) : ActiveSlot;
            if (targetSlot == null) return false;
            var instance = GetModuleInstance(targetSlot.Id);
            await instance.WifiGate.WaitAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            instance.WifiCancellation = cancellation;
            instance.WifiBusy = true;
            instance.WifiError = null;
            instance.Refresh();
            var acquired = false;
            try
            {
                await instance.ControlGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                acquired = true;
                if (expectedIntent is { } expected && GetVoWifiIntentVersion(targetSlot.Id) != expected) return false;
                if (targetSlot.VoWifi.State == VoWifiState.ImsRegistered) return true;
                if (targetSlot.Sim == null) throw new InvalidOperationException("请先插入并解锁 SIM。");
                var version = AdvanceVoWifiIntent(targetSlot.Id);
                _voWifiDesiredStates[GetVoWifiPreferenceKey(targetSlot)] = true;
                var started = await StartVoWifiCoreAsync(targetSlot, version, cancellation.Token).ConfigureAwait(false);
                if (!started) instance.WifiError = "VoWiFi 连接未完成，请查看日志。";
                return started;
            }
            catch (Exception ex)
            {
                instance.WifiError = ex is OperationCanceledException ? null : ex.Message;
                throw;
            }
            finally
            {
                if (acquired) instance.ControlGate.Release();
                instance.WifiCancellation = null;
                instance.WifiBusy = false;
                instance.WifiGate.Release();
                instance.Refresh();
            }
        }

        public async Task<bool> StopVoWifiAsync(string? slotId = null)
        {
            var targetSlot = !string.IsNullOrEmpty(slotId) ? Kernel.Pool.Slots.GetValueOrDefault(slotId) : ActiveSlot;
            if (targetSlot == null) return false;
            var instance = GetModuleInstance(targetSlot.Id);
            try { instance.WifiCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            AdvanceVoWifiIntent(targetSlot.Id);
            _voWifiDesiredStates[GetVoWifiPreferenceKey(targetSlot)] = false;
            await instance.WifiGate.WaitAsync().ConfigureAwait(false);
            await instance.ControlGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (targetSlot.Calls.ActiveCall != null && !targetSlot.Calls.IsHostImsCall)
                    throw new InvalidOperationException("请先挂断此模组的 VoWiFi 通话。");
                instance.WifiError = null;
                return await Kernel.StopVoWifiAsync(targetSlot.Id).ConfigureAwait(false);
            }
            finally
            {
                instance.ControlGate.Release(); instance.WifiGate.Release(); instance.Refresh();
            }
        }

        private async Task<bool> StartVoWifiCoreAsync(ModemSlot targetSlot, long expectedIntentVersion, CancellationToken ct = default)
        {
            // Starting VoWiFi must apply identity/routing only. It must never
            // restore an old CFUN/CGATT preference as a side effect of Connect.
            await ApplyPreferencesToSlotAsync(targetSlot, restoreAutoVoWifi: false, restoreRadioPreferences: false)
                .ConfigureAwait(false);
            if (GetVoWifiIntentVersion(targetSlot.Id) != expectedIntentVersion) return false;
            ct.ThrowIfCancellationRequested();

            await ApplyVoWifiDiscoveryPreferencesAsync(targetSlot).ConfigureAwait(false);
            var effectiveProxy = ResolveEgressProxyForSlot(targetSlot.Id);
            if (!Kernel.SetSlotProxy(targetSlot.Id, effectiveProxy))
            {
                AddLog("ERROR", "Proxy", $"Unable to apply the proxy route to slot {targetSlot.Id}.");
                return false;
            }

            targetSlot.ProxyUrl = effectiveProxy;
            AddLog("INFO", "VoWiFi", !string.IsNullOrEmpty(effectiveProxy)
                ? $"VoWiFi 代理路由已应用至核心: {DescribeProxyEndpoint(effectiveProxy)} -> 卡槽 [{targetSlot.Name}]"
                : $"VoWiFi 使用直连 UDP -> 卡槽 [{targetSlot.Name}]");
            AddLog("INFO", "VoWiFi", $"Starting VoWiFi on slot {targetSlot.Id}...");
            var started = await Kernel.StartVoWifiAsync(targetSlot.Id, ct).ConfigureAwait(false);

            // A manual OFF may race a start already inside the kernel lifecycle
            // gate. OFF wins deterministically and performs a second idempotent
            // stop after that older start unwinds.
            if (GetVoWifiIntentVersion(targetSlot.Id) != expectedIntentVersion)
            {
                await Kernel.StopVoWifiAsync(targetSlot.Id).ConfigureAwait(false);
                return false;
            }
            return started;
        }

        private long AdvanceVoWifiIntent(string slotId) =>
            _voWifiIntentVersions.AddOrUpdate(slotId, 1, static (_, current) => current + 1);

        private long GetVoWifiIntentVersion(string slotId) =>
            _voWifiIntentVersions.TryGetValue(slotId, out var version) ? version : 0;

        private static string GetVoWifiPreferenceKey(ModemSlot slot) =>
            $"{slot.Id}|{slot.Sim?.Iccid ?? "NO_SIM"}";

        public async Task<(bool Success, long RttMs, string Status)> ProbeVoWifiLivenessAsync(string? slotId = null)
        {
            var result = await Kernel.ProbeVoWifiLivenessAsync(slotId);
            AddLog(result.Success ? "INFO" : "WARN", "VoWiFi", $"SIP 探针保活检测: {(result.Success ? "成功" : "失败")}, RTT: {result.RttMs}ms, 状态: {result.Status}");
            return result;
        }

        public async Task<HostImsProbeResult> ProbeHostImsAsync(
            string? slotId = null,
            CancellationToken cancellationToken = default)
        {
            var slot = !string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(s => s.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"Host IMS slot '{slotId}' is unavailable.")
                : ActiveSlot ?? Slots.FirstOrDefault();

            if (slot == null)
                throw new InvalidOperationException("未发现可用蜂窝模组。");
            if (slot.IsPcscReader)
                throw new InvalidOperationException("PC/SC 读卡器没有蜂窝 IMS 数据面；请选择 AT 蜂窝模组。");
            if (slot.IsProfileSwitchInProgress)
                throw new InvalidOperationException("eSIM 正在切换配置，完成后再探测 Host IMS。");
            if (slot.Modem is not { IsOpen: true } modem)
                throw new InvalidOperationException("所选模组尚未连接，无法读取 IMS PDP Context。");

            AddLog("INFO", "IMS", $"正在只读探测卡槽 [{slot.Name}] 的蜂窝 Host IMS 前置条件。");
            var result = await modem.ProbeHostImsPdnAsync(cancellationToken).ConfigureAwait(false);
            AddLog(result.CanAttemptWindowsIms ? "INFO" : "WARN", "IMS",
                $"Host IMS 探测完成：{result.Readiness}；USB={result.UsbNetworkMode ?? "unknown"}；{result.Summary}");
            return result;
        }

        public HostImsPppBearerStatus GetHostImsPppBearerStatus(string? slotId = null) =>
            Kernel.GetHostImsPppBearerStatus(slotId);

        public async Task<HostImsPppBearerStatus> StartHostImsPppBearerAsync(
            string handoffScript, string rasProbeExecutable, bool handoffApproved = false,
            string? slotId = null, CancellationToken cancellationToken = default)
        {
            if (!handoffApproved)
                throw new InvalidOperationException("启动前须确认本次会暂时移交模组 IMS 并重启模组。");
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Windows IMS PPP 需要以管理员身份启动 VoWin；尚未修改模组配置。");
            if (string.IsNullOrWhiteSpace(handoffScript) || !Path.IsPathFullyQualified(handoffScript) ||
                !File.Exists(handoffScript) ||
                !Path.GetFileName(handoffScript).Equals("VoSharp.HostIms.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择完整路径的 VoSharp.HostIms.exe。");
            AddLog("WARN", "IMS", "正在暂时移交模组 IMS 到 Windows PPP；会重启模组，租约结束时恢复模组内部 IMS，不发送 SIP REGISTER。");
            var status = await Kernel.StartHostImsPppBearerAsync(
                handoffScript, rasProbeExecutable, slotId, ct: cancellationToken).ConfigureAwait(false);
            if (status.IsActive)
            {
                var slot = slotId == null ? ActiveSlot : Kernel.Pool.Slots.GetValueOrDefault(slotId);
                if (slot != null)
                {
                    slot.CellularImsMode = TelephonyMode.HostIms;
                    slot.PreferredTelephonyMode ??= TelephonyMode.HostIms;
                    slot.TelephonyEnabled = false;
                }
            }
            AddLog(status.IsActive ? "INFO" : "WARN", "IMS",
                status.IsActive ? "Windows 已持有隔离的 IMS PPP bearer；尚未注册 SIP。" :
                    $"IMS PPP bearer 未激活：{status.LastError ?? "租约已结束"}");
            return status;
        }

        public async Task StopHostImsPppBearerAsync(string? slotId = null)
        {
            await Kernel.StopHostImsPppBearerAsync(slotId).ConfigureAwait(false);
            var slot = slotId == null ? ActiveSlot : Kernel.Pool.Slots.GetValueOrDefault(slotId);
            if (slot?.CellularImsMode == TelephonyMode.HostIms) slot.TelephonyEnabled = false;
            AddLog("INFO", "IMS", "已停止 Windows IMS PPP，并请求恢复模组内部 IMS。");
        }

        public HostImsRegistrationStatus GetHostImsRegistrationStatus(string? slotId = null) =>
            Kernel.GetHostImsRegistrationStatus(slotId);

        public async Task<HostImsRegistrationStatus> StartHostImsRegistrationAsync(
            string? slotId = null,
            HostImsEndpointCandidate? endpoint = null,
            bool subscriberEgressApproved = false,
            bool useUserspaceEsp = true,
            string? winDivertDll = null,
            CancellationToken cancellationToken = default,
            bool enableSmsReceiver = false)
        {
            if (!subscriberEgressApproved)
                throw new InvalidOperationException("发送当前 SIM 的 IMS 身份前，须明确确认本次运营商注册。");
            VoSharp.Telephony.IHostImsSecurityBackend? securityBackend = null;
            if (useUserspaceEsp && !VoKernel.TryCreateHostImsUserspaceBackend(
                    "1", winDivertDll, out securityBackend, out var error))
                throw new InvalidOperationException(error);
            AddLog("WARN", "IMS", useUserspaceEsp
                ? "正在尝试实验性蜂窝 Host IMS 注册；使用 C# ESP 与 WinDivert 原始包 I/O。已有单一模组/SIM/运营商组合的 CLI 双向语音实证，当前界面及其他网络仍需逐项验收。"
                : "正在尝试无 ESP 的实验性蜂窝 Host IMS 注册；仅适用于不要求 ipsec-3gpp 的网络。");
            var status = await Kernel.StartHostImsRegistrationAsync(slotId, endpoint, cancellationToken,
                securityBackend, enableSmsReceiver).ConfigureAwait(false);
            if (status.IsRegistered)
            {
                var slot = slotId == null ? ActiveSlot : Kernel.Pool.Slots.GetValueOrDefault(slotId);
                if (slot != null)
                {
                    slot.CellularImsMode = TelephonyMode.HostIms;
                    slot.PreferredTelephonyMode ??= TelephonyMode.HostIms;
                    slot.TelephonyEnabled = true;
                }
            }
            AddLog(status.IsRegistered ? "INFO" : "WARN", "IMS", $"Host IMS 注册状态：{status.State}；{status.Endpoint ?? "无端点"}");
            return status;
        }

        public async Task StopHostImsRegistrationAsync(string? slotId = null)
        {
            await Kernel.StopHostImsRegistrationAsync(slotId).ConfigureAwait(false);
            var slot = slotId == null ? ActiveSlot : Kernel.Pool.Slots.GetValueOrDefault(slotId);
            if (slot?.CellularImsMode == TelephonyMode.HostIms) slot.TelephonyEnabled = false;
            AddLog("INFO", "IMS", "已停止实验性蜂窝 Host IMS 注册。");
        }

        public async Task<EuiccProbeResult> ProbeEuiccAsync(string? slotId = null, CancellationToken cancellationToken = default)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot == null)
            {
                return new EuiccProbeResult(
                    EuiccCapability.Error,
                    null,
                    Array.Empty<Profile>(),
                    "未发现可用卡槽，无法检测 eUICC 芯片。",
                    Array.Empty<string>());
            }

            var result = await slot.ProbeEuiccAsync(cancellationToken).ConfigureAwait(false);
            var level = result.Capability == EuiccCapability.Error ? "WARN" : "INFO";
            AddLog(level, "eSIM", $"卡槽 [{slot.Name}] eUICC 检测：{result.Message}");
            return result;
        }

        public async Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.GetEuiccProfilesAsync();
            }
            return await Kernel.GetEuiccProfilesAsync();
        }

        public async Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Switching to eSIM profile: {iccidOrAid} on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                // Own the operation in the application service rather than the
                // current page. Navigation or a repeated click now observes the
                // same task and cannot abandon/duplicate a profile switch.
                var operation = _profileSwitchTasks.GetOrAdd(slot.Id,
                    _ => new Lazy<Task<bool>>(
                        () => RunProfileSwitchAsync(slot, iccidOrAid, euiccAid),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                return await operation.Value.ConfigureAwait(false);
            }
            return await Kernel.SwitchEuiccProfileAsync(iccidOrAid);
        }

        private async Task<bool> RunProfileSwitchAsync(ModemSlot slot, string iccidOrAid, string? euiccAid)
        {
            try
            {
                // A queued auto-start belongs to the old SIM identity. Invalidate
                // it before the slot changes so it cannot wake up mid-switch and
                // contend for the modem AT channel.
                _voWifiDesiredStates[GetVoWifiPreferenceKey(slot)] = false;
                AdvanceVoWifiIntent(slot.Id);
                _autoVoWifiStarts.TryRemove(slot.Id, out _);

                var switched = await slot.SwitchEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid).ConfigureAwait(false);
                if (switched)
                {
                    await ApplyPreferencesToSlotAsync(slot).ConfigureAwait(false);
                    RequestSlotSync();
                }
                return switched;
            }
            finally
            {
                _profileSwitchTasks.TryRemove(slot.Id, out _);
            }
        }

        public async Task<bool> DisableEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Disabling eSIM profile: {iccidOrAid} on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.DisableEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid);
            }
            return await Kernel.DisableEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Deleting eSIM profile: {iccidOrAid}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.DeleteEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid);
            }
            return await Kernel.DeleteEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Renaming profile {iccidOrAid} -> \"{nickname}\" on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.RenameEuiccProfileAsync(iccidOrAid, nickname, euiccAid: euiccAid);
            }
            return await Kernel.RenameEuiccProfileAsync(iccidOrAid, nickname);
        }

        public async Task<string> GetEuiccEidAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.GetEuiccEidAsync();
            }
            return await Kernel.GetEuiccEidAsync();
        }

        public async Task<EuiccDownloadResult> DownloadEuiccProfileAsync(
            string activationCode,
            string? confirmationCode = null,
            IProgress<EuiccDownloadProgress>? progress = null,
            string? slotId = null,
            CancellationToken cancellationToken = default,
            bool allowUntrustedTls = false,
            bool allowRetryAfterUncertain = false,
            string? euiccAid = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            AddLog("INFO", "eSIM", $"Starting verified eSIM profile download on slot {slot?.Name ?? "Active"}.");
            if (allowUntrustedTls)
                AddLog("WARN", "eSIM", "TLS certificate validation bypass enabled for this SM-DP+ download session.");
            if (allowRetryAfterUncertain)
                AddLog("WARN", "eSIM", "Provider-authorized retry enabled; the matching local uncertain transaction may be reset.");
            var tracedProgress = new CallbackProgress<EuiccDownloadProgress>(value =>
            {
                AddLog("INFO", "eSIM", $"Download progress {value.Percent}%: {value.Status}");
                progress?.Report(value);
            });
            try
            {
                var result = slot != null
                    ? await slot.DownloadEuiccProfileAsync(activationCode, confirmationCode, tracedProgress, cancellationToken, allowUntrustedTls, allowRetryAfterUncertain, euiccAid)
                    : await Kernel.DownloadEuiccProfileAsync(activationCode, confirmationCode, tracedProgress, cancellationToken, allowUntrustedTls, allowRetryAfterUncertain);
                AddLog(result.InstalledWithWarning ? "WARN" : "INFO", "eSIM",
                    $"Profile download completed. ICCID={result.Iccid}; Warning={result.Warning ?? "None"}");
                return result;
            }
            catch (Exception ex)
            {
                var errorChain = new List<string>();
                for (Exception? current = ex; current != null; current = current.InnerException)
                {
                    var message = current.Message.Trim();
                    if (!string.IsNullOrEmpty(message) && !errorChain.Contains(message, StringComparer.Ordinal))
                        errorChain.Add(message);
                }
                AddLog("ERROR", "eSIM", $"Profile download failed: {string.Join(" -> ", errorChain)}");
                throw;
            }
        }

        public bool SetSlotProxy(string slotId, string? proxyUrl)
        {
            AddLog("INFO", "Proxy", $"Setting proxy for slot {slotId}: {proxyUrl ?? "Direct"}");
            return Kernel.SetSlotProxy(slotId, proxyUrl);
        }

        public async Task<int> TestProxyConnectivityAsync(ProxyNodeModel node, string targetHost = "8.8.8.8", int targetPort = 53)
        {
            var sw = Stopwatch.StartNew();
            node.Status = "正在验证 SOCKS5 公网 UDP 收发...";
            IPEndPoint? relay = null;

            try
            {
                using var proxy = Socks5Client.TryParse(node.ToProxyUrl())
                    ?? throw new InvalidOperationException("VoWiFi 需要 socks://、socks5:// 或 socks5h:// 代理；HTTP 代理不支持 IKEv2/ESP UDP。");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                relay = await proxy.UdpAssociateAsync(cts.Token);

                var targetIp = IPAddress.TryParse(targetHost, out var parsedTarget)
                    ? parsedTarget
                    : (await Dns.GetHostAddressesAsync(targetHost, cts.Token).ConfigureAwait(false))
                        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                        ?? throw new InvalidOperationException($"无法解析 UDP 探测目标 {targetHost}。");

                var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue + 1);
                var dnsQuery = BuildDnsHealthQuery(transactionId);
                var dnsResponse = await proxy.UdpRoundTripAsync(
                    dnsQuery,
                    new IPEndPoint(targetIp, targetPort),
                    cts.Token).ConfigureAwait(false);
                if (dnsResponse.Length < 12 ||
                    BinaryPrimitives.ReadUInt16BigEndian(dnsResponse.AsSpan(0, 2)) != transactionId ||
                    (dnsResponse[2] & 0x80) == 0)
                {
                    throw new InvalidOperationException("SOCKS5 公网 UDP 返回了无效的 DNS 响应。");
                }

                sw.Stop();
                int ms = (int)sw.ElapsedMilliseconds;
                node.LatencyMs = ms;
                node.Status = $"SOCKS5 公网 UDP 正常 ({ms}ms)";
                AddLog("INFO", "Proxy",
                    $"SOCKS5 UDP data plane healthy for {node.Name} ({node.Host}:{node.Port}); relay {relay}, DNS {targetIp}:{targetPort}, RTT {ms}ms");
                return ms;
            }
            catch (Exception ex)
            {
                sw.Stop();
                node.LatencyMs = -1;
                node.Status = relay == null
                    ? $"SOCKS5 UDP 关联失败: {ex.Message}"
                    : $"SOCKS5 UDP 已关联，但公网 DNS 探测无响应: {ex.Message}";
                AddLog("WARN", "Proxy",
                    $"SOCKS5 UDP health check failed for {node.Name} ({node.Host}:{node.Port}); " +
                    $"relay={(relay == null ? "not established" : relay)}, error={ex.Message}");
                return -1;
            }
        }

        public async Task<string> BuildImsDiagnosticReportAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(s => s.Id == slotId)
                : ActiveSlot) ?? Slots.FirstOrDefault();
            var diag = slot?.VoWifiDiag ?? VoWifiDiag;
            var routeDecision = await ResolveVoWifiRouteAsync(slot?.Id).ConfigureAwait(false);
            var report = new StringBuilder();
            var appVersion = typeof(VoKernelService).Assembly.GetName().Version?.ToString() ?? "unknown";

            report.AppendLine("VoWin IMS diagnostic report");
            report.AppendLine("This report automatically redacts subscriber identities, phone numbers, AKA material, and proxy credentials.");
            report.AppendLine($"Generated (local): {DateTimeOffset.Now:O}");
            report.AppendLine($"App version: {appVersion}");
            report.AppendLine($"OS: {Environment.OSVersion}");
            report.AppendLine();
            report.AppendLine("=== Selected modem / SIM context ===");
            report.AppendLine($"Slot: {slot?.Id ?? "none"}");
            report.AppendLine($"Port: {slot?.PortName ?? "unknown"}");
            report.AppendLine($"Firmware: {slot?.Modem?.FirmwareRevision ?? "unknown"}");
            report.AppendLine($"Flight mode: {slot?.IsFlightMode.ToString() ?? "unknown"}");
            report.AppendLine($"SIM PLMN: {slot?.Sim?.Mcc ?? "?"}-{slot?.Sim?.Mnc ?? "?"}");
            report.AppendLine($"IMSI source: {slot?.ImsiIdentitySource ?? "unknown"}");
            report.AppendLine($"EF_IMSI PLMN: {DescribeImsiPlmn(slot?.LastPermanentImsi)}");
            report.AppendLine($"AT+CIMI PLMN: {DescribeImsiPlmn(slot?.LastReportedImsi)}");
            report.AppendLine($"Multi-IMSI PLMN conflict: {slot?.HasImsiPlmnConflict.ToString() ?? "unknown"}");
            report.AppendLine($"Stable routing MCC: {slot?.StableRoutingMcc ?? "unknown"}");
            report.AppendLine($"SOCKS route configured: {!string.IsNullOrWhiteSpace(slot?.ProxyUrl)}");
            report.AppendLine($"Route rule: {routeDecision.RuleDisplay}");
            report.AppendLine($"Route target: {routeDecision.TargetDisplay}");
            report.AppendLine();
            report.AppendLine("=== Cellular Host IMS prerequisite probe ===");
            if (slot?.Modem is { IsOpen: true } modem)
            {
                try
                {
                    var hostIms = await modem.ProbeHostImsPdnAsync().ConfigureAwait(false);
                    report.AppendLine($"Readiness: {hostIms.Readiness}");
                    report.AppendLine($"Modem IMS setting: {hostIms.ModemImsEnabled?.ToString() ?? "MBN/default"}");
                    report.AppendLine($"USB network mode: {hostIms.UsbNetworkMode ?? "unknown"}");
                    foreach (var adapter in hostIms.WindowsCellularAdapters)
                        report.AppendLine($"Windows cellular candidate: {DiagnosticLogRedactor.Redact(adapter.Name)} " +
                            $"({(adapter.IsConnected ? "connected" : "disconnected")}; {DiagnosticLogRedactor.Redact(adapter.Description)})");
                    foreach (var candidate in hostIms.EndpointCandidates)
                        report.AppendLine($"IMS endpoint candidate: {candidate.Display}; " +
                            $"local={candidate.LocalAddress}; P-CSCF={candidate.PcscfAddress}; route=unverified");
                    report.AppendLine($"Conclusion: {hostIms.Summary}");
                    foreach (var context in hostIms.Contexts)
                    {
                        report.AppendLine(
                            $"{context.Label}: type={context.PdpType}; active={context.IsActive}; " +
                            $"local={string.Join(',', context.LocalAddresses)}; P-CSCF={string.Join(',', context.PcscfServers)}; " +
                            $"Windows interfaces={string.Join(',', context.HostInterfaces.DefaultIfEmpty("none"))}");
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine($"Probe failed: {DiagnosticLogRedactor.Redact(ex.Message)}");
                }
            }
            else report.AppendLine("Unavailable: no open physical modem.");
            report.AppendLine();
            report.AppendLine("=== Cellular USB PCM fallback ===");
            try
            {
                var audio = CellularAudioWorker.ProbeModemEndpoints(slot?.PortName ?? throw new InvalidOperationException("No physical modem selected."));
                report.AppendLine($"WinMM modem capture endpoint: {(audio.Capture ? "present" : "not currently enumerated")}");
                report.AppendLine($"WinMM modem playback endpoint: {(audio.Playback ? "present" : "not currently enumerated")}");
                report.AppendLine("Endpoint enumeration does not prove live call PCM or a working QDC507 D4 route.");
            }
            catch (Exception ex)
            {
                report.AppendLine($"WinMM endpoint enumeration failed: {DiagnosticLogRedactor.Redact(ex.Message)}");
            }
            report.AppendLine();
            report.AppendLine("=== VoWiFi / IMS snapshot ===");
            report.AppendLine($"State: {diag?.State.ToString() ?? "unavailable"}");
            report.AppendLine($"Last error: {DiagnosticLogRedactor.Redact(diag?.LastError ?? "none")}");
            report.AppendLine($"Failure classification: stage={diag?.FailureStage ?? "none"}; category={diag?.FailureCategory ?? "none"}");
            report.AppendLine($"ePDG: {diag?.EpdgFqdn ?? "unknown"} ({diag?.EpdgIp ?? "unknown"}:{diag?.EpdgPort})");
            report.AppendLine($"IKE suite: {diag?.Suite.ToString() ?? "unknown"}; DH: {diag?.DhGroup ?? "unknown"}; EAP: {diag?.EapMethod ?? "unknown"}");
            report.AppendLine($"Tunnel: {(diag?.Tunnel is null ? "not established" : $"assigned={diag.Tunnel.AssignedIPv4 ?? diag.Tunnel.AssignedIPv6 ?? "unknown"}; P-CSCF={diag.Tunnel.PcscfIp}; ESP={diag.Tunnel.EncryptionAlgorithm}/{diag.Tunnel.IntegrityAlgorithm}")}");
            report.AppendLine($"IMS result: {diag?.Ims?.RegistrationState ?? "not registered"}; security={diag?.Ims?.SecurityAssociation ?? "n/a"}; expiry={diag?.Ims?.ExpiresSeconds.ToString() ?? "n/a"}");
            report.AppendLine($"SIP probes: sent={diag?.SipProbesSent ?? 0}; success={diag?.SipProbesSuccess ?? 0}; failed={diag?.SipProbesFailed ?? 0}; last={diag?.LastProbeResult ?? "none"}");
            report.AppendLine();
            report.AppendLine("=== Latest IMS registration chain ===");
            report.AppendLine("Only the most recent VoWiFi start attempt and its ePDG / IKE / EAP / tunnel / IMS / SIP events are included.");

            try
            {
                var persisted = await ReadTailLinesAsync(GetLogFilePath(), 5000).ConfigureAwait(false);
                var live = Logs.Select(entry =>
                    $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] [{entry.Source}] {entry.Message}");
                var chain = ExtractLatestImsRegistrationChain(persisted.Concat(live));
                foreach (var line in chain)
                    report.AppendLine(DiagnosticLogRedactor.Redact(line));
            }
            catch (Exception ex)
            {
                report.AppendLine($"[WARN] Could not read persisted log: {DiagnosticLogRedactor.Redact(ex.Message)}");
            }

            AddLog("INFO", "Diagnostics", "A concise IMS registration-chain diagnostic report was generated (sensitive fields redacted).");
            return report.ToString();
        }

        private static string DescribeImsiPlmn(string? imsi)
        {
            if (string.IsNullOrWhiteSpace(imsi) || imsi.Length < 5) return "unavailable";
            return $"{imsi[..3]}-{imsi.Substring(3, 2)}…";
        }

        private static IReadOnlyList<string> ExtractLatestImsRegistrationChain(IEnumerable<string> source)
        {
            var allLines = source
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var startIndex = allLines.FindLastIndex(IsImsRegistrationStart);
            // Keep the few setup events immediately before the start marker so
            // the selected SOCKS/direct route is visible with the attempt.
            var attemptLines = startIndex >= 0
                ? allLines.Skip(Math.Max(0, startIndex - 5))
                : allLines;
            var relevant = attemptLines.Where(IsImsRegistrationEvent).ToList();

            if (relevant.Count == 0)
                return ["No VoWiFi registration event was recorded yet. Reproduce the failure once, then export the report immediately."];

            // An ESP-heavy trace can be noisy. Preserve the beginning (DNS/IKE)
            // and the end (the actual failure) while keeping the report shareable.
            const int maxLines = 420;
            if (relevant.Count <= maxLines) return relevant;

            var excerpt = relevant.Take(100).ToList();
            excerpt.Add($"[INFO] [Diagnostics] {relevant.Count - maxLines} registration-chain events omitted; showing setup and final failure.");
            excerpt.AddRange(relevant.TakeLast(maxLines - excerpt.Count));
            return excerpt;
        }

        private static bool IsImsRegistrationStart(string line) =>
            line.Contains("Starting VoWiFi on slot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VoWiFi state: ResolvingEpdg", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("REGISTER diagnostics started", StringComparison.OrdinalIgnoreCase);

        private static bool IsImsRegistrationEvent(string line) =>
            line.Contains("[VoWiFi]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IMS]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[SIP]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IKE", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IPsec", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[Proxy]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[StateMachine]", StringComparison.OrdinalIgnoreCase);

        private static async Task<IReadOnlyList<string>> ReadTailLinesAsync(string path, int maxLines)
        {
            if (!File.Exists(path)) return Array.Empty<string>();

            var lines = new Queue<string>(maxLines);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (lines.Count == maxLines) lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.ToArray();
        }

        private static byte[] BuildDnsHealthQuery(ushort transactionId)
        {
            // Minimal recursive A query for example.com.
            var query = new byte[29];
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2, 2), 0x0100);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);
            query[12] = 7;
            Encoding.ASCII.GetBytes("example").CopyTo(query, 13);
            query[20] = 3;
            Encoding.ASCII.GetBytes("com").CopyTo(query, 21);
            query[24] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(25, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(27, 2), 1);
            return query;
        }

        public void AddProxyPreset(ProxyNodeModel node)
        {
            ProxyPresets.Add(node);
            _ = Preferences.SaveProxyNodeAsync(node);
            AddLog("INFO", "Proxy", $"Added proxy preset: {node.Name} ({node.ToProxyUrl()})");
            RaiseEgressRoutesChanged();
        }

        public void RemoveProxyPreset(string nodeId)
        {
            var item = ProxyPresets.FirstOrDefault(p => p.Id == nodeId);
            if (item != null)
            {
                ProxyPresets.Remove(item);
                _ = Preferences.DeleteProxyNodeAsync(nodeId);
                AddLog("INFO", "Proxy", $"Removed proxy preset: {item.Name}");
                RaiseEgressRoutesChanged();
            }
        }

        public string? ResolveEgressProxyForSlot(string slotId)
        {
            var slot = Slots.FirstOrDefault(s => s.Id == slotId);
            if (slot == null) return null;

            // Tier 1: the existence of an ICCID row always wins. A row without
            // a proxy is an explicit DIRECT rule and must not fall through.
            var iccid = slot.Sim?.Iccid?.Trim();
            if (!string.IsNullOrWhiteSpace(iccid))
            {
                var cardRule = IccidRoutes.FirstOrDefault(route =>
                    string.Equals(route.Iccid, iccid, StringComparison.Ordinal));
                if (cardRule != null)
                {
                    if (!string.IsNullOrWhiteSpace(cardRule.ProxyNodeId))
                    {
                        return ToSupportedSocksProxy(
                            ProxyPresets.FirstOrDefault(node => node.Id == cardRule.ProxyNodeId)?.ToProxyUrl());
                    }

                    // ProxyUrl exists only for a migrated legacy per-SIM URL.
                    return ToSupportedSocksProxy(cardRule.ProxyUrl);
                }
            }

            // Tier 2: without an ICCID row, resolve the country from the active
            // IMSI MCC and match the PLMN country route.
            var mcc = slot.StableRoutingMcc ?? slot.Sim?.Mcc;
            if (!string.IsNullOrWhiteSpace(mcc))
            {
                var country = MccCountryHelper.FindByMcc(mcc);
                if (country != null)
                {
                    var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, country.Code, StringComparison.OrdinalIgnoreCase));
                    if (rule != null && !rule.IsDirect && !string.IsNullOrEmpty(rule.ProxyNodeId))
                    {
                        var node = ProxyPresets.FirstOrDefault(p => p.Id == rule.ProxyNodeId);
                        if (node != null)
                        {
                            return ToSupportedSocksProxy(node.ToProxyUrl());
                        }
                    }
                }
            }

            return null;
        }

        public async Task<VoWifiRouteDecision> ResolveVoWifiRouteAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(candidate => string.Equals(candidate.Id, slotId, StringComparison.OrdinalIgnoreCase))
                : ActiveSlot) ?? Slots.FirstOrDefault();

            if (slot == null)
            {
                return new VoWifiRouteDecision(
                    "未选择通信设备",
                    "未解析",
                    "请先选择已识别的 SIM 卡。",
                    null,
                    false);
            }

            await Task.CompletedTask;
            var iccid = slot.Sim?.Iccid?.Trim();
            var cardRule = string.IsNullOrWhiteSpace(iccid)
                ? null
                : IccidRoutes.FirstOrDefault(route => string.Equals(route.Iccid, iccid, StringComparison.Ordinal));
            if (cardRule != null)
            {
                if (!string.IsNullOrWhiteSpace(cardRule.ProxyNodeId))
                {
                    var node = ProxyPresets.FirstOrDefault(candidate => candidate.Id == cardRule.ProxyNodeId);
                    if (node == null)
                    {
                        return new VoWifiRouteDecision(
                            "ICCID 卡规则（最高优先级）",
                            "直连 UDP（引用节点不存在）",
                            "已命中这张卡的 ICCID 规则，因此不会继续匹配 PLMN；请重新选择有效代理节点。",
                            null,
                            false);
                    }

                    using var parsedNode = Socks5Client.TryParse(node.ToProxyUrl());
                    if (parsedNode == null)
                    {
                        return new VoWifiRouteDecision(
                            "ICCID 卡规则（最高优先级）",
                            "直连 UDP（节点协议不支持）",
                            "已命中 ICCID 规则，但该节点不是 SOCKS5；不会继续匹配 PLMN，也不会回落到系统/TUN 代理。",
                            null,
                            false);
                    }

                    return new VoWifiRouteDecision(
                        "ICCID 卡规则（最高优先级）",
                        $"SOCKS5：{node.Name} ({node.Host}:{node.Port})",
                        $"ICCID {iccid} 已设置专属出口；IMSI MCC/PLMN 国家规则已被覆盖。",
                        node.ToProxyUrl(),
                        true);
                }

                if (!string.IsNullOrWhiteSpace(cardRule.ProxyUrl))
                {
                    return DescribeConfiguredRoute(
                        "ICCID 卡规则（最高优先级，旧版地址）",
                        cardRule.ProxyUrl,
                        $"ICCID {iccid} 已设置专属出口；IMSI MCC/PLMN 国家规则已被覆盖。");
                }

                return new VoWifiRouteDecision(
                    "ICCID 卡规则（最高优先级）",
                    "直连 UDP",
                    $"ICCID {iccid} 明确设为直连，因此不会继续匹配 IMSI MCC/PLMN 国家规则。",
                    null,
                    false);
            }

            // Country dispatch is deliberately based on the IMSI-derived MCC.
            // ICCID is used only to locate the per-SIM preference above; it is
            // not an authoritative country or carrier-routing source.
            var mcc = slot.StableRoutingMcc ?? slot.Sim?.Mcc;
            var country = MccCountryHelper.FindByMcc(mcc);
            if (country == null)
            {
                return new VoWifiRouteDecision(
                    "无 ICCID 卡规则；IMSI MCC 无法识别",
                    "直连 UDP",
                    $"未能由当前 IMSI 解析 MCC（{mcc ?? "--"}），因此没有命中国家分流规则。",
                    null,
                    false);
            }

            var countryRule = CountryRoutes.FirstOrDefault(rule =>
                string.Equals(rule.CountryCode, country.Code, StringComparison.OrdinalIgnoreCase));
            if (countryRule != null && !countryRule.IsDirect && !string.IsNullOrWhiteSpace(countryRule.ProxyNodeId))
            {
                var node = ProxyPresets.FirstOrDefault(candidate => candidate.Id == countryRule.ProxyNodeId);
                if (node != null)
                {
                    using var parsedNode = Socks5Client.TryParse(node.ToProxyUrl());
                    if (parsedNode == null)
                    {
                        return new VoWifiRouteDecision(
                            $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）",
                            "直连 UDP（节点协议不支持）",
                            "国家规则命中的节点不是 SOCKS5；不会回落到系统/TUN 代理。",
                            null,
                            false);
                    }

                    return new VoWifiRouteDecision(
                        $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）",
                        $"SOCKS5：{node.Name} ({node.Host}:{node.Port})",
                        slot.HasImsiPlmnConflict
                            ? "同一 ICCID 曾报告不同 PLMN；为避免出口抖动，国家规则使用首次稳定 MCC。建议为这张卡配置 ICCID 专属规则。"
                            : "未找到 ICCID 卡规则，因此由 IMSI 的 MCC 命中次级 PLMN 规则。",
                        node.ToProxyUrl(),
                        true);
                }

                return new VoWifiRouteDecision(
                    $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）",
                    "直连 UDP",
                    "国家规则引用的代理节点已不存在，因此不会回退到系统/TUN 代理。",
                    null,
                    false);
            }

            var ruleNote = countryRule?.IsDirect == true
                ? "该国家规则明确配置为直连。"
                : "未配置该国家的代理规则。";
            return new VoWifiRouteDecision(
                $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）",
                "直连 UDP",
                slot.HasImsiPlmnConflict
                    ? $"同一 ICCID 曾报告不同 PLMN；国家分流已锁定首次稳定 MCC。{ruleNote} 建议配置 ICCID 专属规则。"
                    : $"未找到 ICCID 卡规则；{ruleNote}",
                null,
                false);
        }

        private static VoWifiRouteDecision DescribeConfiguredRoute(string ruleDisplay, string proxyUrl, string detail)
        {
            using var parsedProxy = Socks5Client.TryParse(proxyUrl);
            if (parsedProxy == null)
            {
                return new VoWifiRouteDecision(
                    ruleDisplay,
                    "直连 UDP（保存的代理无效，已忽略）",
                    $"{detail} 但保存的地址不是受支持的 socks://、socks5:// 或 socks5h:// 代理，VoWiFi 不会使用 HTTP/TUN 代理。",
                    null,
                    false);
            }

            return new VoWifiRouteDecision(
                ruleDisplay,
                $"SOCKS5：{DescribeProxyEndpoint(proxyUrl)}",
                detail,
                proxyUrl,
                true);
        }

        private static string? ToSupportedSocksProxy(string? proxyUrl)
        {
            if (string.IsNullOrWhiteSpace(proxyUrl)) return null;
            using var parsedProxy = Socks5Client.TryParse(proxyUrl);
            return parsedProxy == null ? null : proxyUrl.Trim();
        }

        public void SaveIccidRoute(string iccid, string? proxyNodeId, string? cardName = null, string? imsi = null, string? phoneNumber = null)
        {
            var normalizedIccid = iccid?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedIccid))
                throw new ArgumentException("ICCID 不能为空。", nameof(iccid));

            var node = !string.IsNullOrWhiteSpace(proxyNodeId)
                ? ProxyPresets.FirstOrDefault(candidate => candidate.Id == proxyNodeId)
                : null;
            if (!string.IsNullOrWhiteSpace(proxyNodeId) && node == null)
                throw new InvalidOperationException("所选代理节点不存在或已被删除。");

            var route = IccidRoutes.FirstOrDefault(candidate =>
                string.Equals(candidate.Iccid, normalizedIccid, StringComparison.Ordinal));
            if (route == null)
            {
                route = new IccidRouteModel { Iccid = normalizedIccid };
                IccidRoutes.Add(route);
            }

            route.CardName = string.IsNullOrWhiteSpace(cardName) ? route.CardName : cardName.Trim();
            route.ImsiSnapshot = string.IsNullOrWhiteSpace(imsi) ? route.ImsiSnapshot : imsi.Trim();
            route.PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? route.PhoneNumber : phoneNumber.Trim();
            route.ProxyNodeId = node?.Id;
            route.ProxyUrl = null;
            route.ProxyNodeName = node?.Name ?? "直连模式 (Direct)";
            route.UpdatedAt = DateTime.Now;
            _ = Preferences.SaveIccidRouteAsync(route);

            AddLog("INFO", "Proxy Routing",
                $"Updated ICCID egress rule: ****{normalizedIccid[^Math.Min(4, normalizedIccid.Length)..]} -> {node?.Name ?? "Direct"}");
            RaiseEgressRoutesChanged();
        }

        public void RemoveIccidRoute(string iccid)
        {
            var normalizedIccid = iccid?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedIccid)) return;

            var route = IccidRoutes.FirstOrDefault(candidate =>
                string.Equals(candidate.Iccid, normalizedIccid, StringComparison.Ordinal));
            if (route == null) return;

            IccidRoutes.Remove(route);
            _ = Preferences.DeleteIccidRouteAsync(normalizedIccid);
            AddLog("INFO", "Proxy Routing",
                $"Removed ICCID egress rule: ****{normalizedIccid[^Math.Min(4, normalizedIccid.Length)..]}; the card now follows its IMSI MCC/PLMN rule.");
            RaiseEgressRoutesChanged();
        }

        private void RaiseEgressRoutesChanged()
        {
            try { EgressRoutesChanged?.Invoke(); } catch { }
        }

        public void SaveCountryRoute(string countryCode, string? proxyNodeId)
        {
            var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
            var node = !string.IsNullOrEmpty(proxyNodeId) ? ProxyPresets.FirstOrDefault(p => p.Id == proxyNodeId) : null;

            if (rule != null)
            {
                rule.ProxyNodeId = proxyNodeId;
                rule.ProxyNodeName = node != null ? $"{node.Name} ({node.Host}:{node.Port})" : "直连模式 (Direct)";
            }
            else
            {
                var meta = MccCountryHelper.FindByCode(countryCode);
                if (meta != null)
                {
                    rule = new CountryRouteModel
                    {
                        CountryCode = meta.Code,
                        CountryName = meta.Name,
                        FlagEmoji = meta.Flag,
                        MccList = string.Join(", ", meta.Mccs),
                        ProxyNodeId = proxyNodeId,
                        ProxyNodeName = node != null ? $"{node.Name} ({node.Host}:{node.Port})" : "直连模式 (Direct)"
                    };
                    CountryRoutes.Add(rule);
                }
            }

            if (rule != null)
            {
                _ = Preferences.SaveCountryRouteAsync(rule);
            }

            AddLog("INFO", "Proxy Routing", $"Updated country egress rule: {countryCode} -> {(node != null ? node.Name : "Direct")}");
            RaiseEgressRoutesChanged();
        }

        public void RemoveCountryRoute(string countryCode)
        {
            var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
            if (rule != null)
            {
                CountryRoutes.Remove(rule);
                _ = Preferences.DeleteCountryRouteAsync(countryCode);
                AddLog("INFO", "Proxy Routing", $"Removed country rule: {countryCode}");
                RaiseEgressRoutesChanged();
            }
        }

        public void OnUsbDeviceInserted()
        {
            if (BeginUsbDebounce() is not { } token) return;
            AddLog("INFO", "USB Monitor", "检测到 USB 设备插入事件，等待串口驱动完成初始化...");
            _ = HandleUsbDeviceInsertedAsync(token);
        }

        public void OnUsbDeviceRemoved()
        {
            if (BeginUsbDebounce() is not { } token) return;
            AddLog("INFO", "USB Monitor", "检测到 USB 设备拔出事件，正在保留 VoWiFi 会话并释放硬件句柄...");
            _ = HandleUsbDeviceRemovedAsync(token);
        }

        private CancellationToken? BeginUsbDebounce()
        {
            lock (_usbDebounceGate)
            {
                if (Volatile.Read(ref _serviceDisposed) != 0) return null;
                try { _usbDebounceCts?.Cancel(); } catch (ObjectDisposedException) { }
                _usbDebounceCts?.Dispose();
                _usbDebounceCts = new CancellationTokenSource();
                return _usbDebounceCts.Token;
            }
        }

        private void StopUsbDebounce()
        {
            lock (_usbDebounceGate)
            {
                var source = _usbDebounceCts;
                _usbDebounceCts = null;
                try { source?.Cancel(); } catch (ObjectDisposedException) { }
                source?.Dispose();
            }
        }

        private async Task HandleUsbDeviceRemovedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                if (Volatile.Read(ref _serviceDisposed) != 0) return;
                bool retryBusyDevices;
                do
                {
                    if (Volatile.Read(ref _serviceDisposed) != 0) return;
                    var availablePorts = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    retryBusyDevices = await DetachMissingModemsAsync(availablePorts, token,
                        () => SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);
                    if (Volatile.Read(ref _serviceDisposed) != 0) return;
                    token.ThrowIfCancellationRequested();
                    await RunOnUiAsync(SyncSlots, DispatcherPriority.DataBind).ConfigureAwait(false);
                    // An insertion or shutdown cancels this removal intent. Do
                    // not wait behind an operation and then replay its old port
                    // snapshot; every retry observes the current USB inventory.
                    if (retryBusyDevices) await Task.Delay(300, token).ConfigureAwait(false);
                } while (retryBusyDevices);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AddLog("ERROR", "USB Monitor", $"USB 拔出处理失败: {ex.Message}");
            }
        }

        internal async Task<bool> DetachMissingModemsAsync(IReadOnlySet<string> availablePorts, CancellationToken token,
            Func<IReadOnlySet<string>>? readCurrentPorts = null)
        {
            token.ThrowIfCancellationRequested();
            var retryBusyDevices = false;
            foreach (var slot in Kernel.GetSlots().Where(slot => !slot.IsPcscReader &&
                !slot.IsHostImsPppTransitionOrActive && !IsTelephonyTransitioning(slot.Id) &&
                !availablePorts.Contains(slot.PortName)))
            {
                if (Volatile.Read(ref _serviceDisposed) != 0) return false;
                token.ThrowIfCancellationRequested();
                IAsyncDisposable? lease;
                try { lease = await Kernel.Pool.AcquireSlotLeaseAsync(slot.Id, timeoutMs: 0, ct: token).ConfigureAwait(false); }
                catch (ObjectDisposedException) when (Volatile.Read(ref _serviceDisposed) != 0) { return false; }
                if (lease is null)
                {
                    if (Kernel.Pool.Slots.ContainsKey(slot.Id)) retryBusyDevices = true;
                    continue;
                }
                await using (lease)
                {
                    if (!Kernel.Pool.Slots.TryGetValue(slot.Id, out var current) || !ReferenceEquals(current, slot)) continue;
                    var instance = GetModuleInstance(slot.Id);
                    if (!await instance.ControlGate.WaitAsync(0, token).ConfigureAwait(false))
                    {
                        retryBusyDevices = true;
                        continue;
                    }
                    try
                    {
                        if (Volatile.Read(ref _serviceDisposed) != 0) return false;
                        token.ThrowIfCancellationRequested();
                        if (slot.IsHostImsPppTransitionOrActive || IsTelephonyTransitioning(slot.Id)) continue;
                        var currentPorts = readCurrentPorts?.Invoke() ?? availablePorts;
                        token.ThrowIfCancellationRequested();
                        if (currentPorts.Contains(slot.PortName)) continue;
                        await slot.DetachHardwareAsync(token).ConfigureAwait(false);
                        if (Volatile.Read(ref _serviceDisposed) != 0) return false;
                        token.ThrowIfCancellationRequested();
                        if (slot.Modem is null && slot.State == SlotState.Offline)
                            AddLog("INFO", "USB Monitor",
                                $"模块 [{slot.Name}] 已拔出；现有 VoWiFi 隧道将继续保活，重新插入后会自动重新注册。");
                        else if (!slot.IsHostImsPppTransitionOrActive && !IsTelephonyTransitioning(slot.Id))
                            retryBusyDevices = true; // A direct profile operation may own the slot's hardware gate.
                    }
                    finally { instance.ControlGate.Release(); }
                }
            }
            return retryBusyDevices;
        }

        private async Task HandleUsbDeviceInsertedAsync(CancellationToken token)
        {
            try
            {
                if (!_telephonySettings.AutoDiscoverModems) return;
                await Task.Delay(800, token).ConfigureAwait(false);
                if (Volatile.Read(ref _serviceDisposed) != 0) return;
                AddLog("INFO", "USB Monitor", "开始扫描新插入设备提供的串口...");

                var discovered = await DiscoverSlotsCoreAsync(token).ConfigureAwait(false);
                await RunOnUiAsync(SyncSlots, DispatcherPriority.DataBind).ConfigureAwait(false);
                AddLog("INFO", "USB Monitor", $"USB 扫描完成，当前卡槽数: {discovered.Count}");

                foreach (var slot in discovered)
                {
                    await ApplyPreferencesToSlotAsync(slot).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "USB Monitor", $"USB 设备扫描失败: {ex.Message}");
            }
        }

        public Task ApplyPreferencesToSlotAsync(ModemSlot slot) =>
            ApplyPreferencesToSlotAsync(slot, restoreAutoVoWifi: true);

        private async Task ApplyPreferencesToSlotAsync(
            ModemSlot slot,
            bool restoreAutoVoWifi,
            bool restoreRadioPreferences = true)
        {
            try
            {
                var modPref = await Preferences.GetModulePreferenceAsync(slot.Id, slot.Imei);
                SimPreferenceModel? simPref = null;
                if (slot.Sim != null && !string.IsNullOrEmpty(slot.Sim.Iccid))
                {
                    simPref = await Preferences.GetSimPreferenceAsync(slot.Sim.Iccid);
                }

                if (modPref != null && !string.IsNullOrWhiteSpace(modPref.CustomName))
                {
                    slot.Name = modPref.CustomName.Trim();
                }
                if (slot.IsPcscReader && modPref?.Imei is { Length: > 0 } configuredImei)
                {
                    // ModulePreferences.Imei historically stores the modem's
                    // AT+CGSN value. For a PC/SC-only slot it stores the user
                    // supplied genuine terminal identity used by IMS.
                    slot.VoWifiImei = configuredImei;
                }
                slot.CardNickname = simPref?.CardNickname;

                // Routing is resolved from exactly two persistent tiers:
                // ICCID card rule first, then IMSI MCC/PLMN country rule.
                // Legacy module/default proxy fields are deliberately ignored
                // here so they cannot silently override the visible rule table.
                var resolvedProxy = ResolveEgressProxyForSlot(slot.Id);
                if (!Kernel.SetSlotProxy(slot.Id, resolvedProxy))
                {
                    Kernel.SetSlotProxy(slot.Id, null);
                    slot.ProxyUrl = null;
                    AddLog("WARN", "Proxy",
                        $"卡槽 [{slot.Name}] 命中的分流代理无效，已安全回退到直连；VoWiFi 仅允许 socks://、socks5:// 或 socks5h://。");
                }
                else
                {
                    slot.ProxyUrl = resolvedProxy;
                    AddLog("INFO", "Proxy", resolvedProxy is null
                        ? $"已按 ICCID > IMSI MCC/PLMN 规则解析卡槽 [{slot.Name}]：直连。"
                        : $"已按 ICCID > IMSI MCC/PLMN 规则解析卡槽 [{slot.Name}]：{DescribeProxyEndpoint(resolvedProxy)}");
                }

                ApplyVoWifiHomeIdentity(slot, simPref);

                var managedMode = await GetTelephonyPreferencesAsync(slot.Id).ConfigureAwait(false);
                var hostRecoveryOwned = GetModuleInstance(slot.Id).HostRecoveryStatus?.KeepOnlineRequested == true;
                if (!IsTelephonyTransitioning(slot.Id) && !hostRecoveryOwned)
                {
                    slot.PreferredTelephonyMode = managedMode.DefaultRoute;
                    slot.CellularImsMode ??= managedMode.CellularMode;
                }
                // Host handoff deliberately reopens the modem and re-reads the
                // SIM. Those READY events must not restore CGATT/CFUN mid-lease.
                if (slot.IsHostImsPppTransitionOrActive || IsTelephonyTransitioning(slot.Id) || hostRecoveryOwned)
                    restoreRadioPreferences = false;

                // A SIM preference follows the card and overrides the module
                // default. Restore RF state before starting any background work
                // that could report cellular registration or signal state.
                // Radio/data/VoWiFi switches belong to the SIM, never to the
                // physical slot.  A SIM without a saved record starts with all
                // switches off so moving it to another modem is predictable.
                bool? preferredFlightMode = simPref?.DefaultFlightMode ?? false;
                if (restoreRadioPreferences && !slot.IsPcscReader && preferredFlightMode.HasValue && slot.IsFlightMode != preferredFlightMode.Value)
                {
                    try
                    {
                        var applied = await slot.SetFlightModeAsync(preferredFlightMode.Value).ConfigureAwait(false);
                        if (!applied)
                        {
                            AddLog("WARN", "Preferences", $"无法为卡槽 [{slot.Name}] 恢复飞行模式偏好。");
                        }
                        else
                        {
                            AddLog("INFO", "Preferences", preferredFlightMode.Value
                                ? $"已为卡槽 [{slot.Name}] 恢复飞行模式，蜂窝射频已关闭。"
                                : $"已为卡槽 [{slot.Name}] 恢复正常模式，蜂窝射频已开启。");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", "Preferences", $"恢复飞行模式偏好失败，继续处理其他设置: {ex.Message}");
                    }
                }

                if (restoreRadioPreferences && !slot.IsPcscReader && !slot.IsFlightMode)
                {
                    bool? preferredRoaming = simPref?.DefaultDataRoaming ?? false;
                    if (preferredRoaming.HasValue)
                    {
                        try
                        {
                            var applied = await slot.SetDataRoamingEnabledAsync(preferredRoaming.Value).ConfigureAwait(false);
                            AddLog(applied ? "INFO" : "WARN", "Preferences", applied
                                ? $"已为卡槽 [{slot.Name}] 恢复数据漫游偏好: {(preferredRoaming.Value ? "允许" : "禁止")}。"
                                : $"模组未确认数据漫游偏好，可能不支持 Quectel 漫游指令。");
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "Preferences", $"恢复数据漫游偏好失败，继续处理其他设置: {ex.Message}");
                        }
                    }

                    bool? preferredCellularData = ResolveCellularDataStartupPreference(simPref, managedMode);
                    if (preferredCellularData.HasValue)
                    {
                        try
                        {
                            var applied = await slot.SetCellularDataEnabledAsync(preferredCellularData.Value).ConfigureAwait(false);
                            AddLog(applied ? "INFO" : "WARN", "Preferences", applied
                                ? $"已为卡槽 [{slot.Name}] 恢复蜂窝数据偏好: {(preferredCellularData.Value ? "开启" : "关闭")}。"
                                : $"模组未确认蜂窝数据偏好 (CGATT)。");
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "Preferences", $"恢复蜂窝数据偏好失败，继续处理 VoWiFi: {ex.Message}");
                        }
                    }

                    try { await slot.RefreshMetricsAsync().ConfigureAwait(false); }
                    catch (Exception ex) { AddLog("WARN", "Preferences", $"偏好恢复后的网络刷新失败: {ex.Message}"); }
                }

                if (restoreAutoVoWifi)
                {
                    QueueRestoreTelephonyMode(slot, managedMode);
                }

                if (!slot.IsHostImsPppTransitionOrActive && !IsTelephonyTransitioning(slot.Id) && !hostRecoveryOwned)
                    _ = SyncModemSmsAsync(slot);
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Preferences", $"Apply preferences failed: {ex.Message}");
            }
        }

        private static string DescribeProxyEndpoint(string proxyUrl)
        {
            if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri))
                return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            return proxyUrl;
        }

        internal static bool ResolveCellularDataStartupPreference(SimPreferenceModel? saved, SimTelephonyPreferences managed) =>
            saved?.DefaultCellularData ?? managed.AutoConnectCellular;

        private void ApplyVoWifiHomeIdentity(ModemSlot slot, SimPreferenceModel? simPref)
        {
            var reported = slot.Sim;
            var savedImsi = simPref?.Imsi?.Trim();
            if (reported == null || string.IsNullOrWhiteSpace(savedImsi) ||
                savedImsi.Equals(reported.Imsi, StringComparison.Ordinal))
            {
                return;
            }

            // The preference record is selected by the live ICCID. Preserve a
            // previously observed permanent IMSI for a multi-IMSI profile without
            // consulting a bundled carrier/ICCID table.
            if (savedImsi.Length is < 5 or > 16 || !savedImsi.All(char.IsAsciiDigit))
                return;
            var learnedIdentity = SimIdentity.FromImsiAndIccid(
                savedImsi,
                reported.Iccid,
                reported.OperatorName,
                reported.PhoneNumber,
                null, // Another IMSI's EF_AD length is not proof for this learned identity.
                reported.HomePlmns);
            slot.RememberVoWifiIdentity(learnedIdentity);
            AddLog("INFO", "VoWiFi", $"卡槽 [{slot.Name}] 发现同一 ICCID 的另一条历史 IMSI，已加入回退候选；认证结果尚待验证。");
        }

        private void QueueAutoVoWifiStart(ModemSlot slot)
        {
            var preferenceKey = GetVoWifiPreferenceKey(slot);
            if (_voWifiDesiredStates.TryGetValue(preferenceKey, out var desired) && !desired) return;
            _voWifiDesiredStates.TryAdd(preferenceKey, true);
            if (slot.VoWifi.State is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka or
                VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering or VoWifiState.ImsRegistered)
                return;
            if (!_autoVoWifiStarts.TryAdd(slot.Id, 0)) return;
            var expectedIntentVersion = GetVoWifiIntentVersion(slot.Id);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (GetVoWifiIntentVersion(slot.Id) != expectedIntentVersion ||
                        (_voWifiDesiredStates.TryGetValue(preferenceKey, out var stillDesired) && !stillDesired)) return;
                    AddLog("INFO", "VoWiFi", $"卡槽 [{slot.Name}] 已启用自动 VoWiFi，正在注册…");
                    var started = await StartVoWifiCoreAsync(slot, expectedIntentVersion).ConfigureAwait(false);
                    if (!started)
                        AddLog("WARN", "VoWiFi", $"卡槽 [{slot.Name}] 自动 VoWiFi 注册失败，请查看 VoWiFi 日志。 ");
                }
                catch (Exception ex)
                {
                    AddLog("WARN", "VoWiFi", $"卡槽 [{slot.Name}] 自动 VoWiFi 启动异常: {ex.Message}");
                }
                finally
                {
                    _autoVoWifiStarts.TryRemove(slot.Id, out _);
                }
            });
        }

        public async Task SyncModemSmsAsync(ModemSlot slot, CancellationToken cancellationToken = default)
        {
            if (slot.IsPcscReader)
            {
                // Inbound/outbound SMS uses the IMS SIP path for a PC/SC slot;
                // there is no modem message store to enumerate.
                return;
            }
            if (slot?.Sms == null) return;
            if (!_syncingSlots.TryAdd(slot.Id, 0)) return;

            AddLog("INFO", "SMS", $"Starting SMS synchronization for slot [{slot.Name}] ({slot.Id})...");
            try
            {
                // 1. Fetch all SMS stored on the module (both SIM and module memory)
                var messages = await slot.Sms.ListSmsAsync(SmsStatus.All, cancellationToken).ConfigureAwait(false);
                AddLog("INFO", "SMS", $"Found {messages.Count} stored messages on modem [{slot.Name}].");

                int importedCount = 0;
                foreach (var msg in messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool isOutgoing = msg.Direction == SmsDirection.Submitted;
                    var remote = !string.IsNullOrWhiteSpace(msg.SenderOrRecipient) ? msg.SenderOrRecipient : "Unknown";
                    var normalizedTimestamp = isOutgoing
                        ? TimestampDisplayHelper.ToUtcStorageTime(msg.Timestamp)
                        : TimestampDisplayHelper.NormalizeIncomingNetworkTime(msg.Timestamp);
                    if (isOutgoing && await Preferences.HasSmsMessageAsync(remote, normalizedTimestamp, msg.Text, msg.RawPdu).ConfigureAwait(false))
                        continue; // Keep the existing sent-history import behavior.

                    // Historical modem storage has no receive-session card proof.
                    // It must remain unbound even if a different SIM is now inserted.
                    {
                        var model = new SmsMessageModel
                        {
                            Index = msg.Index,
                            SlotId = slot.Id,
                            SenderOrRecipient = remote,
                            Text = msg.Text,
                            Timestamp = normalizedTimestamp,
                            IsOutgoing = isOutgoing,
                            DeliveryState = isOutgoing ? SmsDeliveryState.Sent : SmsDeliveryState.Received,
                            DeliveryStatus = msg.DeliveryStatus,
                            MessageReference = msg.MessageReference,
                            RawPdu = msg.RawPdu,
                            SourceMessageId = msg.Id,
                            SimBinding = null
                        };

                        if (isOutgoing) await Preferences.SaveSmsMessageAsync(model).ConfigureAwait(false);
                        else if (!await Preferences.SaveIncomingSmsIfNewAsync(model, cancellationToken).ConfigureAwait(false)) continue;

                        await RunOnUiAsync(() =>
                        {
                            var conv = GetOrCreateConversation(remote);
                            if (!conv.Messages.Any(m => m.Id == model.Id))
                            {
                                conv.Messages.Add(model);
                                if (!model.IsOutgoing)
                                {
                                    conv.UnreadCount++;
                                }
                                conv.UpdateLastMessage();
                                MoveConversationToTop(conv);
                            }
                        }).ConfigureAwait(false);

                        importedCount++;
                    }
                }

                if (importedCount > 0)
                {
                    AddLog("INFO", "SMS", $"Imported {importedCount} new messages from modem [{slot.Name}] into local SQLite.");
                }

                // 2. Delete all messages from modem storages to prevent card storage from filling up
                if (slot.Modem != null && slot.Modem.IsOpen)
                {
                    var storages = new[] { "\"SM\",\"SM\",\"SM\"", "\"ME\",\"ME\",\"ME\"" };
                    foreach (var st in storages)
                    {
                        try
                        {
                            await slot.Modem.SendRawAtCommandAsync($"AT+CPMS={st}", 2000, cancellationToken).ConfigureAwait(false);
                            var delResp = await slot.Modem.SendRawAtCommandAsync("AT+CMGD=1,4", 5000, cancellationToken).ConfigureAwait(false);
                            if (!delResp.Success)
                            {
                                foreach (var m in messages)
                                {
                                    if (m.Index > 0)
                                    {
                                        await slot.Modem.SendRawAtCommandAsync($"AT+CMGD={m.Index}", 2000, cancellationToken).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "SMS", $"Clear storage {st} failed: {ex.Message}");
                        }
                    }

                    // Restore preferred storage to ME
                    try
                    {
                        await slot.Modem.SendRawAtCommandAsync("AT+CPMS=\"ME\",\"ME\",\"ME\"", 2000, cancellationToken).ConfigureAwait(false);
                    }
                    catch { }

                    AddLog("INFO", "SMS", $"Modem [{slot.Name}] SMS storage cleaned up.");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "SMS", $"Modem SMS sync failed for [{slot.Name}]: {ex.Message}");
            }
            finally
            {
                _syncingSlots.TryRemove(slot.Id, out _);
            }
        }

        public async Task SaveModulePreferencesAsync(string slotId, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? customName = null)
        {
            var slot = Kernel.GetSlots().FirstOrDefault(s => s.Id == slotId);
            var normalizedName = string.IsNullOrWhiteSpace(customName) ? null : customName.Trim();
            var model = new ModulePreferenceModel
            {
                Id = slotId,
                Imei = slot?.IsPcscReader == true ? slot.VoWifiImei : slot?.Imei,
                PortName = slot?.PortName,
                CustomName = normalizedName,
                DefaultFlightMode = flightMode,
                DefaultVoWifi = vowifi,
                DefaultCellularData = cellularData,
                DefaultDataRoaming = roaming,
                DefaultProxyUrl = proxyUrl,
                BaudRate = slot?.BaudRate ?? 115200,
                LastSeenAt = DateTime.Now
            };
            await Preferences.SaveModulePreferenceAsync(model);
            if (slot != null)
            {
                slot.Name = normalizedName ?? $"Slot {slot.Id} ({slot.PortName})";
            }
            AddLog("INFO", "Preferences", $"Saved module preference for [{slotId}]");
        }

        private async Task PersistRuntimeIntentAsync(
            ModemSlot slot,
            bool? flightMode = null,
            bool? voWifi = null)
        {
            var sim = slot.Sim;
            if (sim == null || string.IsNullOrWhiteSpace(sim.Iccid)) return;

            await _runtimePreferenceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var existing = await Preferences.GetSimPreferenceAsync(sim.Iccid).ConfigureAwait(false)
                    ?? new SimPreferenceModel { Iccid = sim.Iccid };
                existing.Imsi = sim.Imsi;
                existing.DefaultFlightMode = flightMode ?? existing.DefaultFlightMode;
                existing.DefaultVoWifi = voWifi ?? existing.DefaultVoWifi;
                existing.LastSeenAt = DateTime.Now;
                await Preferences.SaveSimPreferenceAsync(existing).ConfigureAwait(false);
            }
            finally
            {
                _runtimePreferenceGate.Release();
            }
        }

        public async Task SaveSimPreferencesAsync(string iccid, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? nickname)
        {
            var normalizedNickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
            var slot = Kernel.GetSlots().FirstOrDefault(s =>
                string.Equals(s.Sim?.Iccid, iccid, StringComparison.Ordinal));
            await _runtimePreferenceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var model = await Preferences.GetSimPreferenceAsync(iccid).ConfigureAwait(false)
                    ?? new SimPreferenceModel { Iccid = iccid };
                model.Imsi = slot?.Sim?.Imsi ?? model.Imsi;
                model.CardNickname = normalizedNickname;
                model.DefaultFlightMode = flightMode;
                model.DefaultVoWifi = vowifi;
                model.DefaultCellularData = cellularData;
                model.DefaultDataRoaming = roaming;
                model.DedicatedProxyUrl = proxyUrl;
                model.LastSeenAt = DateTime.Now;
                await Preferences.SaveSimPreferenceAsync(model).ConfigureAwait(false);
            }
            finally
            {
                _runtimePreferenceGate.Release();
            }
            if (slot != null)
            {
                slot.CardNickname = normalizedNickname;
            }
            AddLog("INFO", "Preferences", $"Saved sim preference for [{iccid}]");
        }

        private int _serviceDisposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _serviceDisposed, 1) != 0) return;
            Kernel.Pool.PrepareForApplicationExit();
            _discoveryShutdown.Cancel();
            StopUsbDebounce(); // Ignore USB re-enumeration generated by releasing the IMS owner below.
            CancelIncomingAutoAnswer();
            _autoAnswerMessages.Clear();
            // Cancel foreground starts before awaiting recovery/pool gates.
            foreach (var instance in _moduleInstances.Values)
            {
                try { instance.WifiCancellation?.Cancel(); } catch (ObjectDisposedException) { }
                try { instance.CellularCancellation?.Cancel(); } catch (ObjectDisposedException) { }
                instance.HostRecovery?.Cancel();
            }
            foreach (var cancellation in _pendingDialCancellations.Values)
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var instance in _moduleInstances.Values)
                await StopHostImsRecoveryAsync(instance).ConfigureAwait(false);
            foreach (var instance in _moduleInstances.Values) instance.Dispose();

            foreach (var media in _cellularMedia.Values) media.Cancel();
            await _desktopAudioTransition.WaitAsync().ConfigureAwait(false);
            try
            {
                StopMicrophone();
                if (_autoAnswerPlaybackTask is { } playback)
                {
                    try { await playback.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (Exception ex) { AddLog("WARN", "AutoAnswerAudio", $"Recording shutdown incomplete ({ex.GetType().Name})."); }
                }
                foreach (var media in _cellularMedia.Values)
                    await StopCellularModuleAudioAsync(media.SlotId).ConfigureAwait(false);
            }
            finally { _desktopAudioTransition.Release(); }
            foreach (var media in _cellularMedia.Values)
            {
                try { await media.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { AddLog("WARN", "CellularAudio", $"{media.AtPort}: Shutdown cleanup failed: {ex.Message}"); }
            }
            _cellularMedia.Clear();

            try
            {
                await Kernel.DisposeAsync();
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Shutdown", $"Kernel cleanup incomplete ({ex.GetType().Name}); ownership will not be released as if shutdown succeeded.");
                throw;
            }
            // Keep shutdown/deregistration evidence until the kernel has released
            // its modem leases; completing the writer first lost these events.
            _logLines.Writer.TryComplete();
            try { await _logWriterTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { _logWriterCts.Cancel(); }
            _logWriterCts.Dispose();
            _diagnosticSamplerCts.Cancel();
            await _diagnosticSampler.ConfigureAwait(false);
            _diagnosticSamplerCts.Dispose();
            await _diagnosticRecorder.DisposeAsync().ConfigureAwait(false);
            _discoveryShutdown.Dispose();
        }
        private void StartMicrophone()
        {
            string? callId, slotId;
            lock (_cellularSipReservationGate)
            {
                if (_desktopAudioKind != DesktopAudioKind.Microphone) return;
                callId = _desktopAudioCallId; slotId = _desktopAudioSlotId;
            }
            if (callId == null) return;
            StopMicrophone();
            DesktopMicrophoneSession? session = null;
            try
            {
                var hostInput = CellularAudioWorker.FindHostInputDevice();
                if (hostInput < 0)
                    throw new InvalidOperationException("No host microphone is available for IMS audio.");
                var deviceName = NAudio.Wave.WaveIn.GetCapabilities(hostInput).ProductName;
                session = new DesktopMicrophoneSession(callId, slotId, deviceName,
                    pcm => Kernel is VoKernel kernel && kernel.TrySendPresentedImsPcm(callId, slotId, pcm));
                var calls = slotId == null ? null : Kernel.Pool.Slots.GetValueOrDefault(slotId)?.Calls;
                if (calls?.ActiveCall?.CallId == callId && calls.GetMediaStats() is { } stats)
                    session.SetTransportBaseline(stats);
                Volatile.Write(ref _microphoneSession, session);
                _waveIn = new NAudio.Wave.WaveIn
                {
                    DeviceNumber = hostInput,
                    WaveFormat = new NAudio.Wave.WaveFormat(8000, 16, 1),
                    BufferMilliseconds = 20
                };
                var owner = session;
                _waveIn.DataAvailable += (s, a) => owner.Process(a.Buffer, a.BytesRecorded);
                _waveIn.RecordingStopped += (s, e) =>
                {
                    owner.CaptureStopped(e.Exception);
                    if (owner.Snapshot().Error is { } error)
                        AddLog("WARN", "AudioUplink", $"Microphone capture stopped unexpectedly ({error}); no audio samples logged.");
                };
                _waveIn.StartRecording();
                session.Started();
                AddLog("INFO", "Audio", $"Microphone capture started: {deviceName}; WinMM input={hostInput}; 8000Hz/16bit/mono.");
            }
            catch (Exception ex)
            {
                session ??= new DesktopMicrophoneSession(callId, slotId, "麦克风不可用", _ => false);
                session.CaptureStopped(ex);
                Volatile.Write(ref _microphoneSession, session);
                var failedCapture = _waveIn; _waveIn = null;
                try { failedCapture?.Dispose(); } catch { }
                AddLog("WARN", "Audio", $"Failed to start microphone ({ex.GetType().Name}).");
            }
        }

        private void StopMicrophone()
        {
            var session = Volatile.Read(ref _microphoneSession);
            if (session != null)
            {
                var status = GetMicrophoneUplinkStatus();
                session.Close(); // Before native Stop: late callbacks must be ignored.
                AddLog("INFO", "AudioUplink", $"Desktop audio source ended: recording={status.IsMessagePlayback}; {status.DetailText}; peak={status.InputPeak}; {status.StatusText}; no audio samples logged.");
                Volatile.Write(ref _microphoneSession, null);
            }
            var playbackCts = Interlocked.Exchange(ref _autoAnswerPlaybackCts, null);
            try { playbackCts?.Cancel(); } catch (ObjectDisposedException) { }
            if (_waveIn != null)
            {
                var capture = _waveIn;
                _waveIn = null;
                try
                {
                    capture.StopRecording();
                }
                catch { }
                finally { try { capture.Dispose(); } catch { } }
                AddLog("INFO", "Audio", "Microphone capture stopped.");
            }
        }

        public Task<CallExperienceSettings> GetCallExperienceSettingsAsync() => Task.FromResult(new CallExperienceSettings
        {
            VolteAudioPrewarmEnabled = _callExperience.VolteAudioPrewarmEnabled,
            SaveCallRecordings = _callExperience.SaveCallRecordings,
            RecordingDirectory = _callExperience.RecordingDirectory,
            AutoAnswerEnabled = _callExperience.AutoAnswerEnabled,
            AutoAnswerDelaySeconds = _callExperience.AutoAnswerDelaySeconds,
            AutoAnswerMessagePath = _callExperience.AutoAnswerMessagePath
        });

        public async Task SaveCallExperienceSettingsAsync(CallExperienceSettings settings)
        {
            settings.AutoAnswerDelaySeconds = Math.Clamp(settings.AutoAnswerDelaySeconds, 5, 120);
            _callExperience = settings;
            await _callExperienceStore.SaveAsync(settings);
            ApplyVolteAudioPrewarmPreference();
            ApplyCallRecordingPreference();
        }

        public SipGatewayStatus? GetSipGatewayStatus() => Kernel.LocalSipGateway?.Status;

        public async Task StartSipGatewayAsync(SipGatewayOptions options, CancellationToken cancellationToken = default)
        {
            if (Kernel.ActiveCall?.State is CallState.Incoming or CallState.Ringing or CallState.Dialing or CallState.Active or CallState.Held)
                throw new InvalidOperationException("请在当前通话结束后启用 SIP 对接。");
            // Persist paid SIP SMS admission in the same durable database as
            // the desktop inbox. Never silently fall back to process memory.
            var smsStore = Preferences as ISipSmsOperationStore;
            if (options.AllowOutgoingSms && smsStore is null)
                throw new InvalidOperationException("短信防重存储不可用，未启用收费短信发送。");
            var forwardStore = Preferences as ISipSmsForwardStore;
            if (options.MessagesEnabled && options.ForwardIncomingSms && forwardStore is null)
                throw new InvalidOperationException("短信持久转发存储不可用，未启用收件转发。");
            await Kernel.StartSipGatewayAsync(options with { LocalIncomingEnabled = true, SmsOperationStore = smsStore,
                SmsForwardStore = forwardStore }, cancellationToken).ConfigureAwait(false);
            if (Kernel.LocalSipGateway is { } gateway)
                gateway.SmsSubmitted += (_, submission) => _ = RecordSipSmsSubmissionAsync(submission);
            var endpoint = Kernel.LocalSipGateway?.Status.LocalEndPoint;
            AddLog("INFO", "SipGateway", $"Private SIP bridge started on {endpoint}.");
        }

        public async Task StopSipGatewayAsync()
        {
            await Kernel.StopSipGatewayAsync().ConfigureAwait(false);
            AddLog("INFO", "SipGateway", "Private SIP bridge stopped.");
        }

        private void ApplyVolteAudioPrewarmPreference()
        {
            if (!_callExperience.VolteAudioPrewarmEnabled)
            {
                _callAlerting.ReleaseHostAudio();
                return;
            }

            if (CurrentCallState is CallState.Idle or CallState.Ended or CallState.Incoming)
                _callAlerting.PrewarmHostAudio();
        }

        private void ApplyCallRecordingPreference()
        {
            var directory = _callExperience.RecordingDirectory;
            Kernel.Calls.ConfigureAudioRecording(_callExperience.SaveCallRecordings, directory);
            foreach (var slot in Kernel.GetSlots())
                slot.Calls.ConfigureAudioRecording(_callExperience.SaveCallRecordings, directory);
        }

        private void StartIncomingAlertingAndAutoAnswer(string? slotId)
        {
            if (Volatile.Read(ref _incomingAutoAnswerCts) != null) return;
            ApplyVolteAudioPrewarmPreference();
            _callAlerting.StartRinging();
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _incomingAutoAnswerCts, cts, null) != null) { cts.Dispose(); return; }
            if (!_callExperience.AutoAnswerEnabled) return;
            var callId = Kernel.ActiveCall?.CallId;
            if (callId == null) return;
            var token = cts.Token;
            var delaySeconds = _callExperience.AutoAnswerDelaySeconds;
            var message = _callExperience.AutoAnswerMessagePath;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                    if (token.IsCancellationRequested || !HasIncomingCall || CurrentCallState != CallState.Incoming ||
                        !string.Equals(Kernel.ActiveCall?.CallId, callId, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Kernel.ActiveCallSlotId, slotId, StringComparison.OrdinalIgnoreCase)) return;
                    if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Auto-answer recording is not configured.");
                    CallMessagePlayback.Validate(message); // Fail before pickup, not a silently connected call.
                    AddLog("INFO", "Calls", "Incoming call timed out; answering automatically.");
                    await AnswerCoreAsync(slotId, callId, message, token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AddLog("WARN", "Calls", $"Auto-answer failed ({ex.GetType().Name})."); }
            });
        }

        private void CancelIncomingAutoAnswer()
        {
            var cts = Interlocked.Exchange(ref _incomingAutoAnswerCts, null);
            cts?.Cancel();
            cts?.Dispose();
        }

        private void StopIncomingAlerting()
        {
            CancelIncomingAutoAnswer();
            _callAlerting.StopRinging();
        }

        private async Task EnsureDesktopMediaForCallAsync(CallConnectedEventArgs call)
        {
            if (Volatile.Read(ref _serviceDisposed) != 0) return;
            await _desktopAudioTransition.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _serviceDisposed) != 0) return;
                if (!string.Equals(Kernel.ActiveCall?.CallId, call.CallId, StringComparison.OrdinalIgnoreCase)) return;
                if (call.Codec?.StartsWith("Cellular", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (IsReserved(call.SlotId))
                    {
                        AddLog("INFO", "CellularAudio", "Cellular UAC is reserved for the SIP/RTP bridge; desktop microphone/speaker startup was skipped.");
                        return;
                    }
                    if (!TryClaimDesktopAudio(call.CallId, call.SlotId, DesktopAudioKind.CellularUsb)) return;
                    _callAlerting.ReleaseHostAudio();
                    var keepAudio = false;
                    try
                    {
                        var started = await StartCellularCallAudioAsync(
                            call.Codec.Contains("Cellular/UAC", StringComparison.OrdinalIgnoreCase),
                            _autoAnswerMessages.Take(call.CallId, call.SlotId), CancellationToken.None,
                            call.SlotId ?? throw new InvalidOperationException("USB 通话缺少所属模组。"), call.CallId).ConfigureAwait(false);
                        keepAudio = started && string.Equals(Kernel.ActiveCall?.CallId, call.CallId, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        AddLog("ERROR", "CellularAudio", $"Unexpected cellular audio failure: {ex.Message}");
                    }
                    finally
                    {
                        if (!keepAudio)
                        {
                            ReleaseDesktopAudioClaim(call.CallId, call.SlotId);
                            await StopCellularModuleAudioAsync(call.SlotId, call.CallId).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    var calls = call.SlotId is null ? Kernel.Calls
                        : Kernel.Pool.Slots.TryGetValue(call.SlotId, out var owner) ? owner.Calls : null;
                    if (calls?.ExternalMediaBridgeEnabled == true) return;
                    if (!TryClaimDesktopAudio(call.CallId, call.SlotId, DesktopAudioKind.Microphone)) return;
                    var autoAnswerMessage = _autoAnswerMessages.Take(call.CallId, call.SlotId);
                    PostToUi(() =>
                    {
                        if (!string.Equals(Kernel.ActiveCall?.CallId, call.CallId, StringComparison.OrdinalIgnoreCase))
                        {
                            ReleaseDesktopAudioClaim(call.CallId, call.SlotId);
                            return;
                        }
                        _callAlerting.ReleaseHostAudio();
                        if (autoAnswerMessage != null) StartAutomaticAnswerMessage(autoAnswerMessage);
                        else StartMicrophone();
                    });
                }
            }
            finally
            {
                _desktopAudioTransition.Release();
            }
        }

        private async Task StopDesktopCellularAudioAsync(string? slotId, string callId)
        {
            // Cancel startup before waiting for the desktop transition gate.
            if (slotId != null && _cellularMedia.TryGetValue(slotId, out var media)) media.Cancel(callId);
            await _desktopAudioTransition.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCellularModuleAudioAsync(slotId, callId).ConfigureAwait(false);
            }
            finally
            {
                _desktopAudioTransition.Release();
            }
        }

        private async Task ResumePromotedCallAudioAsync(Task cleanup, CallConnectedEventArgs promoted)
        {
            try
            {
                await cleanup.ConfigureAwait(false);
                await EnsureDesktopMediaForCallAsync(promoted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddLog("WARN", "CellularAudio", $"Could not resume audio for the remaining call: {ex.Message}");
            }
        }

    }
}







