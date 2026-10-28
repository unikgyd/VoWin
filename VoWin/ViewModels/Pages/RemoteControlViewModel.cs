using System.Windows.Media;
using System.Windows.Media.Imaging;
using VoWin.Models;
using VoWin.Services;
using Wpf.Ui.Abstractions.Controls;
using ZXing;
using ZXing.Common;

namespace VoWin.ViewModels.Pages;

public partial class RemoteControlViewModel : ObservableObject, INavigationAware
{
    private readonly IRemoteControlService _remote;
    private bool _loaded;

    [ObservableProperty] private bool _weixinEnabled;
    [ObservableProperty] private string _weixinBaseUrl = "https://ilinkai.weixin.qq.com";
    [ObservableProperty] private string _weixinAllowedUsers = string.Empty;
    [ObservableProperty] private string _weixinAccount = "未登录";
    [ObservableProperty] private string _weixinStatus = "未配置";
    [ObservableProperty] private ImageSource? _weixinQrImage;
    [ObservableProperty] private string _weixinVerificationCode = string.Empty;
    [ObservableProperty] private bool _weixinNeedsVerificationCode;

    [ObservableProperty] private bool _qqEnabled;
    [ObservableProperty] private string _qqAppId = string.Empty;
    [ObservableProperty] private string _qqClientSecret = string.Empty;
    [ObservableProperty] private string _qqAllowedUsers = string.Empty;
    [ObservableProperty] private string _qqStatus = "未配置";
    [ObservableProperty] private bool _telegramEnabled;
    [ObservableProperty] private string _telegramApiBaseUrl = "https://api.telegram.org";
    [ObservableProperty] private string _telegramBotToken = string.Empty;
    [ObservableProperty] private string _telegramAllowedUsers = string.Empty;
    [ObservableProperty] private string _telegramStatus = "未启用";
    [ObservableProperty] private bool _barkEnabled;
    [ObservableProperty] private string _barkServerUrl = "https://api.day.app";
    [ObservableProperty] private string _barkDeviceKey = string.Empty;
    [ObservableProperty] private bool _barkIncludeSmsContent;
    [ObservableProperty] private string _barkStatus = "未启用";
    [ObservableProperty] private bool _allowRemoteCalls;
    [ObservableProperty] private bool _allowRemoteSms;
    [ObservableProperty] private bool _allowRemoteLineChanges;
    [ObservableProperty] private bool _allowRemoteReadSms;
    public GatewayStatusItem TelegramIndicator => _remote.TelegramIndicator;
    public GatewayStatusItem BarkIndicator => _remote.BarkIndicator;
    public GatewayStatusItem WeixinIndicator => _remote.WeixinIndicator;
    public GatewayStatusItem QqIndicator => _remote.QqIndicator;
    public string CommandHelp => VoWin.Services.RemoteControl.RemoteCommandProcessor.HelpText;
    private RemoteControlSettings? _lastLoadedSettings;

