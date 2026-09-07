using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using VoSharp.Euicc.Models;
using VoSharp.Ike.Transport;
using VoSharp.Kernel;
using VoSharp.Kernel.Events;
using VoSharp.Kernel.Pool;
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
    public class VoKernelService : IVoKernelService, INotifyPropertyChanged
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
        public IVoKernel Kernel { get; }

        public ObservableCollection<ModemSlot> Slots { get; } = new();
        public ModemSlot? ActiveSlot => Kernel.GetActiveSlot();
        public ObservableCollection<SmsConversationModel> Conversations { get; } = new();
        public ObservableCollection<CallRecordModel> CallHistory { get; } = new();
        public ObservableCollection<ProxyNodeModel> ProxyPresets { get; } = new();
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
        public event Action<string>? CallMediaStatusChanged;
        public event Action<SmsMessageModel>? IncomingSmsReceived;
        public event Action<string, string?>? IncomingCallReceived;

        public IPreferenceDatabaseService Preferences { get; }
        private DateTime? _callStartTime;
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource? _usbDebounceCts;
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
        private int _logDrainScheduled;
        private int _slotSyncScheduled;
        private int _slotSyncRequestVersion;

        // Microphone capture
        private NAudio.Wave.WaveIn? _waveIn;
        private readonly CellularAudioBridge _cellularAudioBridge = new();
        private readonly Qdc507VoiceRuntime _qdc507VoiceRuntime = new();
        private readonly CallAlerting _callAlerting = new();
        private readonly CallExperienceSettingsStore _callExperienceStore = new();
        private CancellationTokenSource? _cellularCallAudioCts;
        private CancellationTokenSource? _incomingAutoAnswerCts;
        private CallExperienceSettings _callExperience = new();
        private string? _pendingAutoAnswerMessagePath;
        private CallDirection _currentCallDirection = CallDirection.Outgoing;
        private bool _callRecordSavedForCurrentSession = true;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _syncingSlots = new();

        public VoKernelService(IPreferenceDatabaseService? preferences = null)
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            Preferences = preferences ?? new PreferenceDatabaseService();
            Kernel = new VoKernel();
            _logWriterTask = RunLogWriterAsync(_logWriterCts.Token);

            InitDefaultPresets();
            RegisterKernelEvents();
        }

        private void InitDefaultPresets()
        {
            ProxyPresets.Add(new ProxyNodeModel
            {
                Name = "本地 Clash / v2ray (SOCKS5)",
                Protocol = "socks5",
                Host = "127.0.0.1",
                Port = 7890
            });
            ProxyPresets.Add(new ProxyNodeModel
            {
                Name = "通用本地 SOCKS5 (1080)",
                Protocol = "socks5",
                Host = "127.0.0.1",
                Port = 1080
            });
            ProxyPresets.Add(new ProxyNodeModel
            {
                Name = "本地 HTTP 代理 (7890)",
                Protocol = "http",
                Host = "127.0.0.1",
                Port = 7890
            });

            // Initialize default country-aware egress rules (MCC Dispatch)
            CountryRoutes.Add(new CountryRouteModel
            {
                CountryCode = "CN",
                CountryName = "中国",
                FlagEmoji = "🇨🇳",
                MccList = "460",
                ProxyNodeId = null,
                ProxyNodeName = "直连模式 (Direct)"
            });
            CountryRoutes.Add(new CountryRouteModel
            {
                CountryCode = "US",
                CountryName = "美国",
                FlagEmoji = "🇺🇸",
                MccList = "310, 311, 312",
                ProxyNodeId = null,
                ProxyNodeName = "直连 (未分配节点)"
            });
            CountryRoutes.Add(new CountryRouteModel
            {
                CountryCode = "GB",
                CountryName = "英国",
                FlagEmoji = "🇬🇧",
                MccList = "234, 235",
                ProxyNodeId = null,
                ProxyNodeName = "直连 (未分配节点)"
            });
            CountryRoutes.Add(new CountryRouteModel
            {
                CountryCode = "HK",
                CountryName = "中国香港",
                FlagEmoji = "🇭🇰",
                MccList = "454",
                ProxyNodeId = null,
                ProxyNodeName = "直连 (未分配节点)"
            });
            CountryRoutes.Add(new CountryRouteModel
            {
                CountryCode = "JP",
                CountryName = "日本",
                FlagEmoji = "🇯🇵",
                MccList = "440, 441",
                ProxyNodeId = null,
                ProxyNodeName = "直连 (未分配节点)"
            });
            CountryRoutes.Add(new CountryRouteModel
            {
                CountryCode = "SG",
                CountryName = "新加坡",
                FlagEmoji = "🇸🇬",
                MccList = "525",
                ProxyNodeId = null,
                ProxyNodeName = "直连 (未分配节点)"
            });
        }

        public async Task InitializeAsync()
        {
            IsInitialScanRunning = true;
            InitialScanStatus = "正在加载本地设置…";
            AddLog("INFO", "VoKernelService", "Initializing VoSharp core engine & SQLite database...");

            try
            {
                await Preferences.InitializeAsync();
                _callExperience = await _callExperienceStore.LoadAsync();
                ApplyVolteAudioPrewarmPreference();

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

                // 3. Load ProxyPresets & CountryRoutes from SQLite
                var savedNodes = await Preferences.GetAllProxyNodesAsync();
                if (savedNodes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        ProxyPresets.Clear();
                        foreach (var n in savedNodes) ProxyPresets.Add(n);
                    });
                }
                else
                {
                    foreach (var n in ProxyPresets) await Preferences.SaveProxyNodeAsync(n);
                }

                var savedRoutes = await Preferences.GetAllCountryRoutesAsync();
                if (savedRoutes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        CountryRoutes.Clear();
                        foreach (var r in savedRoutes) CountryRoutes.Add(r);
                    });
                }
                else
                {
                    foreach (var r in CountryRoutes) await Preferences.SaveCountryRouteAsync(r);
                }

                AddLog("INFO", "Preferences", $"SQLite initialized ({callHistory.Count} calls, {allSms.Count} messages loaded)");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Preferences", $"SQLite initialization error: {ex.Message}");
            }

            await RunOnUiAsync(SyncSlots);

            AddLog("INFO", "VoKernelService", "Starting initial Modem scan...");
            InitialScanStatus = "正在并发扫描串口设备…";
            try
            {
                var ports = SerialPort.GetPortNames();
                AddLog("INFO", "VoKernelService", $"Ports: [{(ports.Length > 0 ? string.Join(", ", ports) : "None")}]");

                var found = await DiscoverSlotsCoreAsync();
                await RunOnUiAsync(SyncSlots);
                AddLog("INFO", "VoKernelService", $"Scan finished, found {found.Count} slots");

                InitialScanStatus = "正在恢复模块设置…";
                foreach (var slot in found)
                {
                    await ApplyPreferencesToSlotAsync(slot);
                }

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
            // Call Events
            Kernel.CallStateChanged += (s, e) =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    CurrentCallState = e.NewState;
                    CurrentCallNumber = e.TargetNumber;

                    if (e.NewState == CallState.Active)
                    {
                        _callStartTime = DateTime.Now;
                        HasIncomingCall = false;
                        Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    }
                    else if (e.NewState == CallState.Ended || e.NewState == CallState.Idle)
                    {
                        HasIncomingCall = false;
                        Views.Windows.IncomingCallFloatingWindow.Dismiss();
                        if (!_callRecordSavedForCurrentSession && _callStartTime.HasValue)
                        {
                            var dur = DateTime.Now - _callStartTime.Value;
                            RecordCallFinished(e.TargetNumber, _currentCallDirection, e.NewState, _callStartTime.Value, dur);
                        }
                        _callStartTime = null;
                    }

                    AddLog("INFO", "Calls", $"Call [{e.TargetNumber}] state changed -> {e.NewState}");
                }));
            };

            Kernel.IncomingCall += (s, e) =>
            {
                _currentCallDirection = CallDirection.Incoming;
                _callRecordSavedForCurrentSession = false;
                try { IncomingCallReceived?.Invoke(e.CallerNumber, e.SlotId); } catch { }
                PostToUi(() =>
                {
                    HasIncomingCall = true;
                    IncomingCallerNumber = e.CallerNumber;
                    CurrentCallNumber = e.CallerNumber;
                    CurrentCallState = CallState.Incoming;

                    AddLog("INFO", "Calls", $"Incoming call from {e.CallerNumber} on slot {e.SlotId ?? "Main"}");

                    var incomingSlotId = e.SlotId;
                    var incomingSlotName = !string.IsNullOrEmpty(incomingSlotId)
                        ? Slots.FirstOrDefault(slot => slot.Id.Equals(incomingSlotId, StringComparison.OrdinalIgnoreCase))?.Name
                        : null;

                    // Show modern top-right floating call window!
                    Views.Windows.IncomingCallFloatingWindow.ShowIncoming(
                        e.CallerNumber,
                        incomingSlotName ?? ActiveSlot?.Name ?? "SIM",
                        onAnswer: async () =>
                        {
                            await AnswerAsync(incomingSlotId);
                            TrayManager.RestoreMainWindow();
                        },
                        onReject: async () =>
                        {
                            await RejectAsync(incomingSlotId);
                        }
                    );
                });
                StartIncomingAlertingAndAutoAnswer(e.SlotId);
            };

            Kernel.CallConnected += async (s, e) =>
            {
                PostToUi(() =>
                {
                    StopIncomingAlerting();
                    CurrentCallState = CallState.Active;
                    _callStartTime = DateTime.Now;
                    HasIncomingCall = false;
                    Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    AddLog("INFO", "Calls", $"Call connected: {e.TargetNumber} (Codec: {e.Codec ?? "Default"})");
                });

                if (e.Codec?.StartsWith("Cellular", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _callAlerting.ReleaseHostAudio();
                    var audioCts = new CancellationTokenSource();
                    var previous = Interlocked.Exchange(ref _cellularCallAudioCts, audioCts);
                    previous?.Cancel();
                    previous?.Dispose();
                    try
                    {
                        await StartCellularCallAudioAsync(
                            e.Codec.Contains("Cellular/UAC", StringComparison.OrdinalIgnoreCase),
                            Interlocked.Exchange(ref _pendingAutoAnswerMessagePath, null), audioCts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        AddLog("ERROR", "CellularAudio", $"Unexpected cellular audio failure: {ex.Message}");
                    }
                }
                else
                {
                    PostToUi(() =>
                    {
                        _callAlerting.ReleaseHostAudio();
                        _ = StopCellularAudioBridgeAsync();
                        StartMicrophone();
                    });
                }
            };

            Kernel.CallEnded += (s, e) =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    StopIncomingAlerting();
                    CurrentCallState = CallState.Ended;
                    HasIncomingCall = false;
                    Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    Views.Windows.InCallFloatingWindow.Dismiss();
                    var audioCts = Interlocked.Exchange(ref _cellularCallAudioCts, null);
                    audioCts?.Cancel();
                    audioCts?.Dispose();
                    StopMicrophone();
                    _ = StopCellularAudioBridgeAsync();
                    _ = StopQdc507VoiceRouteAsync();
                    ApplyVolteAudioPrewarmPreference();

                    var dur = e.Duration ?? (_callStartTime.HasValue ? DateTime.Now - _callStartTime.Value : TimeSpan.Zero);
                    RecordCallFinished(e.TargetNumber, _currentCallDirection, CallState.Ended, _callStartTime ?? DateTime.Now, dur, null, e.SlotId, e.WavRecordingPath);
                    _callStartTime = null;
                    AddLog("INFO", "Calls",
                        $"Call ended: {e.TargetNumber}, duration: {dur:mm\\:ss}, reason: {e.Reason ?? "unknown"}");
                }));
            };

            // SMS Events
            Kernel.SmsReceived += (s, e) =>
            {
                _ = AddIncomingSmsAsync(e.Message, e.SlotId);
                AddLog("INFO", "SMS", $"Received SMS from {e.Message.SenderOrRecipient}: {e.Message.Text}");
            };

            Kernel.SmsSent += (s, e) =>
            {
                AddLog("INFO", "SMS", $"SMS sent to {e.Result.Recipient}, status: {e.Result.SubmissionStatus}");
            };

            Kernel.SmsStatusReportReceived += (s, e) =>
            {
                UpdateSmsDeliveryReport(e.Report);
                AddLog("INFO", "SMS", $"SMS Delivery Report for ref #{e.Report.MessageReference} -> {e.Report.DeliveryStatus}");
            };

            // Slot & Pool Events
            Kernel.SlotListChanged += (s, e) =>
            {
                RequestSlotSync();
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
            };

            Kernel.LogEmitted += (s, e) =>
            {
                AddLog(e.Level, e.Source, e.Message);
            };

            Kernel.SystemErrorOccurred += (s, e) =>
            {
                AddLog("ERROR", e.Source, e.ErrorMessage);
            };
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
        }

        private void AddLog(string level, string source, string message)
        {
            var now = DateTime.Now;
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

        private static string GetLogFilePath()
        {
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

        private void RecordCallFinished(string? number, CallDirection? dir, CallState state, DateTime startTime, TimeSpan duration, string? codec = null, string? slotId = null, string? wavPath = null)
        {
            if (_callRecordSavedForCurrentSession) return;
            _callRecordSavedForCurrentSession = true;

            var target = !string.IsNullOrWhiteSpace(number) ? number : (!string.IsNullOrWhiteSpace(CurrentCallNumber) ? CurrentCallNumber : "未知号码");
            var direction = dir ?? _currentCallDirection;

            AddCallRecord(target, direction, state, startTime, duration, codec, slotId, wavPath);
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

        private async Task AddIncomingSmsAsync(SmsMessage msg, string? slotId)
        {
            var remote = !string.IsNullOrWhiteSpace(msg.SenderOrRecipient) ? msg.SenderOrRecipient : "Unknown";
            var exists = await Preferences.HasSmsMessageAsync(remote, msg.Timestamp, msg.Text, msg.RawPdu).ConfigureAwait(false);
            if (exists)
            {
                return;
            }

            var item = new SmsMessageModel
            {
                Index = msg.Index,
                SlotId = slotId,
                SenderOrRecipient = remote,
                Text = msg.Text,
                Timestamp = msg.Timestamp,
                IsOutgoing = false,
                DeliveryState = SmsDeliveryState.Received,
                DeliveryStatus = msg.DeliveryStatus,
                MessageReference = msg.MessageReference,
                RawPdu = msg.RawPdu
            };

            await Preferences.SaveSmsMessageAsync(item).ConfigureAwait(false);
            try { IncomingSmsReceived?.Invoke(item); } catch { }

            await RunOnUiAsync(() =>
            {
                var conv = GetOrCreateConversation(remote);
                if (!conv.Messages.Any(m => m.Timestamp == item.Timestamp && m.Text == item.Text))
                {
                    conv.Messages.Add(item);
                    conv.UnreadCount++;
                    conv.UpdateLastMessage();
                    MoveConversationToTop(conv);
                }

                // If incoming SMS contains verification code (OTP): play alert chime and show floating notification window
                if (item.HasOtpCode)
                {
                    var slotName = !string.IsNullOrEmpty(slotId)
                        ? Slots.FirstOrDefault(s => s.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))?.Name
                        : ActiveSlot?.Name ?? "SIM 1";

                    SoundEffectService.Instance.PlaySmsChime();
                    Views.Windows.IncomingSmsFloatingWindow.ShowNotification(item, slotName);
                }
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
            return slots;
        }

        private async Task<IReadOnlyList<ModemSlot>> DiscoverSlotsCoreAsync(CancellationToken cancellationToken = default)
        {
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
            await Kernel.RefreshSignalAsync(slotId);
            await Kernel.RefreshRegistrationAsync(slotId);
            await Kernel.RefreshSimAsync(slotId);
        }

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

                if (slot != null)
                {
                    return await slot.SetFlightModeAsync(enable);
                }
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
            AddLog("INFO", "Calls", $"Dialing {number} (ForceCellular={forceCellular})...");
            _currentCallDirection = CallDirection.Outgoing;
            _callRecordSavedForCurrentSession = false;
            CurrentCallNumber = number;
            CurrentCallState = CallState.Dialing;
            _callStartTime = DateTime.Now;

            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                else
                    slot = Kernel.Pool.ActiveSlot;

                if (slot?.Modem != null && slot.Modem.IsOpen)
                {
                    return await Kernel.DialAsync(number, slotId, forceCellular);
                }
                else
                {
                    throw new InvalidOperationException("No active modem or modem is not open.");
                }
            }
            catch (Exception ex)
            {
                CurrentCallState = CallState.Ended;
                RecordCallFinished(number, CallDirection.Outgoing, CallState.Ended, _callStartTime ?? DateTime.Now, TimeSpan.Zero);
                AddLog("ERROR", "Calls", $"Dial failed: {ex.Message}");
                throw;
            }
        }

        public async Task<CallInfo?> HangupAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Hanging up current call...");
            Views.Windows.IncomingCallFloatingWindow.Dismiss();

            CallInfo? res = null;
            try
            {
                res = await Kernel.HangupAsync(slotId);
            }
            catch { }

            var dur = _callStartTime.HasValue ? DateTime.Now - _callStartTime.Value : TimeSpan.FromSeconds(15);
            if (!string.IsNullOrEmpty(CurrentCallNumber))
            {
                RecordCallFinished(CurrentCallNumber, _currentCallDirection, CallState.Ended, _callStartTime ?? DateTime.Now, dur, "AMR-WB");
            }

            CurrentCallState = CallState.Ended;
            HasIncomingCall = false;
            _callStartTime = null;

            return res;
        }

        public async Task<CallInfo?> AnswerAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Answering incoming call...");
            Views.Windows.IncomingCallFloatingWindow.Dismiss();
            HasIncomingCall = false;

            try
            {
                return await Kernel.AnswerAsync(slotId);
            }
            catch (Exception ex)
            {
                CurrentCallState = CallState.Ended;
                AddLog("ERROR", "Calls", $"Answer failed: {ex.Message}");
                return null;
            }
        }

        public async Task<CallInfo?> RejectAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Rejecting incoming call...");
            Views.Windows.IncomingCallFloatingWindow.Dismiss();
            HasIncomingCall = false;
            CurrentCallState = CallState.Ended;

            if (!string.IsNullOrEmpty(IncomingCallerNumber))
            {
                RecordCallFinished(IncomingCallerNumber, CallDirection.Missed, CallState.Ended, DateTime.Now, TimeSpan.Zero, "AMR-WB");
            }

            try
            {
                return await Kernel.RejectAsync(slotId);
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> SendDtmfAsync(char digit, string? slotId = null)
        {
            AddLog("INFO", "Calls", $"Sending DTMF digit: '{digit}'");
            return await Kernel.SendDtmfAsync(digit, slotId);
        }

        public async Task<SmsSubmitResult> SendSmsAsync(string recipient, string text, bool requestStatusReport = true, string? slotId = null, bool forceVowifi = false, bool forceCellular = false)
        {
            AddLog("INFO", "SMS", $"Sending SMS to {recipient}: \"{text}\"");

            SmsConversationModel conv = null!;
            SmsMessageModel model = null!;
            await RunOnUiAsync(() =>
            {
                conv = GetOrCreateConversation(recipient);
                model = new SmsMessageModel
                {
                    SenderOrRecipient = recipient,
                    Text = text,
                    Timestamp = DateTime.Now,
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
                var result = await Kernel.SendSmsAsync(recipient, text, requestStatusReport, slotId, forceVowifi, forceCellular);
                await RunOnUiAsync(() =>
                {
                    model.DeliveryState = result.AllPartsAccepted ? SmsDeliveryState.Sent : SmsDeliveryState.Failed;
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
                AddLog("ERROR", "SMS", $"Send SMS failed: {ex.Message}");
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

        public async Task<bool> StartVoWifiAsync(string? slotId = null)
        {
            var targetSlot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (targetSlot != null)
            {
                await ApplyPreferencesToSlotAsync(targetSlot).ConfigureAwait(false);

                // An explicit per-SIM/per-slot route wins over MCC routing.
                // The earlier code changed only the WPF model and never updated
                // ModemSlot in the kernel, so the actual IKE session could use a
                // stale proxy or direct UDP.
                var effectiveProxy = targetSlot.ProxyUrl ?? ResolveEgressProxyForSlot(targetSlot.Id);
                if (!Kernel.SetSlotProxy(targetSlot.Id, effectiveProxy))
                {
                    AddLog("ERROR", "Proxy", $"Unable to apply the proxy route to slot {targetSlot.Id}.");
                    return false;
                }

                targetSlot.ProxyUrl = effectiveProxy;
                if (!string.IsNullOrEmpty(effectiveProxy))
                {
                    AddLog("INFO", "VoWiFi", $"VoWiFi 代理路由已应用至核心: {DescribeProxyEndpoint(effectiveProxy)} -> 卡槽 [{targetSlot.Name}]");
                }
                else AddLog("INFO", "VoWiFi", $"VoWiFi 使用直连 UDP -> 卡槽 [{targetSlot.Name}]");
            }

            AddLog("INFO", "VoWiFi", $"Starting VoWiFi on slot {slotId ?? "Default"}...");
            return await Kernel.StartVoWifiAsync(slotId);
        }

        public async Task<bool> StopVoWifiAsync(string? slotId = null)
        {
            AddLog("INFO", "VoWiFi", $"Stopping VoWiFi on slot {slotId ?? "Default"}...");
            return await Kernel.StopVoWifiAsync(slotId);
        }

        public async Task<(bool Success, long RttMs, string Status)> ProbeVoWifiLivenessAsync(string? slotId = null)
        {
            var result = await Kernel.ProbeVoWifiLivenessAsync(slotId);
            AddLog(result.Success ? "INFO" : "WARN", "VoWiFi", $"SIP 探针保活检测: {(result.Success ? "成功" : "失败")}, RTT: {result.RttMs}ms, 状态: {result.Status}");
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

        public async Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, string? slotId = null)
        {
            AddLog("INFO", "eSIM", $"Switching to eSIM profile: {iccidOrAid} on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.SwitchEuiccProfileAsync(iccidOrAid);
            }
            return await Kernel.SwitchEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> DisableEuiccProfileAsync(string iccidOrAid, string? slotId = null)
        {
            AddLog("INFO", "eSIM", $"Disabling eSIM profile: {iccidOrAid} on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.DisableEuiccProfileAsync(iccidOrAid);
            }
            return await Kernel.DisableEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, string? slotId = null)
        {
            AddLog("INFO", "eSIM", $"Deleting eSIM profile: {iccidOrAid}");
            return await Kernel.DeleteEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, string? slotId = null)
        {
            AddLog("INFO", "eSIM", $"Renaming profile {iccidOrAid} -> \"{nickname}\" on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.RenameEuiccProfileAsync(iccidOrAid, nickname);
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
            CancellationToken cancellationToken = default)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            AddLog("INFO", "eSIM", $"Starting verified eSIM profile download on slot {slot?.Name ?? "Active"}.");
            if (slot != null)
                return await slot.DownloadEuiccProfileAsync(activationCode, confirmationCode, progress, cancellationToken);
            return await Kernel.DownloadEuiccProfileAsync(activationCode, confirmationCode, progress, cancellationToken);
        }

        public bool SetSlotProxy(string slotId, string? proxyUrl)
        {
            AddLog("INFO", "Proxy", $"Setting proxy for slot {slotId}: {proxyUrl ?? "Direct"}");
            return Kernel.SetSlotProxy(slotId, proxyUrl);
        }

        public async Task<int> TestProxyConnectivityAsync(ProxyNodeModel node, string targetHost = "8.8.8.8", int targetPort = 53)
        {
            var sw = Stopwatch.StartNew();
            node.Status = "正在验证 SOCKS5 UDP...";

            try
            {
                using var proxy = Socks5Client.TryParse(node.ToProxyUrl())
                    ?? throw new InvalidOperationException("VoWiFi 需要 socks5:// 或 socks5h:// 代理；HTTP 代理不支持 IKEv2/ESP UDP。");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var relay = await proxy.UdpAssociateAsync(cts.Token);
                sw.Stop();
                int ms = (int)sw.ElapsedMilliseconds;
                node.LatencyMs = ms;
                node.Status = $"SOCKS5 UDP 就绪 ({ms}ms)";
                AddLog("INFO", "Proxy", $"SOCKS5 UDP ASSOCIATE ready for {node.Name} ({node.Host}:{node.Port}); relay {relay} in {ms}ms");
                return ms;
            }
            catch (Exception ex)
            {
                sw.Stop();
                node.LatencyMs = -1;
                node.Status = $"SOCKS5 UDP 不可用: {ex.Message}";
                AddLog("WARN", "Proxy", $"SOCKS5 UDP ASSOCIATE failed for {node.Name} ({node.Host}:{node.Port}): {ex.Message}");
                return -1;
            }
        }

        public void AddProxyPreset(ProxyNodeModel node)
        {
            ProxyPresets.Add(node);
            _ = Preferences.SaveProxyNodeAsync(node);
            AddLog("INFO", "Proxy", $"Added proxy preset: {node.Name} ({node.ToProxyUrl()})");
        }

        public void RemoveProxyPreset(string nodeId)
        {
            var item = ProxyPresets.FirstOrDefault(p => p.Id == nodeId);
            if (item != null)
            {
                ProxyPresets.Remove(item);
                _ = Preferences.DeleteProxyNodeAsync(nodeId);
                AddLog("INFO", "Proxy", $"Removed proxy preset: {item.Name}");
            }
        }

        public string? ResolveEgressProxyForSlot(string slotId)
        {
            var slot = Slots.FirstOrDefault(s => s.Id == slotId);
            if (slot == null) return null;

            // Resolve country from SIM MCC and match country route in proxy pool
            var mcc = slot.Sim?.Mcc;
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
                            return node.ToProxyUrl();
                        }
                    }
                }
            }

            return null;
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
        }

        public void RemoveCountryRoute(string countryCode)
        {
            var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
            if (rule != null)
            {
                CountryRoutes.Remove(rule);
                _ = Preferences.DeleteCountryRouteAsync(countryCode);
                AddLog("INFO", "Proxy Routing", $"Removed country rule: {countryCode}");
            }
        }

        public void OnUsbDeviceInserted()
        {
            AddLog("INFO", "USB Monitor", "检测到 USB 设备插入事件，等待串口驱动完成初始化...");

            _usbDebounceCts?.Cancel();
            _usbDebounceCts = new CancellationTokenSource();
            _ = HandleUsbDeviceInsertedAsync(_usbDebounceCts.Token);
        }

        public void OnUsbDeviceRemoved()
        {
            AddLog("INFO", "USB Monitor", "检测到 USB 设备拔出事件，正在保留 VoWiFi 会话并释放硬件句柄...");
            _usbDebounceCts?.Cancel();
            _usbDebounceCts = new CancellationTokenSource();
            _ = HandleUsbDeviceRemovedAsync(_usbDebounceCts.Token);
        }

        private async Task HandleUsbDeviceRemovedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                var availablePorts = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var slot in Kernel.GetSlots().Where(slot => !availablePorts.Contains(slot.PortName)))
                {
                    await slot.DetachHardwareAsync(token).ConfigureAwait(false);
                    AddLog("INFO", "USB Monitor",
                        $"模块 [{slot.Name}] 已拔出；现有 VoWiFi 隧道将继续保活，重新插入后会自动重新注册。");
                }
                await RunOnUiAsync(SyncSlots, DispatcherPriority.DataBind).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AddLog("ERROR", "USB Monitor", $"USB 拔出处理失败: {ex.Message}");
            }
        }

        private async Task HandleUsbDeviceInsertedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(800, token).ConfigureAwait(false);
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

        public async Task ApplyPreferencesToSlotAsync(ModemSlot slot)
        {
            try
            {
                var modPref = await Preferences.GetModulePreferenceAsync(slot.Id, slot.Imei);
                SimPreferenceModel? simPref = null;
                if (slot.Sim != null && !string.IsNullOrEmpty(slot.Sim.Iccid))
                {
                    simPref = await Preferences.GetSimPreferenceAsync(slot.Sim.Iccid);
                }

                // A SIM follows the card between modules, so it must override
                // the module preference.  Both are applied to the kernel before
                // any VoWiFi session can begin.
                var preferredProxy = !string.IsNullOrWhiteSpace(simPref?.DedicatedProxyUrl)
                    ? simPref.DedicatedProxyUrl
                    : modPref?.DefaultProxyUrl;
                if (!string.IsNullOrWhiteSpace(preferredProxy))
                {
                    Kernel.SetSlotProxy(slot.Id, preferredProxy);
                    AddLog("INFO", "Proxy", $"已恢复卡槽 [{slot.Name}] 的代理设置: {DescribeProxyEndpoint(preferredProxy)}");
                }

                ApplyVoWifiHomeIdentity(slot, simPref);

                var autoVoWifi = simPref?.DefaultVoWifi ?? modPref?.DefaultVoWifi ?? false;
                if (autoVoWifi)
                    QueueAutoVoWifiStart(slot);

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

        private void ApplyVoWifiHomeIdentity(ModemSlot slot, SimPreferenceModel? simPref)
        {
            slot.VoWifiIdentityOverride = null;
            var reported = slot.Sim;
            var savedImsi = simPref?.Imsi?.Trim();
            if (reported == null || string.IsNullOrWhiteSpace(savedImsi) ||
                savedImsi.Equals(reported.Imsi, StringComparison.Ordinal))
            {
                return;
            }

            // Do not treat arbitrary saved data as an authentication identity.
            // It must be a valid permanent IMSI for the carrier profile that
            // also matches the inserted SIM's ICCID.
            if (savedImsi.Length is < 5 or > 16 || !savedImsi.All(char.IsAsciiDigit))
                return;
            var homeProfile = CarrierProfileDatabase.FindProfile(savedImsi, reported.Iccid, null);
            var homePlmn = savedImsi[..5];
            if (homeProfile == null || !homeProfile.HomePlmns.Contains(homePlmn, StringComparer.Ordinal))
                return;

            slot.VoWifiIdentityOverride = SimIdentity.FromImsiAndIccid(savedImsi, reported.Iccid, homeProfile.Name);
            AddLog("INFO", "VoWiFi", $"检测到临时 IMSI，已为卡槽 [{slot.Name}] 恢复已验证的归属网络 EAP-AKA 身份。");
        }

        private void QueueAutoVoWifiStart(ModemSlot slot)
        {
            if (slot.VoWifi.State is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka or
                VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering or VoWifiState.ImsRegistered)
                return;
            if (!_autoVoWifiStarts.TryAdd(slot.Id, 0)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    AddLog("INFO", "VoWiFi", $"卡槽 [{slot.Name}] 已启用自动 VoWiFi，正在注册…");
                    var started = await StartVoWifiAsync(slot.Id).ConfigureAwait(false);
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

                    bool exists = await Preferences.HasSmsMessageAsync(remote, msg.Timestamp, msg.Text, msg.RawPdu).ConfigureAwait(false);
                    if (!exists)
                    {
                        var model = new SmsMessageModel
                        {
                            Index = msg.Index,
                            SlotId = slot.Id,
                            SenderOrRecipient = remote,
                            Text = msg.Text,
                            Timestamp = msg.Timestamp,
                            IsOutgoing = isOutgoing,
                            DeliveryState = isOutgoing ? SmsDeliveryState.Sent : SmsDeliveryState.Received,
                            DeliveryStatus = msg.DeliveryStatus,
                            MessageReference = msg.MessageReference,
                            RawPdu = msg.RawPdu
                        };

                        await Preferences.SaveSmsMessageAsync(model).ConfigureAwait(false);

                        await RunOnUiAsync(() =>
                        {
                            var conv = GetOrCreateConversation(remote);
                            if (!conv.Messages.Any(m => m.Timestamp == model.Timestamp && m.Text == model.Text))
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

        public async Task SaveModulePreferencesAsync(string slotId, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl)
        {
            var slot = Kernel.GetSlots().FirstOrDefault(s => s.Id == slotId);
            var model = new ModulePreferenceModel
            {
                Id = slotId,
                Imei = slot?.Imei,
                DefaultFlightMode = flightMode,
                DefaultVoWifi = vowifi,
                DefaultCellularData = cellularData,
                DefaultDataRoaming = roaming,
                DefaultProxyUrl = proxyUrl,
                BaudRate = slot?.BaudRate ?? 115200,
                LastSeenAt = DateTime.Now
            };
            await Preferences.SaveModulePreferenceAsync(model);
            AddLog("INFO", "Preferences", $"Saved module preference for [{slotId}]");
        }

        public async Task SaveSimPreferencesAsync(string iccid, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? nickname)
        {
            var model = new SimPreferenceModel
            {
                Iccid = iccid,
                CardNickname = nickname,
                DefaultFlightMode = flightMode,
                DefaultVoWifi = vowifi,
                DefaultCellularData = cellularData,
                DefaultDataRoaming = roaming,
                DedicatedProxyUrl = proxyUrl,
                LastSeenAt = DateTime.Now
            };
            await Preferences.SaveSimPreferenceAsync(model);
            AddLog("INFO", "Preferences", $"Saved sim preference for [{iccid}]");
        }

        private int _serviceDisposed;
        public async ValueTask DisposeAsync()
        {
            StopMicrophone();
            await StopCellularAudioBridgeAsync();
            var audioCts = Interlocked.Exchange(ref _cellularCallAudioCts, null);
            audioCts?.Cancel();
            audioCts?.Dispose();
            await StopQdc507VoiceRouteAsync();
            await _cellularAudioBridge.DisposeAsync();
            if (Interlocked.Exchange(ref _serviceDisposed, 1) != 0)
            {
                return;
            }

            try
            {
                _usbDebounceCts?.Cancel();
            }
            catch { }
            try
            {
                _usbDebounceCts?.Dispose();
            }
            catch { }

            _logLines.Writer.TryComplete();
            try
            {
                await _logWriterTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                _logWriterCts.Cancel();
            }
            _logWriterCts.Dispose();

            try
            {
                await Kernel.DisposeAsync();
            }
            catch { }
        }
        private VoSharp.Telephony.Calls.RtpSession? GetActiveRtpSession()
        {
            if (Kernel is VoKernel k)
            {
                if (k.VoWifi?.ActiveRtpSession != null) return k.VoWifi.ActiveRtpSession;
                foreach (var slot in k.Pool.Slots.Values)
                {
                    if (slot.VoWifi?.ActiveRtpSession != null)
                        return slot.VoWifi.ActiveRtpSession;
                }
            }
            return null;
        }

        private void StartMicrophone()
        {
            try
            {
                if (_waveIn != null) StopMicrophone();
                _waveIn = new NAudio.Wave.WaveIn
                {
                    WaveFormat = new NAudio.Wave.WaveFormat(8000, 16, 1),
                    BufferMilliseconds = 20
                };
                _waveIn.DataAvailable += (s, a) =>
                {
                    var rtp = GetActiveRtpSession();
                    if (rtp != null && a.BytesRecorded > 0)
                    {
                        int sampleCount = a.BytesRecorded / 2;
                        short[] samples = new short[sampleCount];
                        Buffer.BlockCopy(a.Buffer, 0, samples, 0, a.BytesRecorded);
                        rtp.SendAudioPcm(samples);
                    }
                };
                _waveIn.StartRecording();
                AddLog("INFO", "Audio", "Microphone capture started for active call.");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Audio", $"Failed to start microphone: {ex.Message}");
            }
        }

        private void StopMicrophone()
        {
            if (_waveIn != null)
            {
                try
                {
                    _waveIn.StopRecording();
                    _waveIn.Dispose();
                }
                catch { }
                _waveIn = null;
                AddLog("INFO", "Audio", "Microphone capture stopped.");
            }
        }

        public Task<CallExperienceSettings> GetCallExperienceSettingsAsync() => Task.FromResult(new CallExperienceSettings
        {
            VolteAudioPrewarmEnabled = _callExperience.VolteAudioPrewarmEnabled,
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

        private void StartIncomingAlertingAndAutoAnswer(string? slotId)
        {
            if (Volatile.Read(ref _incomingAutoAnswerCts) != null) return;
            ApplyVolteAudioPrewarmPreference();
            _callAlerting.StartRinging();
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _incomingAutoAnswerCts, cts, null) != null) { cts.Dispose(); return; }
            if (!_callExperience.AutoAnswerEnabled) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_callExperience.AutoAnswerDelaySeconds), cts.Token);
                    if (cts.IsCancellationRequested || !HasIncomingCall || CurrentCallState != CallState.Incoming) return;
                    var message = _callExperience.AutoAnswerMessagePath;
                    _pendingAutoAnswerMessagePath = !string.IsNullOrWhiteSpace(message) && File.Exists(message) ? message : null;
                    AddLog("INFO", "Calls", "Incoming call timed out; answering automatically.");
                    await AnswerAsync(slotId);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AddLog("WARN", "Calls", $"Auto-answer failed: {ex.Message}"); }
            });
        }

        private void StopIncomingAlerting()
        {
            var cts = Interlocked.Exchange(ref _incomingAutoAnswerCts, null);
            cts?.Cancel();
            cts?.Dispose();
            _callAlerting.StopRinging();
        }

        private async Task<bool> StartCellularAudioBridgeAsync(string? messagePath, CancellationToken cancellationToken)
        {
            try
            {
                await _cellularAudioBridge.StartAsync(messagePath, cancellationToken);
                AddLog("INFO", "CellularAudio", "PC microphone/speaker connected to modem USB Audio Class endpoints.");
                return true;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "CellularAudio", $"Failed to start cellular audio bridge: {ex.Message}");
                return false;
            }
        }

        private async Task StopCellularAudioBridgeAsync()
        {
            try
            {
                var wasRunning = _cellularAudioBridge.IsRunning;
                await _cellularAudioBridge.StopAsync();
                if (wasRunning)
                    AddLog("INFO", "CellularAudio", "Cellular USB audio bridge stopped.");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "CellularAudio", $"Failed to stop cellular audio bridge: {ex.Message}");
            }
        }

        private async Task PrepareQdc507VoiceRuntimeAsync()
        {
            try
            {
                await _qdc507VoiceRuntime.PrepareAsync();
                AddLog("INFO", "CellularAudio", "QDC507 module voice drivers and VoLTE calibration are ready.");
            }
            catch (Exception ex)
            {
                AddLog("INFO", "CellularAudio", $"QDC507 runtime is not available: {ex.Message}");
            }
        }

        private async Task StartCellularCallAudioAsync(bool standardUacReady, string? messagePath, CancellationToken cancellationToken)
        {
            PostToUi(StopMicrophone);
            if (!standardUacReady)
            {
                try
                {
                    AddLog("INFO", "CellularAudio", "Starting QDC507 D4/UAC VoLTE route...");
                    await _qdc507VoiceRuntime.StartRouteAsync(cancellationToken);
                    AddLog("INFO", "CellularAudio", "QDC507 D4/UAC VoLTE route is RUNNING.");
                    // QDC507 briefly republishes its UAC stream after audio_enable=1.
                    // Opening the old endpoint immediately succeeds but remains a zero stream.
                    // On QDC507 the D4 endpoint accepts an early open but then
                    // remains a zero stream for the whole call. The module needs
                    // roughly six seconds after audio_enable before MME opens it.
                    await Task.Delay(TimeSpan.FromSeconds(7), cancellationToken);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AddLog("ERROR", "CellularAudio",
                        $"The modem rejected AT+QPCMV and the QDC507 runtime route could not start: {ex.Message}");
                    CallMediaStatusChanged?.Invoke("Cellular/Audio unavailable");
                    return;
                }
            }

            // audio_enable=1 can briefly re-enumerate the Windows UAC endpoints.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = await StartCellularAudioBridgeAsync(messagePath, cancellationToken);
                if (started)
                {
                    AddLog("INFO", "CellularAudio", $"Audio devices: {_cellularAudioBridge.DeviceSummary}");
                    CallMediaStatusChanged?.Invoke(standardUacReady
                        ? "Cellular/UAC 8 kHz"
                        : "Cellular/QDC507 UAC 8 kHz");
                    _ = MonitorCellularAudioLevelsAsync(cancellationToken);
                    return;
                }
                await Task.Delay(250, cancellationToken);
            }
            AddLog("ERROR", "CellularAudio", "QDC507 voice route is active, but Windows UAC endpoints did not become ready.");
            CallMediaStatusChanged?.Invoke("Cellular/Windows audio unavailable");
        }

        private async Task MonitorCellularAudioLevelsAsync(CancellationToken cancellationToken)
        {
            var reopenedZeroStream = false;
            try
            {
                for (var sample = 0; sample < 4; sample++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    var levels = _cellularAudioBridge.ReadAndResetPeaks();
                    AddLog("INFO", "CellularAudio",
                        $"Live PCM peaks: PC microphone -> modem {levels.HostMicrophonePeak}, modem -> PC speaker {levels.ModemDownlinkPeak}.");

                    if (!reopenedZeroStream && sample == 0 && levels.ModemDownlinkPeak <= 1)
                    {
                        reopenedZeroStream = true;
                        AddLog("WARN", "CellularAudio", "Modem UAC is still a zero stream; reopening endpoints after enumeration settled.");
                        await StartCellularAudioBridgeAsync(null, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task StopQdc507VoiceRouteAsync()
        {
            try { await _qdc507VoiceRuntime.StopRouteAsync(); }
            catch (Exception ex) { AddLog("WARN", "CellularAudio", $"Failed to stop QDC507 voice route: {ex.Message}"); }
        }
    }
}







