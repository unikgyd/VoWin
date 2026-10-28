using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
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
        private const string SingleInstanceMutexName = @"Local\VoWin.SingleInstance.v1";
        private const string SingleInstanceActivationEventName = @"Local\VoWin.ActivateExistingInstance.v1";
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

                services.AddHostedService(sp => sp.GetRequiredService<SmsApiService>());
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
                services.AddSingleton<SmsApiService>();
                services.AddSingleton<ISmsApiService>(sp => sp.GetRequiredService<SmsApiService>());

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
        private Task? _stopServicesTask;
        private Task? _shutdownTask;
        private Mutex? _singleInstanceMutex;
        private EventWaitHandle? _singleInstanceActivationEvent;
        private CancellationTokenSource? _singleInstanceListenerCts;
        private Task? _singleInstanceListenerTask;
        private bool _ownsSingleInstanceMutex;
        public static IServiceProvider Services
        {
            get { return _host.Services; }
        }

        private async void OnStartup(object sender, StartupEventArgs e)
        {
            if (e.Args.Length == 2 && e.Args[0] == "--verify-runtime" && Path.IsPathFullyQualified(e.Args[1]))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { await RuntimeSmokeCheck.RunAsync(e.Args[1]); Shutdown(0); }
                catch (Exception ex) { LogCrash("Runtime smoke check", ex); Shutdown(1); }
                return;
            }
            if (CellularAudioWorkerHost.IsRequested(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = await CellularAudioWorkerHost.RunAsync(e.Args);
                Shutdown(exitCode);
                return;
            }

            // An elevated successor must wait for the previous instance to
            // finish modem/PPP cleanup before trying to acquire its ports.
            if (e.Args.Length == 2 && e.Args[0] == "--wait-for-exit" &&
                int.TryParse(e.Args[1], out var predecessorId) && predecessorId != Environment.ProcessId)
            {
                try
                {
                    using var predecessor = Process.GetProcessById(predecessorId);
                    await predecessor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                }
                catch (ArgumentException) { }
                catch (Exception ex)
                {
                    MessageBox.Show($"原进程尚未退出，未启动新的通信实例。{ex.Message}", "VoWin 重启未完成");
                    Shutdown(1);
                    return;
                }
            }

            if (!AcquireSingleInstance())
            {
                Shutdown(0);
                return;
            }

            if (e.Args.Length == 2 && e.Args[0] == "--verify-startup" &&
                (!Path.IsPathFullyQualified(e.Args[1]) || File.Exists(e.Args[1]) ||
                 Environment.GetEnvironmentVariable("VOWIN_TEST_DUAL_MODEM") != "1" ||
                 Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED") != "1"))
            {
                Shutdown(1);
                return;
            }

            // A pre-single-instance release cannot answer the activation event.
            // Detect it by process name as a final guard so two releases never
            // contend for the same modem ports.
            if (HasAnotherVoWinProcess())
            {
                ReleaseSingleInstance();
                MessageBox.Show(
                    "检测到另一个 VoWin 正在运行。请从托盘打开或先彻底退出已有实例，避免两个程序同时占用通信模组。",
                    "VoWin 已在运行",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(0);
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
                StartSingleInstanceActivationListener();
                if (BundledRuntime.IsEmbedded)
                {
                    var setup = new RuntimeSetupWindow();
                    using var preparation = new CancellationTokenSource();
                    setup.Closed += (_, _) => preparation.Cancel();
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    setup.Show();
                    try
                    {
                        await Task.Run(() => BundledRuntime.PrepareAsync(
                            new Progress<string>(message => Dispatcher.Invoke(() => setup.SetStatus(message))), preparation.Token));
                    }
                    catch (OperationCanceledException) { Shutdown(0); return; }
                    catch (Exception ex)
                    {
                        LogCrash("Runtime extraction", ex);
                        MessageBox.Show(setup, $"无法准备内置运行组件，通信尚未启动。\n{ex.Message}\n请检查磁盘空间与目录权限，或重新获取完整发布版。",
                            "VoWin 组件准备失败", MessageBoxButton.OK, MessageBoxImage.Error);
                        Shutdown(1);
                        return;
                    }
                    finally { setup.Close(); }
                    ShutdownMode = ShutdownMode.OnMainWindowClose;
                }
                await _host.StartAsync();
                if (e.Args.Length == 2 && e.Args[0] == "--verify-startup" && Path.IsPathFullyQualified(e.Args[1]))
                {
                    try
                    {
                        var service = _host.Services.GetRequiredService<IVoKernelService>();
                        var report = await StartupSmokeCheck.ObserveAsync(service);
                        var observedSlots = service.Kernel.Pool.Slots.Values.ToArray();
                        await StopApplicationServicesAsync();
                        await StartupSmokeCheck.SaveAsync(e.Args[1], report, service, observedSlots);
                        await ShutdownGracefullyAsync(report.Success ? 0 : 1);
                    }
                    catch (Exception ex)
                    {
                        LogCrash("Published startup verification", ex);
                        await ShutdownGracefullyAsync(1);
                    }
                    return;
                }
                _ = CheckForGitHubReleaseUpdateAsync();
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
            => ApplyBrandTheme(ApplicationThemeManager.GetAppTheme());

        public static void ApplyBrandTheme(ApplicationTheme theme)
        {
            var res = System.Windows.Application.Current?.Resources;
            if (res == null) return;

            var dark = theme == ApplicationTheme.Dark;
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
            // Some status elements receive a brush from a view-model or a value
            // converter rather than directly through DynamicResource. Re-evaluate
            // those bindings after the palette has been replaced.
            Helpers.ThemeBrushes.NotifyThemeResourcesRefreshed();
        }

        private async Task CheckForGitHubReleaseUpdateAsync()
        {
            try
            {
                // Do not delay the main window or modem initialization for a
                // network request. GitHub failures are intentionally silent.
                var installed = Assembly.GetExecutingAssembly().GetName().Version;
                if (installed == null) return;
                var update = await new GitHubReleaseUpdateService().CheckAsync(installed, CancellationToken.None);
                if (update == null) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    var result = MessageBox.Show(
                        $"发现新版本 VoWin v{update.Version.ToString(3)}。\n当前版本：v{installed.ToString(3)}\n\n是否打开 GitHub 下载页面？",
                        "发现新版本",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = update.ReleasePageUrl,
                            UseShellExecute = true
                        });
                    }
                }, DispatcherPriority.ApplicationIdle);
            }
            catch
            {
                // Update checking is best-effort and must never affect startup.
            }
        }

        private bool AcquireSingleInstance()
        {
            try
            {
                _singleInstanceActivationEvent = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: SingleInstanceActivationEventName);
                _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
                _ownsSingleInstanceMutex = createdNew;
                if (createdNew) return true;

                try { _singleInstanceActivationEvent.Set(); } catch { }
                _singleInstanceActivationEvent.Dispose();
                _singleInstanceActivationEvent = null;
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                return false;
            }
            catch (Exception ex)
            {
                LogCrash("SingleInstance.Acquire", ex);
                // Failing open here could let two processes seize hardware, so
                // prefer a safe exit if Windows cannot create the named objects.
                MessageBox.Show("VoWin 无法确认是否已有运行实例，已取消启动以保护通信模组。", "VoWin 启动受保护", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private static bool HasAnotherVoWinProcess()
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                return Process.GetProcessesByName(current.ProcessName)
                    .Any(process =>
                    {
                        try { return process.Id != current.Id; }
                        finally { process.Dispose(); }
                    });
            }
            catch
            {
                return false;
            }
        }

        private void StartSingleInstanceActivationListener()
        {
            var activationEvent = _singleInstanceActivationEvent;
            if (activationEvent == null) return;

            var cts = new CancellationTokenSource();
            _singleInstanceListenerCts = cts;
            _singleInstanceListenerTask = Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        if (!activationEvent.WaitOne(500) || cts.IsCancellationRequested) continue;
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!cts.IsCancellationRequested)
                                TrayManager.RestoreMainWindow();
                        }, DispatcherPriority.ApplicationIdle);
                    }
                }
                catch (ObjectDisposedException) { }
            });
        }

        private void ReleaseSingleInstance()
        {
            var cts = Interlocked.Exchange(ref _singleInstanceListenerCts, null);
            cts?.Cancel();
            try { _singleInstanceActivationEvent?.Set(); } catch { }
            try { _singleInstanceActivationEvent?.Dispose(); } catch { }
            _singleInstanceActivationEvent = null;
            if (_ownsSingleInstanceMutex)
            {
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            }
            _ownsSingleInstanceMutex = false;
            try { _singleInstanceMutex?.Dispose(); } catch { }
            _singleInstanceMutex = null;
            cts?.Dispose();
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
        internal Task ShutdownGracefullyAsync(int exitCode = 0)
        {
            if (!Dispatcher.CheckAccess())
                return Dispatcher.InvokeAsync(() => ShutdownGracefullyAsync(exitCode)).Task.Unwrap();
            return _shutdownTask ??= ShutdownCoreAsync(exitCode);
        }

        private async Task ShutdownCoreAsync(int exitCode)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            TrayManager.BeginExit();
            if (MainWindow is MainWindow main) main.ShowShutdownProgress("正在注销 IMS 并关闭本机连接…");
            try { await StopApplicationServicesAsync(); }
            catch (Exception ex)
            {
                LogCrash("Shutdown incomplete", ex);
                if (MainWindow is MainWindow failed) failed.ShowShutdownProgress("退出清理未完成，未强制结束进程。请保留 shutdown.log；若驱动持续无响应，请保存工作后重启 Windows。");
                return; // Do not release ownership while a driver/lease still belongs to this process.
            }
            _servicesStoppedSafely = true;
            TrayManager.CompleteExit();
            ReleaseSingleInstance(); // Hold ownership until both modem leases have unwound.
            Shutdown(exitCode);
        }

        private bool _servicesStoppedSafely;
        private static readonly ApplicationShutdownCoordinator ShutdownCoordinator = new();

        private Task StopApplicationServicesAsync() => _stopServicesTask ??= StopApplicationServicesCoreAsync();

        private static async Task StopApplicationServicesCoreAsync()
        {
            var stages = new[]
            {
                new ShutdownStage("停止控制和提醒与后台服务…", () => _host.StopAsync()),
                new ShutdownStage("注销 IMS、停止语音并关闭本机连接（不等待模组恢复）…", async () =>
                {
                    if (_host.Services.GetService<IVoKernelService>() is { } kernel) await kernel.DisposeAsync();
                }),
                new ShutdownStage("等待原生收发 I/O 和驱动句柄安全释放…", async () =>
                {
                    while (!await VoSharp.Telephony.HostImsIoCleanup.WaitForCompletionAsync(TimeSpan.FromSeconds(2)))
                        await Task.Delay(250);
                }),
                new ShutdownStage("释放应用服务…", async () =>
                {
                    try
                    {
                        if (_host is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
                        else _host.Dispose();
                    }
                    catch (ObjectDisposedException ex)
                    {
                        // This stage is reached only AFTER kernel/lease teardown
                        // and the native-I/O barrier. A duplicate managed-service
                        // dispose must not leave a safely disconnected app stuck.
                        LogCrash("Managed service already disposed after communication cleanup", ex);
                    }
                })
            };
            await ShutdownCoordinator.StopAsync(stages, message =>
            {
                try
                {
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Logs");
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, "shutdown.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
                }
                catch { }
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher.HasShutdownStarted == false)
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (app.MainWindow is MainWindow window) window.ShowShutdownProgress(message);
                    }));
            }, TimeSpan.FromSeconds(30));
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            ReleaseSingleInstance();
            // Normal exits await cleanup before WPF stops its dispatcher. This
            // event is synchronous: async work here cannot keep a process alive.
            try
            {
                if (!_servicesStoppedSafely) _host.Dispose();
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
