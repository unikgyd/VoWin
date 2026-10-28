using VoWin.Models;
using VoSharp.Kernel.SipGateway;

namespace VoWin.Services
{
    public sealed record IncomingSmsPersistenceResult(bool IsNew, int QueuedRecipients, bool ForwardingCapacityExceeded, bool ForwardingUnsupported = false);

    public interface IPreferenceDatabaseService
    {
        Task InitializeAsync();

        Task<ModulePreferenceModel?> GetModulePreferenceAsync(string identifier, string? imei = null);
        Task SaveModulePreferenceAsync(ModulePreferenceModel model);
        Task<IReadOnlyList<ModulePreferenceModel>> GetAllModulePreferencesAsync();

        Task<SimPreferenceModel?> GetSimPreferenceAsync(string iccid);
        Task SaveSimPreferenceAsync(SimPreferenceModel model);
        Task<IReadOnlyList<SimPreferenceModel>> GetAllSimPreferencesAsync();

        // Call Records
        Task SaveCallRecordAsync(CallRecordModel record);
        Task<IReadOnlyList<CallRecordModel>> GetCallRecordsAsync(int limit = 200);
        Task DeleteCallRecordAsync(string id);
        Task ClearCallRecordsAsync();

        // SMS Messages
        Task SaveSmsMessageAsync(SmsMessageModel message);
        // A caller may notify only after a successful atomic insert. Never fall
        // back to a check-then-save implementation when an adapter lacks this API.
        Task<bool> SaveIncomingSmsIfNewAsync(SmsMessageModel message, CancellationToken ct = default) =>
            throw new NotSupportedException("Atomic incoming SMS persistence is unavailable.");
        Task<IncomingSmsPersistenceResult> SaveIncomingSmsWithForwardsAsync(SmsMessageModel message,
            IReadOnlyList<SipSmsForwardRecipient> recipients, CancellationToken ct = default) =>
            recipients.Count == 0 ? SaveWithoutForwardsAsync(this, message, ct) :
                throw new NotSupportedException("Atomic incoming SMS and forwarding persistence is unavailable.");
        private static async Task<IncomingSmsPersistenceResult> SaveWithoutForwardsAsync(IPreferenceDatabaseService service, SmsMessageModel message, CancellationToken ct) =>
            new(await service.SaveIncomingSmsIfNewAsync(message, ct).ConfigureAwait(false), 0, false);
        Task<IReadOnlyList<SmsMessageModel>> GetAllSmsMessagesAsync();
        Task<SmsInboxQueryResult> QueryIncomingSmsAsync(SmsInboxQuery query, CancellationToken ct = default);
        Task<IReadOnlyList<SmsMessageModel>> GetMessagesByNumberAsync(string remoteNumber);
        Task UpdateSmsDeliveryStatusAsync(int messageReference, string recipient, SmsDeliveryState state, string? status);
        Task UpdateSmsDeliveryStatusAsync(string messageId, SmsDeliveryState state, string? status);
        Task DeleteSmsMessageAsync(string id);
        Task DeleteConversationAsync(string remoteNumber);
        Task<bool> HasSmsMessageAsync(string senderOrRecipient, DateTime timestamp, string text, string? rawPdu = null);

        // Proxy Nodes & Country Routes
        Task SaveProxyNodeAsync(ProxyNodeModel node);
        Task<IReadOnlyList<ProxyNodeModel>> GetAllProxyNodesAsync();
        Task DeleteProxyNodeAsync(string id);
        Task SaveCountryRouteAsync(CountryRouteModel route);
        Task<IReadOnlyList<CountryRouteModel>> GetAllCountryRoutesAsync();
        Task DeleteCountryRouteAsync(string countryCode);
        Task SaveIccidRouteAsync(IccidRouteModel route);
        Task<IReadOnlyList<IccidRouteModel>> GetAllIccidRoutesAsync();
        Task DeleteIccidRouteAsync(string iccid);
    }
}
