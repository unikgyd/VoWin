using System.Media;
using System.Windows;
using System.Windows.Input;

namespace VoWin.Views.Windows
{
    public partial class IncomingCallFloatingWindow : Window
    {
        private static IncomingCallFloatingWindow? _instance;
        private Func<Task>? _onAnswer;
        private Func<Task>? _onReject;

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

        public static void ShowIncoming(string number, string slotInfo, Func<Task> onAnswer, Func<Task> onReject)
        {
            RunOnUi(() =>
            {
                if (_instance == null || !_instance.IsLoaded)
                {
                    _instance = new IncomingCallFloatingWindow();
                }

                _instance._onAnswer = onAnswer;
                _instance._onReject = onReject;

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
            var cb = _onAnswer;
            Dismiss();
            if (cb != null)
            {
                await cb();
            }
        }

        private async void BtnReject_Click(object sender, RoutedEventArgs e)
        {
            var cb = _onReject;
            Dismiss();
            if (cb != null)
            {
                await cb();
            }
        }
    }
}