    [ObservableProperty] private bool _notifyIncomingSms = true;
    [ObservableProperty] private bool _notifyOtpOnly;
    [ObservableProperty] private bool _notifyIncomingCalls = true;
    [ObservableProperty] private string _lastActivity = "尚无远程活动";
    [ObservableProperty] private string _pairingStatus = "未开启配对";
    [ObservableProperty] private string _operationStatus = string.Empty;
    [ObservableProperty] private bool _isBusy;
    public bool CanEdit => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEdit));

    public RemoteControlViewModel(IRemoteControlService remote)
    {
        _remote = remote;
        _remote.StatusChanged += OnRemoteStatusChanged;
    }

    public Task OnNavigatedToAsync()
    {
        if (!_loaded)
        {
            LoadSettings(_remote.Settings);
            _loaded = true;
        }
        RefreshStatus();
        return Task.CompletedTask;
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _remote.SaveAndRestartAsync(BuildSettings());
            OperationStatus = "设置已安全保存，后台连接已重新加载。";
            RefreshStatus();
        }
        catch (Exception ex) { OperationStatus = $"保存失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestQqAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _remote.TestQqAsync(BuildSettings());
            OperationStatus = "QQ AppID/AppSecret 验证成功。";
        }
        catch (Exception ex) { OperationStatus = $"QQ 验证失败：{ex.Message}"; }
        finally { IsBusy = false; RefreshStatus(); }
    }

    [RelayCommand]
    private async Task TestTelegramAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _remote.TestTelegramAsync(BuildSettings()); OperationStatus = "Telegram 凭据验证通过；保存后按开关连接。"; }
        catch (Exception ex) { OperationStatus = $"Telegram 验证失败：{ex.Message}"; }
        finally { IsBusy = false; RefreshStatus(); }
    }
    [RelayCommand]
    private async Task TestBarkAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _remote.TestBarkAsync(BuildSettings()); OperationStatus = "测试推送已提交，请在手机确认收到。"; }
        catch (Exception ex) { OperationStatus = $"Bark 推送失败：{ex.Message}"; }
        finally { IsBusy = false; RefreshStatus(); }
    }

    [RelayCommand]
    private async Task LoginWeixinAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        WeixinNeedsVerificationCode = false;
        try
        {
            var start = await _remote.BeginWeixinLoginAsync();
            if (!start.Success || string.IsNullOrWhiteSpace(start.QrContent))
            {
                OperationStatus = start.Message;
                return;
            }
            WeixinQrImage = CreateQrImage(start.QrContent);
            OperationStatus = "请用微信扫描二维码；扫码后本页会自动完成连接。";
            await ContinueWeixinLoginCoreAsync(null);
        }
        catch (Exception ex) { OperationStatus = $"微信登录失败：{ex.Message}"; }
        finally { IsBusy = false; RefreshStatus(); }
    }

    [RelayCommand]
    private async Task ContinueWeixinLoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await ContinueWeixinLoginCoreAsync(WeixinVerificationCode); }
        catch (Exception ex) { OperationStatus = $"微信登录失败：{ex.Message}"; }
        finally { IsBusy = false; RefreshStatus(); }
    }

    private async Task ContinueWeixinLoginCoreAsync(string? code)
    {
        var result = await _remote.PollWeixinLoginAsync(code);
        WeixinNeedsVerificationCode = result.NeedsVerificationCode;
        OperationStatus = result.Message;
        if (result.Connected)
        {
            LoadSettings(_remote.Settings);
            WeixinQrImage = null;
            WeixinVerificationCode = string.Empty;
        }
    }

    [RelayCommand]
    private void CreatePairingCode()
    {
        _remote.CreatePairingCode();
        RefreshStatus();
        OperationStatus = "先启用对应通道，再让用户私聊发送：绑定 <配对码>。一次有效，最多尝试 10 次。Bark 无需配对。";
    }

    internal RemoteControlSettings BuildSettings() => new()
    {
        WeixinEnabled = WeixinEnabled,
        WeixinBaseUrl = WeixinBaseUrl,
        WeixinToken = _remote.Settings.WeixinToken,
        WeixinBotId = _remote.Settings.WeixinBotId,
        WeixinUserId = _remote.Settings.WeixinUserId,
        WeixinAllowedUsers = WeixinAllowedUsers,
        QqEnabled = QqEnabled,
        QqAppId = QqAppId,
        QqClientSecret = QqClientSecret,
        QqAllowedUsers = QqAllowedUsers,
        TelegramEnabled = TelegramEnabled,
        TelegramApiBaseUrl = TelegramApiBaseUrl,
        TelegramBotToken = TelegramBotToken,
        TelegramAllowedUsers = TelegramAllowedUsers,
        BarkEnabled = BarkEnabled,
        BarkServerUrl = BarkServerUrl,
        BarkDeviceKey = BarkDeviceKey,
        BarkIncludeSmsContent = BarkIncludeSmsContent,
        AllowRemoteCalls = AllowRemoteCalls,
        AllowRemoteSms = AllowRemoteSms,
        AllowRemoteLineChanges = AllowRemoteLineChanges,
        AllowRemoteReadSms = AllowRemoteReadSms,
        NotifyIncomingSms = NotifyIncomingSms,
        NotifyOtpOnly = NotifyOtpOnly,
        NotifyIncomingCalls = NotifyIncomingCalls
    };

    private void LoadSettings(RemoteControlSettings value)
    {
        WeixinEnabled = value.WeixinEnabled;
        WeixinBaseUrl = value.WeixinBaseUrl;
        WeixinAllowedUsers = value.WeixinAllowedUsers;
        WeixinAccount = string.IsNullOrWhiteSpace(value.WeixinBotId) ? "未登录" : $"机器人 {ShortId(value.WeixinBotId)}";
        QqEnabled = value.QqEnabled;
        QqAppId = value.QqAppId;
        QqClientSecret = value.QqClientSecret;
        QqAllowedUsers = value.QqAllowedUsers;
        TelegramEnabled = value.TelegramEnabled;
        TelegramApiBaseUrl = value.TelegramApiBaseUrl;
        TelegramBotToken = value.TelegramBotToken;
        TelegramAllowedUsers = value.TelegramAllowedUsers;
        BarkEnabled = value.BarkEnabled;
        BarkServerUrl = value.BarkServerUrl;
        BarkDeviceKey = value.BarkDeviceKey;
        BarkIncludeSmsContent = value.BarkIncludeSmsContent;
        AllowRemoteCalls = value.AllowRemoteCalls;
        AllowRemoteSms = value.AllowRemoteSms;
        AllowRemoteLineChanges = value.AllowRemoteLineChanges;
        AllowRemoteReadSms = value.AllowRemoteReadSms;
        NotifyIncomingSms = value.NotifyIncomingSms;
        NotifyOtpOnly = value.NotifyOtpOnly;
        NotifyIncomingCalls = value.NotifyIncomingCalls;
        _lastLoadedSettings = value.Clone();
    }

    private void OnRemoteStatusChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) RefreshStatus();
        else dispatcher.BeginInvoke(RefreshStatus);
    }

    private void RefreshStatus()
    {
        WeixinStatus = _remote.WeixinStatus;
        QqStatus = _remote.QqStatus;
        TelegramStatus = _remote.TelegramStatus;
        BarkStatus = _remote.BarkStatus;
        OnPropertyChanged(nameof(TelegramIndicator)); OnPropertyChanged(nameof(BarkIndicator));
        OnPropertyChanged(nameof(WeixinIndicator)); OnPropertyChanged(nameof(QqIndicator));
        var current = _remote.Settings;
        if (_lastLoadedSettings is { } previous)
        {
            // A pairing updates the saved allowlist, but never overwrites a user's unsaved form edits.
            if (WeixinAllowedUsers == previous.WeixinAllowedUsers) WeixinAllowedUsers = current.WeixinAllowedUsers;
            if (QqAllowedUsers == previous.QqAllowedUsers) QqAllowedUsers = current.QqAllowedUsers;
            if (TelegramAllowedUsers == previous.TelegramAllowedUsers) TelegramAllowedUsers = current.TelegramAllowedUsers;
            _lastLoadedSettings = current.Clone();
        }
        LastActivity = _remote.LastActivity;
        PairingStatus = _remote.PairingCodeStatus;
    }

    private static BitmapSource CreateQrImage(string content)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = 260, Height = 260, Margin = 1, PureBarcode = true }
        };
        var pixels = writer.Write(content);
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null,
            pixels.Pixels, pixels.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static string ShortId(string id) => id.Length <= 12 ? id : id[..6] + "…" + id[^4..];
}
