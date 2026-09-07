using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VoWin.Services
{
    public static class TrayManager
    {
        private const int WM_USER = 0x0400;
        public const int WM_TRAY_CALLBACK = WM_USER + 101;

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_INFO = 0x00000010;

        private const int NIIF_INFO = 0x00000001;

        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONUP = 0x0205;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static NOTIFYICONDATA _nid;
        private static Window? _mainWindow;
        private static bool _isCreated = false;
        private static bool _isExiting = false;

        public static void Initialize(Window mainWindow)
        {
            _mainWindow = mainWindow;

            var wih = new WindowInteropHelper(mainWindow);
            var hWnd = wih.EnsureHandle();

            var hIcon = GetApplicationIconHandle();

            _nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hWnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY_CALLBACK,
                hIcon = hIcon,
                szTip = "VoWin (后台运行中)"
            };

            Shell_NotifyIcon(NIM_ADD, ref _nid);
            _isCreated = true;

            var source = HwndSource.FromHwnd(hWnd);
            source?.AddHook(TrayWndProc);

            mainWindow.Closing += (s, e) =>
            {
                if (!_isExiting)
                {
                    e.Cancel = true;
                    mainWindow.Hide();
                    ShowNotification("VoWin 已最小化到托盘", "有来电时将在屏幕右上角自动弹出悬浮窗。");
                }
            };

            mainWindow.StateChanged += (s, e) =>
            {
                if (mainWindow.WindowState == WindowState.Minimized)
                {
                    mainWindow.Hide();
                }
            };
        }

        public static void ShowNotification(string title, string message)
        {
            if (!_isCreated) return;

            _nid.uFlags = NIF_INFO;
            _nid.szInfoTitle = title;
            _nid.szInfo = message;
            _nid.dwInfoFlags = NIIF_INFO;

            Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }

        public static void ExitApplication()
        {
            _isExiting = true;
            if (_isCreated)
            {
                Shell_NotifyIcon(NIM_DELETE, ref _nid);
                _isCreated = false;
            }
            Application.Current.Shutdown();
        }

        public static void RestoreMainWindow(bool navigateToPhone = false)
        {
            if (_mainWindow == null) return;
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
            _mainWindow.Focus();

            if (navigateToPhone && _mainWindow is Views.Windows.MainWindow mw)
            {
                mw.Navigate(typeof(Views.Pages.PhonePage));
            }
        }

        private static IntPtr TrayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAY_CALLBACK)
            {
                int mouseMsg = lParam.ToInt32();
                if (mouseMsg == WM_LBUTTONUP)
                {
                    // Left click: restore or toggle window
                    if (_mainWindow != null)
                    {
                        if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
                        {
                            _mainWindow.Hide();
                        }
                        else
                        {
                            RestoreMainWindow();
                        }
                    }
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    // Right click: show context menu
                    ShowTrayContextMenu();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private static void ShowTrayContextMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var itemOpen = new System.Windows.Controls.MenuItem { Header = "显示主界面" };
            itemOpen.Click += (s, e) => RestoreMainWindow();
            menu.Items.Add(itemOpen);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var itemExit = new System.Windows.Controls.MenuItem { Header = "彻底退出 VoWin" };
            itemExit.Click += (s, e) => ExitApplication();
            menu.Items.Add(itemExit);

            menu.IsOpen = true;
        }

        private static IntPtr GetApplicationIconHandle()
        {
            try
            {
                // 1. Try vowin.ico in runtime output directory
                var baseDir = AppContext.BaseDirectory;
                var icoPath = Path.Combine(baseDir, "vowin.ico");
                if (File.Exists(icoPath))
                {
                    return new System.Drawing.Icon(icoPath).Handle;
                }

                // 2. Try project/parent directory if running under debug output folder
                var parentDir = Directory.GetParent(baseDir)?.FullName;
                if (!string.IsNullOrEmpty(parentDir))
                {
                    var parentIco = Path.Combine(parentDir, "vowin.ico");
                    if (File.Exists(parentIco))
                    {
                        return new System.Drawing.Icon(parentIco).Handle;
                    }
                }

                // 3. Extract associated Win32 executable icon (set via ApplicationIcon in csproj)
                if (!string.IsNullOrEmpty(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
                {
                    var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                    if (appIcon != null)
                    {
                        return appIcon.Handle;
                    }
                }

                // 4. Try loading from WPF pack URI resource stream (Assets/vowin.png)
                var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/vowin.png", UriKind.Absolute));
                if (streamInfo?.Stream != null)
                {
                    using var bmp = new System.Drawing.Bitmap(streamInfo.Stream);
                    var hIcon = bmp.GetHicon();
                    if (hIcon != IntPtr.Zero)
                    {
                        return hIcon;
                    }
                }
            }
            catch { }

            // Default application icon (IDI_APPLICATION = 32512)
            return LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }
    }
}
