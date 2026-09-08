using System.Collections.ObjectModel;
using VoSharp.Euicc.Models;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;

namespace VoWin.Services
{
    public interface IVoKernelService : IAsyncDisposable
    {
        IVoKernel Kernel { get; }

        ObservableCollection<ModemSlot> Slots { get; }
        ModemSlot? ActiveSlot { get; }
        ObservableCollection<SmsConversationModel> Conversations { get; }
        ObservableCollection<CallRecordModel> CallHistory { get; }
        ObservableCollection<ProxyNodeModel> ProxyPresets { get; }
        ObservableCollection<LogEntryModel> Logs { get; }
        bool IsInitialScanRunning { get; }
        string InitialScanStatus { get; }

        IPreferenceDatabaseService Preferences { get; }
        void OnUsbDeviceInserted();
        void OnUsbDeviceRemoved();
        Task ApplyPreferencesToSlotAsync(ModemSlot slot);
        Task SaveModulePreferencesAsync(string slotId, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? customName = null);
        Task SaveSimPreferencesAsync(string iccid, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? nickname);

        // Telemetry
        TelephonyState StateMachineState { get; }
        SignalQuality? CurrentSignal { get; }
        NetworkRegistration? CurrentRegistration { get; }
        SimIdentity? CurrentSim { get; }
        VoWifiDiagnosticInfo? VoWifiDiag { get; }
        CallSession? ActiveCall { get; }
        CallState CurrentCallState { get; }
        string? CurrentCallNumber { get; }
        bool HasIncomingCall { get; }
        string? IncomingCallerNumber { get; }
        event Action<string>? CallMediaStatusChanged;
        event Action<SmsMessageModel>? IncomingSmsReceived;
        event Action<string, string?>? IncomingCallReceived;
        Task<CallExperienceSettings> GetCallExperienceSettingsAsync();
        Task SaveCallExperienceSettingsAsync(CallExperienceSettings settings);

        // Operations
        Task InitializeAsync();
        Task<IReadOnlyList<ModemSlot>> DiscoverSlotsAsync();
        Task<ModemSlot?> AddSlotAsync(string portName, int baudRate = 115200, string? name = null, string? proxyUrl = null);
        Task<bool> RemoveSlotAsync(string slotId);
        bool SelectSlot(string slotId);
        Task RefreshMetricsAsync(string? slotId = null);
        Task<string> ExecuteAtCommandAsync(string command, string? slotId = null);
        Task<string> SendUssdAsync(string code, string? slotId = null);
        Task<bool> SetFlightModeAsync(bool enable, string? slotId = null);
        Task<bool> RebootModemAsync(string? slotId = null);
        Task<(bool Success, long RttMs, string Status)> ProbeVoWifiLivenessAsync(string? slotId = null);

        // Calls
        Task<CallInfo> DialAsync(string number, string? slotId = null, bool forceCellular = false);
        Task<CallInfo?> HangupAsync(string? slotId = null);
        Task<CallInfo?> AnswerAsync(string? slotId = null);
        Task<CallInfo?> RejectAsync(string? slotId = null);
        Task<bool> SendDtmfAsync(char digit, string? slotId = null);

        // SMS
        Task<SmsSubmitResult> SendSmsAsync(string recipient, string text, bool requestStatusReport = true, string? slotId = null, bool forceVowifi = false, bool forceCellular = false);
        void DeleteConversation(string contactNumber);
        void ClearConversation(string contactNumber);
        void DeleteMessage(string conversationNumber, string messageId);
        Task SyncModemSmsAsync(ModemSlot slot, CancellationToken cancellationToken = default);
        void ClearCallHistory();
        void DeleteCallRecord(string id);

        // VoWiFi
        Task<bool> StartVoWifiAsync(string? slotId = null);
        Task<bool> StopVoWifiAsync(string? slotId = null);

        // eSIM / eUICC
        Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(string? slotId = null);
        Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, string? slotId = null);
        Task<bool> DisableEuiccProfileAsync(string iccidOrAid, string? slotId = null);
        Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, string? slotId = null);
        Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, string? slotId = null);
        Task<string> GetEuiccEidAsync(string? slotId = null);
        Task<EuiccDownloadResult> DownloadEuiccProfileAsync(string activationCode, string? confirmationCode = null, IProgress<EuiccDownloadProgress>? progress = null, string? slotId = null, CancellationToken cancellationToken = default, bool allowUntrustedTls = false, bool allowRetryAfterUncertain = false);

        // Proxy & Country Dispatch Routing
        bool SetSlotProxy(string slotId, string? proxyUrl);
        Task<int> TestProxyConnectivityAsync(ProxyNodeModel node, string targetHost = "8.8.8.8", int targetPort = 53);
        void AddProxyPreset(ProxyNodeModel node);
        void RemoveProxyPreset(string nodeId);

        ObservableCollection<CountryRouteModel> CountryRoutes { get; }
        void SaveCountryRoute(string countryCode, string? proxyNodeId);
        void RemoveCountryRoute(string countryCode);
        string? ResolveEgressProxyForSlot(string slotId);
    }
}
