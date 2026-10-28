using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using VoWin.Services;
using Wpf.Ui.Controls;

namespace VoWin.Views.Windows
{
    public partial class InCallFloatingWindow : Window
    {
        private static InCallFloatingWindow? _instance;
        private Func<Task>? _onHangup;
        private Func<bool, bool>? _onMuteChanged;
        private bool _isMuted;
        private long _presentationVersion;
        private bool _hangupInProgress;
        private Point _mouseDownPos;
        private DateTime _mouseDownTime;

        public InCallFloatingWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closed += (_, _) => { if (ReferenceEquals(_instance, this)) _instance = null; };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Position at top-right of work area
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Top + 24;
        }

        public static void ShowActiveCall(
            string number,
            string durationText,
            string? slotInfo,
            Func<Task> onHangup,
            Func<bool, bool> onMuteChanged,
            bool isMuted = false,
            bool canMute = true)
        {
            RunOnUi(() =>
            {
                if (_instance == null)
                {
                    _instance = new InCallFloatingWindow();
                }

                _instance._onHangup = onHangup;
                _instance._presentationVersion++;
                _instance._onMuteChanged = onMuteChanged;
                _instance._isMuted = isMuted;
                _instance.BtnMute.IsEnabled = canMute;

                _instance.TxtNumber.Text = string.IsNullOrWhiteSpace(number) ? "当前通话" : number;
                _instance.TxtDuration.Text = string.IsNullOrWhiteSpace(durationText) ? "00:00" : durationText;
                _instance.TxtSlot.Text = string.IsNullOrWhiteSpace(slotInfo) ? string.Empty : $"· {slotInfo}";

                _instance.UpdateMuteVisuals();

                _instance.Show();
                _instance.Topmost = true;
            });
        }

        public static void UpdatePresentation(string number, string duration, bool isMuted, bool canMute)
        {
            RunOnUi(() =>
            {
                if (_instance == null) return;
                _instance.TxtNumber.Text = number;
                // Do not hide an actionable hangup error on the next timer tick.
                if (_instance.TxtDuration.ToolTip == null) _instance.TxtDuration.Text = duration;
                _instance._isMuted = isMuted;
                _instance.BtnMute.IsEnabled = canMute && !_instance._hangupInProgress;
                _instance.UpdateMuteVisuals();
            });
        }

        public static void UpdateDurationText(string durationText)
        {
            RunOnUi(() =>
            {
                if (_instance != null && _instance.IsLoaded)
                {
                    _instance.TxtDuration.Text = durationText;
                }
            });
        }

        public static void UpdateNumberText(string number)
        {
            RunOnUi(() =>
            {
                if (_instance != null && _instance.IsLoaded)
                {
                    _instance.TxtNumber.Text = number;
                }
            });
        }

        public static bool IsShowing => _instance != null && _instance.IsVisible;

        public static void Dismiss()
        {
            RunOnUi(() =>
            {
                if (_instance != null)
                {
                    _instance.Hide();
                    _instance.Close();
                    _instance = null;
                }
            });
        }

        private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDownPos = e.GetPosition(this);
            _mouseDownTime = DateTime.Now;

            try
            {
                DragMove();
            }
            catch { }

            // If mouse moved very little and released quickly, treat as click to restore
            var upPos = Mouse.GetPosition(this);
            var distance = Math.Sqrt(Math.Pow(upPos.X - _mouseDownPos.X, 2) + Math.Pow(upPos.Y - _mouseDownPos.Y, 2));
            if (distance < 5 && (DateTime.Now - _mouseDownTime).TotalMilliseconds < 350)
            {
                RestoreMainWindow();
            }
        }

        private void RestoreMainWindow()
        {
            Dismiss();
            TrayManager.RestoreMainWindow(navigateToPhone: true);
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!BtnMute.IsEnabled) return;
            try
            {
                if (_onMuteChanged?.Invoke(!_isMuted) != true) return;
                _isMuted = !_isMuted;
                UpdateMuteVisuals();
            }
            catch (Exception ex) { BtnMute.ToolTip = $"静音失败：{ex.Message}"; }
        }

        private void UpdateMuteVisuals()
        {
            if (_isMuted)
            {
                IconMute.Symbol = Wpf.Ui.Controls.SymbolRegular.MicOff24;
                BtnMute.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, 0xEF, 0x44, 0x44)); // translucent red
                BtnMute.ToolTip = "解除静音";
            }
            else
            {
                IconMute.Symbol = Wpf.Ui.Controls.SymbolRegular.Mic24;
                BtnMute.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)); // translucent white
                BtnMute.ToolTip = "静音麦克风";
            }
        }

        private async void BtnHangup_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_onHangup != null && !_hangupInProgress)
            {
                _hangupInProgress = true;
                var version = _presentationVersion;
                var callback = _onHangup;
                BtnHangup.IsEnabled = false;
                try
                {
                    await callback();
                    if (_presentationVersion == version && ReferenceEquals(_instance, this)) Dismiss();
                }
                catch (Exception ex)
                {
                    if (_presentationVersion == version)
                    {
                        TxtDuration.Text = "挂断失败";
                        TxtDuration.ToolTip = ex.Message;
                        BtnHangup.ToolTip = "挂断失败，点击重试；或返回主界面查看详情";
                    }
                }
                finally { _hangupInProgress = false; BtnHangup.IsEnabled = true; }
            }
        }

        private static void RunOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Normal);
        }
    }
}
