using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using VoSharp.Telephony.Calls;
using VoWin.Services;
using VoWin.ViewModels.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace VoWin.Views.Windows
{
    public partial class MainWindow : INavigationWindow
    {
        public MainWindowViewModel ViewModel { get; }
        public IVoKernelService KernelService { get; }
        private readonly DispatcherTimer _floatingStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _gatewayStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private CallSession? _floatingCall;
        private string? _floatingSlotId;
        private bool _shuttingDown;

        internal void ShowShutdownProgress(string message)
        {
            _shuttingDown = true;
            _floatingStatusTimer.Stop(); _gatewayStatusTimer.Stop();
            InCallFloatingWindow.Dismiss();
            RootNavigation.IsEnabled = false;
            ShutdownStatusText.Text = message;
            if (ShutdownOverlay.Visibility != Visibility.Visible)
            {
                ShutdownOverlay.Visibility = Visibility.Visible;
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            }
        }

        public MainWindow(
            MainWindowViewModel viewModel,
            IVoKernelService kernelService,
            INavigationViewPageProvider navigationViewPageProvider,
            INavigationService navigationService,
            ISnackbarService snackbarService
        )
        {
            ViewModel = viewModel;
            KernelService = kernelService;
            DataContext = this;

            InitializeComponent();
            SetPageService(navigationViewPageProvider);

            navigationService.SetNavigationControl(RootNavigation);
            snackbarService.SetSnackbarPresenter(SnackbarPresenter);

            HookInCallFloatingWindow();
            _gatewayStatusTimer.Tick += (_, _) => RefreshGatewayFooter();
            Loaded += (_, _) => { if (!_shuttingDown) { RefreshGatewayFooter(); _gatewayStatusTimer.Start(); } };
        }

        private void RefreshGatewayFooter()
        {
            var snapshot = KernelService.GetGatewayStatus(KernelService.ActiveSlot?.Id);
            var count = KernelService.Slots.Count(slot => slot.State is VoSharp.Kernel.Pool.SlotState.Online or VoSharp.Kernel.Pool.SlotState.Busy);
            GatewayFooterText.Text = $"{count}/{KernelService.Slots.Count} 台在线 · {snapshot.Scope}";
            GatewayFooterText.ToolTip = snapshot.Detail;
            FooterCellular.Item = snapshot.Cellular;
            FooterWifi.Item = snapshot.Wifi;
            FooterSip.Item = snapshot.Sip;
            TrayManager.UpdateStatus(snapshot.TraySummary);
        }

        #region INavigationWindow methods

        public INavigationView GetNavigation() => RootNavigation;

        public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

        public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) => RootNavigation.SetPageProviderService(navigationViewPageProvider);

        public void ShowWindow() => Show();

        public void CloseWindow() => Close();

        #endregion INavigationWindow methods

        private void HookInCallFloatingWindow()
        {
            IsVisibleChanged += (s, e) => CheckInCallFloatingWindow();
            StateChanged += (s, e) => CheckInCallFloatingWindow();
            Loaded += (s, e) => CheckInCallFloatingWindow();
            RootNavigation.Navigated += (s, e) => Dispatcher.BeginInvoke(CheckInCallFloatingWindow);
            _floatingStatusTimer.Tick += (s, e) => CheckInCallFloatingWindow();
            if (KernelService is System.ComponentModel.INotifyPropertyChanged state)
                state.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName is nameof(IVoKernelService.CurrentCallState) or nameof(IVoKernelService.CurrentCallNumber))
                        Dispatcher.BeginInvoke(CheckInCallFloatingWindow);
                };

            KernelService.Kernel.CallStateChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(CheckInCallFloatingWindow);
            };

            KernelService.Kernel.CallEnded += (s, e) =>
            {
                Dispatcher.BeginInvoke(CheckInCallFloatingWindow);
            };
            KernelService.Kernel.CallConnected += (s, e) => Dispatcher.BeginInvoke(CheckInCallFloatingWindow);
        }

        private void CheckInCallFloatingWindow()
        {
            if (_shuttingDown) return;
            var presentedCall = KernelService.Kernel.ActiveCall;
            var ongoing = CallFloatingWindowPolicy.HasOngoingCall(KernelService.CurrentCallState, presentedCall != null);
            if (ongoing) _floatingStatusTimer.Start(); else _floatingStatusTimer.Stop();
            var phoneVisible = (RootNavigation.SelectedItem as INavigationViewItem)?.TargetPageType == typeof(Pages.PhonePage);
            var shouldShow = CallFloatingWindowPolicy.ShouldShow(KernelService.CurrentCallState, presentedCall != null,
                IsVisible, WindowState == WindowState.Minimized, phoneVisible);
            if (shouldShow && presentedCall != null)
            {
                var num = !string.IsNullOrWhiteSpace(KernelService.CurrentCallNumber)
                    ? KernelService.CurrentCallNumber
                    : "当前通话";
                var presentedSlotId = KernelService.Kernel.ActiveCallSlotId;
                var microphone = KernelService.GetMicrophoneUplinkStatus();
                var canMute = microphone.HasSession && microphone.CallId == presentedCall.CallId && microphone.SlotId == presentedSlotId;
                var duration = CallFloatingWindowPolicy.Duration(KernelService.CurrentCallState, presentedCall.ConnectedTime, DateTime.UtcNow);
                var slot = KernelService.Slots.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, presentedSlotId, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? KernelService.ActiveSlot?.Name ?? "SIM 1";

                if (!ReferenceEquals(_floatingCall, presentedCall) || _floatingSlotId != presentedSlotId || !InCallFloatingWindow.IsShowing)
                {
                    _floatingCall = presentedCall; _floatingSlotId = presentedSlotId;
                    InCallFloatingWindow.ShowActiveCall(
                        number: num,
                        durationText: duration,
                        slotInfo: slot,
                        onHangup: async () =>
                        {
                            if (!ReferenceEquals(KernelService.Kernel.ActiveCall, presentedCall) ||
                                !string.Equals(KernelService.Kernel.ActiveCallSlotId, presentedSlotId, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("当前呈现的通话已变化，请在主界面确认。");
                            await KernelService.HangupAsync(presentedSlotId);
                        },
                        onMuteChanged: isMuted =>
                        {
                            if (!ReferenceEquals(KernelService.Kernel.ActiveCall, presentedCall) ||
                                KernelService.Kernel.ActiveCallSlotId != presentedSlotId) return false;
                            return KernelService.SetMicrophoneMuted(isMuted);
                        },
                        isMuted: microphone.IsMuted,
                        canMute: canMute
                    );
                }
                InCallFloatingWindow.UpdatePresentation(num, duration, microphone.IsMuted, canMute);
            }
            else
            {
                _floatingCall = null; _floatingSlotId = null;
                InCallFloatingWindow.Dismiss();
            }
        }

        /// <summary>
        /// Raises the closed event.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _floatingStatusTimer.Stop();
            _gatewayStatusTimer.Stop();
            base.OnClosed(e);

            // Make sure that closing this window will begin the process of closing the application.
            if (Application.Current is App app) _ = app.ShutdownGracefullyAsync();
            else Application.Current.Shutdown();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Initialize system tray icon & minimize-to-tray handling
            TrayManager.Initialize(this);

            // Hook window procedure to listen for USB device arrival events
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            {
                source.AddHook(HwndMessageHook);
            }
        }

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int wp = wParam.ToInt32();
                if (wp == DBT_DEVICEARRIVAL)
                {
                    // USB device inserted!
                    var kernelService = App.Services.GetService(typeof(IVoKernelService)) as IVoKernelService;
                    kernelService?.OnUsbDeviceInserted();
                }
                else if (wp == DBT_DEVICEREMOVECOMPLETE)
                {
                    var kernelService = App.Services.GetService(typeof(IVoKernelService)) as IVoKernelService;
                    kernelService?.OnUsbDeviceRemoved();
                }
            }
            return IntPtr.Zero;
        }

        public void SetServiceProvider(IServiceProvider serviceProvider)
        {
            // Set service provider if supported
        }
    }
}
