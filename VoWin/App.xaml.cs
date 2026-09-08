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
using Wpf.Ui.Appearance;
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
                RestoreThemePreference();
                ApplicationThemeManager.Changed += (_, _) =>
                    Dispatcher.BeginInvoke(ApplyBrandTheme, DispatcherPriority.Background);
                ApplyBrandTheme();
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
        /// Applies VoWin's adaptive light-blue / navy-black visual system after
        /// WPF-UI swaps its theme resources.
        /// </summary>
        public static void ApplyBrandTheme()
        {
            var res = System.Windows.Application.Current?.Resources;
            if (res == null) return;

            var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
            var palette = dark
                ? new Dictionary<string, string>
                {
                    ["AppSurfaceBrush"] = "#0B1220",
                    ["AppNavigationBrush"] = "#0E1828",
                    ["AppCardBrush"] = "#121E30",
                    ["AppCardElevatedBrush"] = "#17253A",
                    ["AppCardBorderBrush"] = "#2A3D58",
                    ["AppDividerBrush"] = "#24364F",
                    ["AppInputBrush"] = "#0F1928",
                    ["AppHoverBrush"] = "#182A42",
                    ["AppSelectedBrush"] = "#1D3554",
                    ["AppOverlayBrush"] = "#99050A12",
                    ["AppTextPrimaryBrush"] = "#F2F7FF",
                    ["AppTextSecondaryBrush"] = "#B4C2D5",
                    ["AppTextTertiaryBrush"] = "#8598B2",
                    ["AppTextOnAccentBrush"] = "#FFFFFF",
                    ["AppTextOnAccentSecondaryBrush"] = "#E5F0FF",
                    ["AppAccentBrush"] = "#60A5FA",
                    ["AppAccentHoverBrush"] = "#7CB5FA",
                    ["AppAccentPressedBrush"] = "#3B82F6",
                    ["AppAccentSoftBrush"] = "#1A3352",
                    ["AppAccentSoftBorderBrush"] = "#315D8F",
                    ["AppSuccessBrush"] = "#34D399",
                    ["AppSuccessSoftBrush"] = "#12382F",
                    ["AppWarningBrush"] = "#FBBF24",
                    ["AppWarningSoftBrush"] = "#3B2C12",
                    ["AppDangerBrush"] = "#FB7185",
                    ["AppDangerSoftBrush"] = "#431D2A",
                    ["AppInfoBrush"] = "#38BDF8",
                    ["AppInfoSoftBrush"] = "#123447",
                    ["ApplicationBackgroundBrush"] = "#0B1220",
                    ["CardBackgroundFillColorDefaultBrush"] = "#121E30",
                    ["CardBackgroundFillColorSecondaryBrush"] = "#0F1928",
                    ["CardStrokeColorDefaultBrush"] = "#2A3D58"
                }
                : new Dictionary<string, string>
                {
                    ["AppSurfaceBrush"] = "#F3F8FF",
                    ["AppNavigationBrush"] = "#EAF3FF",
                    ["AppCardBrush"] = "#FCFEFF",
                    ["AppCardElevatedBrush"] = "#FFFFFF",
                    ["AppCardBorderBrush"] = "#CFE0F5",
                    ["AppDividerBrush"] = "#D9E6F5",
                    ["AppInputBrush"] = "#F8FBFF",
                    ["AppHoverBrush"] = "#E7F0FF",
                    ["AppSelectedBrush"] = "#DCEBFF",
                    ["AppOverlayBrush"] = "#66081220",
                    ["AppTextPrimaryBrush"] = "#142033",
                    ["AppTextSecondaryBrush"] = "#52657A",
                    ["AppTextTertiaryBrush"] = "#71839A",
                    ["AppTextOnAccentBrush"] = "#FFFFFF",
                    ["AppTextOnAccentSecondaryBrush"] = "#DCE8FF",
                    ["AppAccentBrush"] = "#2563EB",
                    ["AppAccentHoverBrush"] = "#1D4ED8",
                    ["AppAccentPressedBrush"] = "#1E40AF",
                    ["AppAccentSoftBrush"] = "#E7F0FF",
                    ["AppAccentSoftBorderBrush"] = "#AFC9F3",
                    ["AppSuccessBrush"] = "#0F9F75",
                    ["AppSuccessSoftBrush"] = "#E7F8F2",
                    ["AppWarningBrush"] = "#C66A08",
                    ["AppWarningSoftBrush"] = "#FFF5E6",
                    ["AppDangerBrush"] = "#D92D4C",
                    ["AppDangerSoftBrush"] = "#FFF0F3",
                    ["AppInfoBrush"] = "#0284C7",
                    ["AppInfoSoftBrush"] = "#E8F6FD",
                    ["ApplicationBackgroundBrush"] = "#F3F8FF",
                    ["CardBackgroundFillColorDefaultBrush"] = "#FCFEFF",
                    ["CardBackgroundFillColorSecondaryBrush"] = "#F8FBFF",
                    ["CardStrokeColorDefaultBrush"] = "#CFE0F5"
                };

            foreach (var (key, color) in palette)
            {
                var nextColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
                if (res[key] is System.Windows.Media.SolidColorBrush existing && !existing.IsFrozen)
                {
                    // Mutating the existing brush also refreshes values returned
                    // by converters and view-model bindings, not only DynamicResource.
                    existing.Color = nextColor;
                }
                else
                {
                    res[key] = new System.Windows.Media.SolidColorBrush(nextColor);
                }
            }

            var accent = (System.Windows.Media.SolidColorBrush)res["AppAccentBrush"];
            var accentHover = (System.Windows.Media.SolidColorBrush)res["AppAccentHoverBrush"];
            var accentPressed = (System.Windows.Media.SolidColorBrush)res["AppAccentPressedBrush"];
            var disabled = new System.Windows.Media.SolidColorBrush(accent.Color) { Opacity = 0.32 };
            disabled.Freeze();

            res["ToggleSwitchFillOn"] = accent;
            res["ToggleSwitchFillOnPointerOver"] = accentHover;
            res["ToggleSwitchFillOnPressed"] = accentPressed;
            res["ToggleSwitchStrokeOn"] = accent;
            res["ToggleSwitchStrokeOnPointerOver"] = accentHover;
            res["ToggleSwitchStrokeOnPressed"] = accentPressed;
            res["ToggleSwitchFillOnDisabled"] = disabled;
            res["ToggleButtonBackgroundChecked"] = accent;
            res["ToggleButtonBackgroundCheckedPointerOver"] = accentHover;
            res["ToggleButtonBackgroundCheckedPressed"] = accentPressed;
            res["ToggleButtonBorderBrushChecked"] = accent;
            res["ToggleButtonBorderBrushCheckedPressed"] = accentPressed;
        }

        [Obsolete("Use ApplyBrandTheme instead.")]
        public static void ApplyGreenToggleTheme() => ApplyBrandTheme();

        private static string ThemePreferencePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoWin",
            "ui-theme.txt");

        private static void RestoreThemePreference()
        {
            try
            {
                if (!File.Exists(ThemePreferencePath)) return;
                var saved = File.ReadAllText(ThemePreferencePath).Trim();
                if (Enum.TryParse<ApplicationTheme>(saved, true, out var theme) &&
                    theme is ApplicationTheme.Light or ApplicationTheme.Dark)
                {
                    ApplicationThemeManager.Apply(theme);
                }
            }
            catch
            {
                // Theme persistence must never prevent the app from starting.
            }
        }

        public static void SaveThemePreference(ApplicationTheme theme)
        {
            if (theme is not (ApplicationTheme.Light or ApplicationTheme.Dark)) return;
            try
            {
                var directory = Path.GetDirectoryName(ThemePreferencePath)!;
                Directory.CreateDirectory(directory);
                File.WriteAllText(ThemePreferencePath, theme.ToString());
            }
            catch
            {
                // A read-only profile should not break live theme switching.
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
