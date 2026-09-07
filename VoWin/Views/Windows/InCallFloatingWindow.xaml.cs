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
        private Action<bool>? _onMuteChanged;
        private bool _isMuted;
        private Point _mouseDownPos;
        private DateTime _mouseDownTime;

        public InCallFloatingWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
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
            Action<bool> onMuteChanged,
            bool isMuted = false)
        {
            RunOnUi(() =>
            {
                if (_instance == null || !_instance.IsLoaded)
                {
                    _instance = new InCallFloatingWindow();
                }

                _instance._onHangup = onHangup;
                _instance._onMuteChanged = onMuteChanged;
                _instance._isMuted = isMuted;

                _instance.TxtNumber.Text = string.IsNullOrWhiteSpace(number) ? "当前通话" : number;
                _instance.TxtDuration.Text = string.IsNullOrWhiteSpace(durationText) ? "00:00" : durationText;
                _instance.TxtSlot.Text = string.IsNullOrWhiteSpace(slotInfo) ? string.Empty : $"· {slotInfo}";

                _instance.UpdateMuteVisuals();

                _instance.Show();
                _instance.Topmost = true;
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
            _isMuted = !_isMuted;
            UpdateMuteVisuals();
            _onMuteChanged?.Invoke(_isMuted);
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
            Dismiss();
            if (_onHangup != null)
            {
                try
                {
                    await _onHangup();
                }
                catch { }
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
