using VoWin.Models;
using VoWin.Services;

namespace VoWin.ViewModels.Pages;

public partial class SettingsViewModel
{
    private readonly ISmsApiService? _smsApi;
    private bool _smsApiSettingsLoaded;
    [ObservableProperty] private bool _smsApiEnabled;
    [ObservableProperty] private string _smsApiBindAddress = "127.0.0.1";
    [ObservableProperty] private int _smsApiPort = 8787;
    [ObservableProperty] private string _smsApiToken = "";
    [ObservableProperty] private bool _smsApiAllowLanAccess;
    [ObservableProperty] private bool _isSmsApiBusy;
    [ObservableProperty] private string _smsApiNotice = "";
    public bool CanEditSmsApi => _smsApi != null && !IsSmsApiBusy;
    public GatewayStatusItem SmsApiIndicator => _smsApi?.Indicator ?? new("短信 API", "未启用", "只读接口，默认关闭。");
    public string SmsApiEndpoint => new SmsApiSettings { BindAddress = SmsApiBindAddress, Port = SmsApiPort }.BaseUrl + "api/v1/sms";
    public string SmsApiHelp => """
        Authorization: Bearer <Token>
        GET /api/v1/sms?limit=20&keyword=登录&slotId=<线路ID>
        GET /api/v1/otp?keyword=登录&maxAgeSeconds=300

        可选参数：keyword（正文包含，不是正则）、sender（完整发件人）、slotId、since（含时区时间）、maxAgeSeconds（最多 7 天）。
        列表还可用 limit（1–100）、otpOnly=true。默认列表取最近 24 小时，验证码取最近 5 分钟，无匹配时 found=false。
        只查已保存的收件短信，不发送、不删除、不标记已读，不占用模组。验证码为规则提取，请核对来源与用途。
        Token 放请求头，不放 URL。中文、+号及其他查询值请做 URL 编码；不提供浏览器跨域访问。
        """;
    partial void OnIsSmsApiBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditSmsApi));
    partial void OnSmsApiBindAddressChanged(string value) => OnPropertyChanged(nameof(SmsApiEndpoint));
    partial void OnSmsApiPortChanged(int value) => OnPropertyChanged(nameof(SmsApiEndpoint));

    internal void LoadSmsApiSettings()
    {
        if (_smsApiSettingsLoaded || _smsApi == null) return;
        var settings = _smsApi.Settings;
        SmsApiEnabled = settings.Enabled; SmsApiBindAddress = settings.BindAddress; SmsApiPort = settings.Port;
        SmsApiToken = settings.Token; SmsApiAllowLanAccess = settings.AllowLanAccess;
        _smsApiSettingsLoaded = true;
        OnPropertyChanged(nameof(SmsApiIndicator));
    }
    [RelayCommand]
    private void GenerateSmsApiToken()
    {
        if (!CanEditSmsApi) return;
        SmsApiToken = SmsApiSettings.GenerateToken();
        SmsApiNotice = "已生成新 Token，保存后生效；旧 Token 将失效，请同步更新调用端。";
    }
    [RelayCommand]
    private async Task SaveSmsApiAsync()
    {
        if (!CanEditSmsApi || _smsApi == null) return;
        IsSmsApiBusy = true;
        try
        {
            await _smsApi.SaveAndApplyAsync(new() { Enabled = SmsApiEnabled, BindAddress = SmsApiBindAddress.Trim(), Port = SmsApiPort,
                Token = SmsApiToken, AllowLanAccess = SmsApiAllowLanAccess });
            SmsApiNotice = SmsApiEnabled ? "已保存并开始监听。持有 Token 可读取所有线路最近的收件短信，请勿公开。" : "已保存，短信 API 已关闭。";
        }
        catch (Exception ex) { SmsApiNotice = DiagnosticLogRedactor.Redact(ex.Message); }
        finally { IsSmsApiBusy = false; OnPropertyChanged(nameof(SmsApiIndicator)); }
    }
    private void OnSmsApiStatusChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) OnPropertyChanged(nameof(SmsApiIndicator));
        else dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SmsApiIndicator)));
    }
}
