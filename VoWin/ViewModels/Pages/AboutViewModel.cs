using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Abstractions.Controls;
using ZXing;
using ZXing.QrCode;
using VoWin.Services;

namespace VoWin.ViewModels.Pages
{
    public partial class AboutViewModel : ObservableObject, INavigationAware
    {
        [ObservableProperty]
        private string _appName = "VoWin & VoSharp";

        [ObservableProperty]
        private string _appVersion = "v1.0.0";

        [ObservableProperty]
        private string _buildInfo = ".NET 10.0 x64 · Release";

        [ObservableProperty]
        private string _projectDescription = "VoWin 是一个基于 .NET 10 与 WPF-UI Fluent Design 体系构建的现代化 Windows 蜂窝通信与卡池路由管理终端。面向实体硬件 SIM 矩阵与 GSMA 规范虚拟 eSIM，提供高品质语音呼叫、短信收发与国家智能出站分流能力。";

        [ObservableProperty]
        private string _gitHubUrl = "https://github.com/unikgyd/VoWin";

        [ObservableProperty]
        private string _gitHubProfileUrl = "https://github.com/unikgyd";

        [ObservableProperty]
        private string _projectNotes = "【项目说明与使用提示】\r\n\r\n• 本软件完全开源免费，仅供个人学习、技术研究与合法合规的网络通信测试使用。\r\n• 请勿将本软件用于任何违反当地法律法规的场景与行为。\r\n• 没有过多必要重新制作一个类似的产品，不如把Token费用给我一部分，我来完善。";

        [ObservableProperty]
        private string _tronAddress = "TDecoS5mFozuyfJSR2QhSsWbCkGm8nsvpH";

        [ObservableProperty]
        private string _bscAddress = "0x0e5e07b7604d8585fd936527f48874a10ad8b884";

        [ObservableProperty]
        private string _polygonAddress = "0x0e5e07b7604d8585fd936527f48874a10ad8b884";

        [ObservableProperty]
        private ImageSource? _tronQrCode;

        [ObservableProperty]
        private ImageSource? _bscQrCode;

        [ObservableProperty]
        private ImageSource? _polygonQrCode;

        [ObservableProperty]
        private ImageSource? _qrCodeImage;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public AboutViewModel()
        {
            InitializeVersion();
            TronQrCode = GenerateQrCode(TronAddress);
            BscQrCode = GenerateQrCode(BscAddress);
            PolygonQrCode = GenerateQrCode(PolygonAddress);
            QrCodeImage = BscQrCode;
        }

        private void InitializeVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
            {
                AppVersion = $"v{version.Major}.{version.Minor}.{version.Build}";
            }
            BuildInfo = $".NET 10.0 ({Environment.Version}) · Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
        }

        [RelayCommand]
        public void OpenUrl(string? url)
        {
            var target = string.IsNullOrWhiteSpace(url) ? GitHubUrl : url;
            if (string.IsNullOrWhiteSpace(target))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
                StatusMessage = $"已在浏览器中打开: {target}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开链接失败: {ex.Message}";
            }
        }

        [RelayCommand]
        public void CopyUrl(string? url)
        {
            var target = string.IsNullOrWhiteSpace(url) ? GitHubUrl : url;
            if (string.IsNullOrWhiteSpace(target))
                return;

            try
            {
                Clipboard.SetText(target);
                StatusMessage = "✅ 仓库链接已成功复制到剪贴板！";
                AppToast.ShowCopySuccess("GitHub 仓库链接");
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制失败: {ex.Message}";
            }
        }

        [RelayCommand]
        public void CopyProjectNotes()
        {
            if (string.IsNullOrWhiteSpace(ProjectNotes))
                return;

            try
            {
                Clipboard.SetText(ProjectNotes);
                StatusMessage = "📋 项目说明文本已成功复制到剪贴板！";
                AppToast.ShowCopySuccess("项目说明与免责声明");
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制失败: {ex.Message}";
            }
        }

        partial void OnTronAddressChanged(string value)
        {
            TronQrCode = GenerateQrCode(value);
        }

        partial void OnBscAddressChanged(string value)
        {
            BscQrCode = GenerateQrCode(value);
            QrCodeImage = BscQrCode;
        }

        partial void OnPolygonAddressChanged(string value)
        {
            PolygonQrCode = GenerateQrCode(value);
        }

        private ImageSource? GenerateQrCode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                var writer = new BarcodeWriterPixelData
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new QrCodeEncodingOptions
                    {
                        Height = 300,
                        Width = 300,
                        Margin = 1,
                        CharacterSet = "UTF-8"
                    }
                };

                var pixelData = writer.Write(text);
                var bitmap = BitmapSource.Create(
                    pixelData.Width,
                    pixelData.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixelData.Pixels,
                    pixelData.Width * 4);

                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void CopyAddressHelper(string address, string networkName)
        {
            if (string.IsNullOrWhiteSpace(address))
                return;

            try
            {
                Clipboard.SetText(address);
                StatusMessage = $"🪙 {networkName} 赞助收款地址已成功复制到剪贴板！感谢支持！";
                AppToast.ShowCopySuccess(networkName);
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制地址失败: {ex.Message}";
            }
        }

        [RelayCommand]
        public void CopyTronAddress() => CopyAddressHelper(TronAddress, "USDT · TRON (TRC-20)");

        [RelayCommand]
        public void CopyBscAddress() => CopyAddressHelper(BscAddress, "USDT · BNB Smart Chain (BEP-20)");

        [RelayCommand]
        public void CopyPolygonAddress() => CopyAddressHelper(PolygonAddress, "USDT · Polygon");

        private void SaveQrCodeHelper(ImageSource? image, string defaultFileName)
        {
            if (image is not BitmapSource bitmap)
                return;

            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = defaultFileName,
                    DefaultExt = ".png",
                    Filter = "PNG 图像 (*.png)|*.png|所有文件 (*.*)|*.*"
                };

                if (dlg.ShowDialog() == true)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(dlg.FileName);
                    encoder.Save(stream);
                    StatusMessage = $"✅ 二维码图片已成功保存至: {dlg.FileName}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"保存二维码失败: {ex.Message}";
            }
        }

        [RelayCommand]
        public void SaveTronQrCode() => SaveQrCodeHelper(TronQrCode, "USDT_TRON_TRC20_QRCode.png");

        [RelayCommand]
        public void SaveBscQrCode() => SaveQrCodeHelper(BscQrCode, "USDT_BSC_BEP20_QRCode.png");

        [RelayCommand]
        public void SavePolygonQrCode() => SaveQrCodeHelper(PolygonQrCode, "USDT_Polygon_QRCode.png");

        [RelayCommand]
        public void SaveQrCode() => SaveBscQrCode();

        public Task OnNavigatedToAsync()
        {
            StatusMessage = string.Empty;
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync() => Task.CompletedTask;
    }
}
