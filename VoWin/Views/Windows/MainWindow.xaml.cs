using System;
using System.Windows;
using VoWin.Services;
using VoWin.ViewModels.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace VoWin.Views.Windows
{
    public partial class MainWindow : INavigationWindow
    {
        public MainWindowViewModel ViewModel { get; }
        public IVoKernelService KernelService { get; }

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

            SystemThemeWatcher.Watch(this);

            InitializeComponent();
            SetPageService(navigationViewPageProvider);

            navigationService.SetNavigationControl(RootNavigation);
            snackbarService.SetSnackbarPresenter(SnackbarPresenter);

            HookInCallFloatingWindow();
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

            KernelService.Kernel.CallStateChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(CheckInCallFloatingWindow);
            };

            KernelService.Kernel.CallEnded += (s, e) =>
            {
                Dispatcher.BeginInvoke(InCallFloatingWindow.Dismiss);
            };
        }

        private void CheckInCallFloatingWindow()
        {
            bool isCallActive = KernelService.CurrentCallState is VoSharp.Telephony.Calls.CallState.Active
                             or VoSharp.Telephony.Calls.CallState.Dialing
                             or VoSharp.Telephony.Calls.CallState.Ringing;

            bool isMainHidden = !IsVisible || WindowState == WindowState.Minimized;

            if (isCallActive && isMainHidden)
            {
                var num = !string.IsNullOrWhiteSpace(KernelService.CurrentCallNumber)
                    ? KernelService.CurrentCallNumber
                    : "当前通话";
                var slot = KernelService.ActiveSlot?.Name ?? "SIM 1";

                InCallFloatingWindow.ShowActiveCall(
                    number: num,
                    durationText: "通话中",
                    slotInfo: slot,
                    onHangup: async () =>
                    {
                        await KernelService.HangupAsync();
                    },
                    onMuteChanged: isMuted =>
                    {
                    }
                );
            }
            else if (!isCallActive || !isMainHidden)
            {
                InCallFloatingWindow.Dismiss();
            }
        }

        /// <summary>
        /// Raises the closed event.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // Make sure that closing this window will begin the process of closing the application.
            Application.Current.Shutdown();
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
