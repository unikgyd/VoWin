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
        private bool _hasOtp;

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

                _instance._hasOtp = sms.HasOtpCode;
                _instance._copyTargetText = (sms.HasOtpCode ? sms.ExtractedOtpCode : sms.Text) ?? string.Empty;

                _instance.TxtSender.Text = string.IsNullOrWhiteSpace(sms.SenderOrRecipient) ? "未知发件人" : sms.SenderOrRecipient;
                _instance.TxtSlotBadge.Text = string.IsNullOrWhiteSpace(slotInfo) ? "SIM" : slotInfo;
                _instance.TxtMessageBody.Text = sms.Text;
                _instance.TxtTime.Text = DateTime.Now.ToString("HH:mm");

                if (sms.HasOtpCode)
                {
                    _instance.IconBadge.Symbol = SymbolRegular.Key24;
                    _instance.IconBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
                    _instance.BorderOtpBadge.Visibility = Visibility.Visible;
                    _instance.TxtTypeBadge.Text = "验证码短信";
                    _instance.PanelOtpDisplay.Visibility = Visibility.Visible;
                    _instance.TxtOtpCode.Text = sms.ExtractedOtpCode;
                    _instance.TxtBtnText.Text = "复制验证码";
                    _instance.BtnCopyAction.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                }
                else
                {
                    _instance.IconBadge.Symbol = SymbolRegular.Chat24;
                    _instance.IconBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                    _instance.BorderOtpBadge.Visibility = Visibility.Collapsed;
                    _instance.PanelOtpDisplay.Visibility = Visibility.Collapsed;
                    _instance.TxtBtnText.Text = "复制短信内容";
                    _instance.BtnCopyAction.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
                }

                _instance._remainingMs = TotalMs;
                _instance.UpdateProgress();

                _instance.Show();
                _instance.Activate();
                _instance.Topmost = true;
            });
        }

        public static void Dismiss()
        {
            RunOnUi(() =>
            {
                if (_instance != null)
                {
                    _instance._timer.Stop();
                    _instance.Hide();
                    _instance.Close();
                    _instance = null;
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

            try
            {
                Clipboard.SetText(_copyTargetText);
                TxtBtnText.Text = "✓ 已复制!";
                BtnCopyAction.Background = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));

                // Briefly keep visible so user sees the success feedback, then dismiss
                await Task.Delay(750);
                Dismiss();
            }
            catch
            {
                try
                {
                    Clipboard.SetDataObject(_copyTargetText, true);
                    TxtBtnText.Text = "✓ 已复制!";
                    BtnCopyAction.Background = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
                    await Task.Delay(750);
                    Dismiss();
                }
                catch { }
            }
        }
    }
}
