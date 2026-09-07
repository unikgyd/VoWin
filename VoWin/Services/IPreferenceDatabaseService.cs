using VoWin.Models;

namespace VoWin.Services
{
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
        Task<IReadOnlyList<SmsMessageModel>> GetAllSmsMessagesAsync();
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
    }
}
