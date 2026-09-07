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

    [ObservableProperty] private bool _notifyIncomingSms = true;
    [ObservableProperty] private bool _notifyOtpOnly;
    [ObservableProperty] private bool _notifyIncomingCalls = true;
    [ObservableProperty] private string _lastActivity = "尚无远程活动";
    [ObservableProperty] private string _pairingStatus = "未开启配对";
    [ObservableProperty] private string _operationStatus = string.Empty;
    [ObservableProperty] private bool _isBusy;

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
        OperationStatus = "让待授权的 QQ/微信用户发送：绑定 <配对码>。配对码仅可使用一次。";
    }

    private RemoteControlSettings BuildSettings() => new()
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
        NotifyIncomingSms = value.NotifyIncomingSms;
        NotifyOtpOnly = value.NotifyOtpOnly;
        NotifyIncomingCalls = value.NotifyIncomingCalls;
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
