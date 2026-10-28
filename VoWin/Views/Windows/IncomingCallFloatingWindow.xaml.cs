using System.Windows;
using System.Windows.Input;

namespace VoWin.Views.Windows
{
    public partial class IncomingCallFloatingWindow : Window
    {
        private static IncomingCallFloatingWindow? _instance;
        private Func<Task>? _onAnswer;
        private Func<Task>? _onReject;
        private long _presentationVersion;
        private long? _operationVersion;

        public IncomingCallFloatingWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            MouseLeftButtonDown += (s, e) => DragMove();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Position at top-right corner of work area
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 16;
            Top = workArea.Top + 20;
        }

        public static void ShowIncoming(string number, string slotInfo, Func<Task> onAnswer, Func<Task> onReject,
            bool simultaneousSip = false)
        {
            RunOnUi(() =>
            {
                if (_instance == null || !_instance.IsLoaded)
                {
                    _instance = new IncomingCallFloatingWindow();
                }

                _instance._onAnswer = onAnswer;
                _instance._onReject = onReject;
                _instance._presentationVersion++;
                _instance.BtnAnswer.IsEnabled = _instance.BtnReject.IsEnabled = true;
                _instance.TxtCallSubtext.Text = simultaneousSip ? "本机与分机同时响铃 · 谁先接听谁通话" : "等待接听 · 音频格式将在接通时协商";
                _instance.BtnReject.ToolTip = simultaneousSip ? "拒接整通来电，所有分机停止振铃" : "拒接来电";
                _instance.TxtCallSubtext.ToolTip = null;

                _instance.TxtCallerNumber.Text = string.IsNullOrWhiteSpace(number) ? "未知来电" : number;
                _instance.TxtSlotBadge.Text = string.IsNullOrWhiteSpace(slotInfo) ? "SIM 1" : slotInfo;

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

            dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Send);
        }

        private async void BtnAnswer_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await PerformActionAsync(answer: true);
        }

        private async void BtnReject_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await PerformActionAsync(answer: false);
        }

        private async Task PerformActionAsync(bool answer)
        {
            var version = _presentationVersion;
            if (_operationVersion == version) return;
            var callback = answer ? _onAnswer : _onReject;
            if (callback == null) return;
            _operationVersion = version;
            BtnAnswer.IsEnabled = BtnReject.IsEnabled = false;
            TxtCallSubtext.Text = answer ? "正在接听…" : "正在拒接…";
            TxtCallSubtext.ToolTip = null;
            try
            {
                await callback();
                if (_presentationVersion == version && ReferenceEquals(_instance, this)) Dismiss();
            }
            catch (Exception ex)
            {
                if (_presentationVersion == version)
                {
                    TxtCallSubtext.Text = answer ? "接听失败 · 可重试或返回主界面" : "拒接失败 · 可重试或返回主界面";
                    TxtCallSubtext.ToolTip = ex.Message;
                }
            }
            finally
            {
                if (_presentationVersion == version && _operationVersion == version)
                {
                    _operationVersion = null;
                    BtnAnswer.IsEnabled = BtnReject.IsEnabled = true;
                }
            }
        }
    }
}
