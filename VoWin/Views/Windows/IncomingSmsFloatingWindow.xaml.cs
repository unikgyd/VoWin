using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VoWin.Models;
using Wpf.Ui.Controls;

namespace VoWin.Views.Windows
{
    public partial class IncomingSmsFloatingWindow : Window
    {
        private static IncomingSmsFloatingWindow? _instance;
        private readonly DispatcherTimer _timer;
        private double _remainingMs = 10000;
        private const double TotalMs = 10000;
        private bool _isMouseOver;
        private string _copyTargetText = string.Empty;
        private long _presentationVersion;

        public IncomingSmsFloatingWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };

            MouseEnter += (s, e) => _isMouseOver = true;
            MouseLeave += (s, e) => _isMouseOver = false;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                    Dismiss();
            };

            _timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _timer.Tick += Timer_Tick;
            Closed += (_, _) =>
            {
                _timer.Stop();
                if (ReferenceEquals(_instance, this)) _instance = null;
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 16;
            Top = workArea.Top + 24;

            _remainingMs = TotalMs;
            UpdateProgress();
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isMouseOver) return;

            _remainingMs -= 50;
            if (_remainingMs <= 0)
            {
                _timer.Stop();
                Dismiss();
                return;
            }

            UpdateProgress();
        }

        private void UpdateProgress()
        {
            double ratio = Math.Max(0, Math.Min(1.0, _remainingMs / TotalMs));
            ProgressBarTimeout.Width = 400 * ratio;
        }

        public static void ShowNotification(SmsMessageModel sms, string? slotInfo)
        {
            RunOnUi(() =>
            {
                if (_instance == null || !_instance.IsLoaded)
                {
                    _instance = new IncomingSmsFloatingWindow();
                }

                _instance.PresentMessage(sms, slotInfo);
                _instance.Show();
                _instance.Topmost = true;
            });
        }

        internal void PresentMessage(SmsMessageModel sms, string? slotInfo)
        {
            _presentationVersion++;
            var otp = sms.ExtractedOtpCode;
            var hasOtp = !string.IsNullOrEmpty(otp);
            _copyTargetText = hasOtp ? otp! : sms.Text;
            TxtSender.Text = string.IsNullOrWhiteSpace(sms.SenderOrRecipient) ? "未知发件人" : sms.SenderOrRecipient;
            TxtSlotBadge.Text = string.IsNullOrWhiteSpace(slotInfo) ? "SIM" : slotInfo;
            TxtMessageBody.Text = sms.Text;
            TxtTime.Text = DateTime.Now.ToString("HH:mm");
            IconBadge.Symbol = hasOtp ? SymbolRegular.Key24 : SymbolRegular.Chat24;
            IconBadge.Foreground = new SolidColorBrush(hasOtp ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x34, 0xD3, 0x99));
            BorderOtpBadge.Visibility = hasOtp ? Visibility.Visible : Visibility.Collapsed;
            TxtTypeBadge.Text = hasOtp ? "验证码短信" : "新短信";
            PanelOtpDisplay.Visibility = hasOtp ? Visibility.Visible : Visibility.Collapsed;
            TxtOtpCode.Text = otp ?? string.Empty;
            TxtBtnText.Text = hasOtp ? "复制验证码" : "复制短信内容";
            BtnCopyAction.Background = new SolidColorBrush(hasOtp ? Color.FromRgb(0x25, 0x63, 0xEB) : Color.FromRgb(0x33, 0x41, 0x55));
            _remainingMs = TotalMs;
            UpdateProgress();
        }

        public static void Dismiss()
        {
            RunOnUi(() =>
            {
                if (_instance != null)
                {
                    var window = _instance;
                    _instance = null;
                    window._timer.Stop();
                    window.Close();
                }
            });
        }

        private static void RunOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action, DispatcherPriority.Send);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Dismiss();
        }

        private async void BtnCopyAction_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_copyTargetText)) return;
            var presentationVersion = _presentationVersion;
            var copyText = _copyTargetText;

            try
            {
                Clipboard.SetText(copyText);
            }
            catch
            {
                try
                {
                    Clipboard.SetDataObject(copyText, true);
                }
                catch { TxtBtnText.Text = "复制失败，请重试"; return; }
            }

            TxtBtnText.Text = "✓ 已复制!";
            BtnCopyAction.Background = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
            await Task.Delay(750);
            // A new SMS may have reused this popup while copy feedback was visible.
            if (ReferenceEquals(_instance, this) && _presentationVersion == presentationVersion) Dismiss();
        }
    }
}
