using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using VoWin.Services;
using VoWin.ViewModels.Pages;
using VoWin.ViewModels.Windows;
using VoWin.Views.Pages;
using VoWin.Views.Windows;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace VoWin
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        // The.NET Generic Host provides dependency injection, configuration, logging, and other services.
        // https://docs.microsoft.com/dotnet/core/extensions/generic-host
        // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
        // https://docs.microsoft.com/dotnet/core/extensions/configuration
        // https://docs.microsoft.com/dotnet/core/extensions/logging
        private static readonly IHost _host = Host
            .CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => { c.SetBasePath(Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory); })
            .ConfigureServices((context, services) =>
            {
                services.AddNavigationViewPageProvider();

                services.AddHostedService<ApplicationHostService>();

                // Theme manipulation
                services.AddSingleton<IThemeService, ThemeService>();

                // TaskBar manipulation
                services.AddSingleton<ITaskBarService, TaskBarService>();

                // Service containing navigation, same as INavigationWindow... but without window
                services.AddSingleton<INavigationService, NavigationService>();

                // In-app Snackbar / Toast notification service
                services.AddSingleton<ISnackbarService, SnackbarService>();

                // Main window with navigation
                services.AddSingleton<INavigationWindow, MainWindow>();
                services.AddSingleton<MainWindowViewModel>();

                // SQLite Preferences & Telecom Kernel Service
                services.AddSingleton<IPreferenceDatabaseService, PreferenceDatabaseService>();
                services.AddSingleton<IVoKernelService, VoKernelService>();
                services.AddSingleton<VoWin.Services.RemoteControl.RemoteControlService>();
                services.AddSingleton<IRemoteControlService>(sp => sp.GetRequiredService<VoWin.Services.RemoteControl.RemoteControlService>());
                services.AddHostedService(sp => sp.GetRequiredService<VoWin.Services.RemoteControl.RemoteControlService>());

                // Pages and ViewModels
                services.AddSingleton<ModemManagerPage>();
                services.AddSingleton<ModemManagerViewModel>();
                services.AddSingleton<PhonePage>();
                services.AddSingleton<PhoneViewModel>();
                services.AddSingleton<MessagesPage>();
                services.AddSingleton<MessagesViewModel>();
                services.AddSingleton<SystemStatusPage>();
                services.AddSingleton<SystemStatusViewModel>();
                services.AddSingleton<ProxyPage>();
                services.AddSingleton<ProxyViewModel>();
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<RemoteControlPage>();
                services.AddSingleton<RemoteControlViewModel>();
                services.AddSingleton<AboutPage>();
                services.AddSingleton<AboutViewModel>();
            }).Build();
        private bool _cellularAudioHelperMode;
        public static IServiceProvider Services
        {
            get { return _host.Services; }
        }

        private async void OnStartup(object sender, StartupEventArgs e)
        {
            if (CellularAudioWorkerHost.IsRequested(e.Args))
            {
                _cellularAudioHelperMode = true;
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = await CellularAudioWorkerHost.RunAsync(e.Args);
                Shutdown(exitCode);
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, exArgs) =>
            {
                LogCrash("AppDomain.UnhandledException", exArgs.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (s, taskArgs) =>
            {
                LogCrash("TaskScheduler.UnobservedTaskException", taskArgs.Exception);
                taskArgs.SetObserved();
            };

            try
            {
                ApplyGreenToggleTheme();
                await _host.StartAsync();
            }
            catch (Exception ex)
            {
                LogCrash("OnStartup._host.Start", ex);
            }
        }

        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin");
                Directory.CreateDirectory(logDir);
                var crashFile = Path.Combine(logDir, "crash.log");
                File.AppendAllText(crashFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\r\n\r\n");
            }
            catch { }
        }

        /// <summary>
        /// 确保全局开关与切换控件开启时呈现统一的翡翠绿色调
        /// </summary>
        public static void ApplyGreenToggleTheme()
        {
            var greenBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Emerald 500
            greenBrush.Freeze();
            var hoverBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69)); // Emerald 600
            hoverBrush.Freeze();
            var pressedBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x04, 0x78, 0x57)); // Emerald 700
            pressedBrush.Freeze();
            var disabledBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x4D, 0x10, 0xB9, 0x81));
            disabledBrush.Freeze();

            var res = System.Windows.Application.Current?.Resources;
            if (res != null)
            {
                res["ToggleSwitchFillOn"] = greenBrush;
                res["ToggleSwitchFillOnPointerOver"] = hoverBrush;
                res["ToggleSwitchFillOnPressed"] = pressedBrush;
                res["ToggleSwitchStrokeOn"] = greenBrush;
                res["ToggleSwitchStrokeOnPointerOver"] = hoverBrush;
                res["ToggleSwitchStrokeOnPressed"] = pressedBrush;
                res["ToggleSwitchFillOnDisabled"] = disabledBrush;

                res["ToggleButtonBackgroundChecked"] = greenBrush;
                res["ToggleButtonBackgroundCheckedPointerOver"] = hoverBrush;
                res["ToggleButtonBackgroundCheckedPressed"] = pressedBrush;
                res["ToggleButtonBorderBrushChecked"] = greenBrush;
                res["ToggleButtonBorderBrushCheckedPressed"] = pressedBrush;
            }
        }

        /// <summary>
        /// Occurs when the application is closing.
        /// </summary>
        private async void OnExit(object sender, ExitEventArgs e)
        {
            if (_cellularAudioHelperMode)
            {
                try { _host.Dispose(); } catch { }
                return;
            }

            var kernelService = _host.Services.GetService<IVoKernelService>();
            if (kernelService != null)
            {
                try { await kernelService.DisposeAsync(); } catch { }
            }

            try
            {
                await _host.StopAsync();
            }
            catch { }

            try
            {
                _host.Dispose();
            }
            catch { }
        }

        /// <summary>
        /// Occurs when an exception is thrown by an application but not handled.
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin");
                Directory.CreateDirectory(logDir);
                var crashFile = Path.Combine(logDir, "crash.log");
                File.AppendAllText(crashFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DispatcherUnhandledException: {e.Exception}\r\n\r\n");
            }
            catch { }

            e.Handled = true;
        }
    }
}
