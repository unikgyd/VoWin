using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoSharp.Kernel;
using VoWin.Services;
using VoWin.ViewModels.Pages;
using VoWin.Views.Pages;
using VoWin.Views.Controls;
using VoWin.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VoWin.Tests;

public class SystemStatusLayoutTests
{
    [Fact]
    public async Task ModeCardsRenderAtNarrowAndWideWidthsWithoutBindingErrors()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Test-only: Windows can suspend WPF rasterization when the desktop
                // has no active display (locked/disconnected session). Never opt the
                // user's normal application into continuous headless rendering.
                AppContext.SetSwitch("Switch.System.Windows.Media.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable", true);
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using var bindingErrors = new StringWriter();
                using var bindingListener = new TextWriterTraceListener(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
                application.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
                application.Resources.MergedDictionaries.Add(new ControlsDictionary());
                application.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                    new Uri("/VoWin;component/Themes/DesignSystem.xaml", UriKind.Relative)));
                var directory = Path.Combine(Path.GetTempPath(), "VoWin-layout-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var service = new VoKernelService(new PreferenceDatabaseService(Path.Combine(directory, "db.sqlite")),
                    new TelephonySettingsStore(Path.Combine(directory, "telephony.json")), Path.Combine(directory, "test.log"));
                var model = new SystemStatusViewModel(service) { IsLogPanelExpanded = false };
                typeof(VoKernelService).GetProperty(nameof(VoKernelService.IsInitialScanRunning))!.SetValue(service, false);
                var artifacts = Path.Combine(AppContext.BaseDirectory, "ui-artifacts");
                Directory.CreateDirectory(artifacts);
                var paintProbe = new DrawingVisual();
                using (var canvas = paintProbe.RenderOpen()) canvas.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
                var paintBitmap = new RenderTargetBitmap(10, 10, 96, 96, PixelFormats.Pbgra32);
                paintBitmap.Render(paintProbe);
                var paintPixel = new byte[4]; paintBitmap.CopyPixels(new Int32Rect(5, 5, 1, 1), paintPixel, 4, 0);
                Assert.True(paintPixel[3] == 255, $"WPF drawing renderer unavailable: pixel={string.Join(',', paintPixel)}.");
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                {
                ApplicationThemeManager.Apply(theme);
                VoWin.App.ApplyBrandTheme(theme);
                Assert.Equal(theme == ApplicationTheme.Dark ? "#FF0B1220" : "#FFF3F8FF",
                    ((SolidColorBrush)application.Resources["AppSurfaceBrush"]).Color.ToString());
                // Detached visuals do not receive application resource-change
                // invalidations. Create a fresh page for each theme.
                var page = new SystemStatusPage(model);
                page.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                foreach (var width in new[] { 800, 1200 })
                foreach (var mode in model.TelephonyModes)
                {
                    model.SelectedTelephonyMode = mode;
                    page.Width = width;
                    page.Height = 900;
                    page.Measure(new Size(width, 900));
                    page.Arrange(new Rect(0, 0, width, 900));
                    page.UpdateLayout();
                    // Flush data-binding notifications, without opening any
                    // native window, modem port or network connection.
                    page.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    page.UpdateLayout();
                    var selector = Descendants<ListBox>(page).Single(list => System.Windows.Automation.AutomationProperties.GetName(list) == "蜂窝 IMS 模式");
                    Assert.Equal(2, selector.Items.Count);
                    Assert.DoesNotContain(selector.Items.Cast<TelephonyModeOption>(), option => option.Mode == VoSharp.Kernel.TelephonyMode.VoWifi);
                    Assert.Single(Descendants<Button>(page), button => button.Command == model.ToggleIndependentVoWifiCommand);
                    Assert.True(selector.ActualWidth > 450);
                    var diagnosticToggle = Descendants<CheckBox>(page).Single(box => Equals(box.Content, "记录详细日志"));
                    Assert.True(diagnosticToggle.IsEnabled);
                    var export = Descendants<Button>(page).Single(button => Equals(button.Content, "导出诊断包"));
                    Assert.True(export.IsEnabled); // Available in every mode, including with no SIM.
                    foreach (var item in selector.Items)
                    {
                        var container = (ListBoxItem)selector.ItemContainerGenerator.ContainerFromItem(item);
                        Assert.True(container.ActualWidth >= 140);
                        Assert.False(container.HasContent && container.ActualHeight <= 0);
                    }
                    var bitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(page);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(artifacts, $"mode-{mode.Mode}-{width}-{theme}.png"));
                    encoder.Save(output);
                }
                }
                model.IsLogPanelExpanded = true;
                foreach (var width in new[] { 800, 1200 })
                {
                    var logPage = new SystemStatusPage(model) { Width = width, Height = 900 };
                    logPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                    logPage.Measure(new Size(width, 900));
                    logPage.Arrange(new Rect(0, 0, width, 900));
                    logPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    logPage.UpdateLayout();
                    var panel = (Border)logPage.FindName("ExpandedTerminalPanel");
                    Assert.Equal(width < 1100 ? 1 : 0, Grid.GetRow(panel));
                    Assert.True(panel.ActualWidth > 300 && panel.ActualWidth < width);
                    var export = Descendants<Button>(panel).Single(button => Equals(button.Content, "导出诊断包"));
                    Assert.True(export.ActualWidth > 80);
                    var bitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(logPage);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(artifacts, $"integrated-diagnostics-{width}.png")); encoder.Save(output);
                    var details = (TabItem)logPage.FindName("ConnectionDetailsTab");
                    details.IsSelected = true;
                    logPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    logPage.UpdateLayout();
                    Assert.True(details.IsSelected);
                    Assert.DoesNotContain(Descendants<Expander>(logPage), expander => Equals(expander.Header, "VoWiFi 连接诊断"));
                    Assert.DoesNotContain(Descendants<Expander>(logPage), expander => Equals(expander.Header, "高级诊断 · Host IMS"));
                    var detailsBitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32);
                    detailsBitmap.Render(logPage);
                    var detailsEncoder = new PngBitmapEncoder(); detailsEncoder.Frames.Add(BitmapFrame.Create(detailsBitmap));
                    using var detailsOutput = File.Create(Path.Combine(artifacts, $"connection-details-{width}.png")); detailsEncoder.Save(detailsOutput);
                }
                model.IsLogPanelExpanded = false;
                var smsApiProxy = System.Reflection.DispatchProxy.Create<ISmsApiService, PhoneCallServiceProxy>();
                ((PhoneCallServiceProxy)smsApiProxy).Handler = (method, _) => method.Name switch
                {
                    "get_Settings" => new SmsApiSettings(),
                    "get_Indicator" => new GatewayStatusItem("短信 API", "未启用", "只读接口，默认关闭。"),
                    "add_StatusChanged" or "remove_StatusChanged" => null,
                    _ => throw new NotSupportedException(method.Name)
                };
                var settingsModel = new SettingsViewModel(service, smsApi: smsApiProxy);
                settingsModel.LoadSmsApiSettings();
                settingsModel.SipModuleRoutes.Add(new("module-a", new() { IncomingTarget = "1001" }));
                settingsModel.SipModuleRoutes.Add(new("module-b", new() { IncomingTarget = "1002" }));
                settingsModel.RefreshSipModuleRoutesCore();
                settingsModel.SipMessagesEnabled = true;
                settingsModel.SipExtensions.Add(new() { Username = "desk-test", Password = "synthetic-test-secret" });
                settingsModel.SipDialRules.Add(new() { Prefix = "71", StripPrefix = true, Prepend = "+", LineId = "module-a" });
                Assert.Equal(VoSharp.Modem.ModemControlPreference.QmiPreferred, settingsModel.SelectedControlPreference);
                var settingsPage = new SettingsPage(settingsModel) { Width = 800, Height = 900 };
                settingsPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                settingsPage.Measure(new Size(800, 900)); settingsPage.Arrange(new Rect(0, 0, 800, 900));
                settingsPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); settingsPage.UpdateLayout();
                var controlChoice = Descendants<ComboBox>(settingsPage).Single(box => box.Items.Count == 2 && box.Items[0] is ModemControlOption);
                Assert.Equal(VoSharp.Modem.ModemControlPreference.QmiPreferred, controlChoice.SelectedValue);
                controlChoice.SelectedValue = VoSharp.Modem.ModemControlPreference.AtOnly;
                settingsPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(VoSharp.Modem.ModemControlPreference.AtOnly, settingsModel.SelectedControlPreference);
                controlChoice.SelectedValue = VoSharp.Modem.ModemControlPreference.QmiPreferred;
                settingsPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.True(Descendants<Wpf.Ui.Controls.ToggleSwitch>(settingsPage)
                    .Single(toggle => Equals(toggle.Content, "启动及插入 USB 时自动检测通信设备")).IsChecked);
                Assert.True(Descendants<Wpf.Ui.Controls.ToggleSwitch>(settingsPage)
                    .Single(toggle => Equals(toggle.Content, "启用 VoLTE 音频预热")).IsChecked);
                var settingsBitmap = new RenderTargetBitmap(800, 900, 96, 96, PixelFormats.Pbgra32); settingsBitmap.Render(settingsPage);
                var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap));
                using (var output = File.Create(Path.Combine(artifacts, "settings-control-preference.png"))) settingsEncoder.Save(output);
                foreach (var sipTheme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var width in new[] { 800, 1200 })
                foreach (var trunk in new[] { false, true })
                {
                    ApplicationThemeManager.Apply(sipTheme);
                    VoWin.App.ApplyBrandTheme(sipTheme);
                    settingsModel.SipGatewayUseTrunk = trunk;
                    var sipPage = new SettingsPage(settingsModel) { Width = width, Height = 900 };
                    sipPage.Measure(new Size(width, 900)); sipPage.Arrange(new Rect(0, 0, width, 900));
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); sipPage.UpdateLayout();
                    foreach (var expander in Descendants<Expander>(sipPage)) expander.IsExpanded = true;
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); sipPage.UpdateLayout();
                    var messageToggle = Descendants<Wpf.Ui.Controls.ToggleSwitch>(sipPage).Single(toggle => Equals(toggle.Content, "启用 SIP 文本消息"));
                    messageToggle.BringIntoView();
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); sipPage.UpdateLayout();
                    var sipScroll = Descendants<ScrollViewer>(sipPage).First();
                    var messageBounds = messageToggle.TransformToAncestor(sipPage).TransformBounds(new Rect(messageToggle.RenderSize));
                    sipScroll.ScrollToVerticalOffset(sipScroll.VerticalOffset + messageBounds.Top - 140);
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); sipPage.UpdateLayout();
                    Assert.True(messageToggle.IsChecked);
                    var messageBitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32); messageBitmap.Render(sipPage);
                    var messageEncoder = new PngBitmapEncoder(); messageEncoder.Frames.Add(BitmapFrame.Create(messageBitmap));
                    using (var messageOutput = File.Create(Path.Combine(artifacts, $"sip-messages-{sipTheme}-{width}-{(trunk ? "trunk" : "direct")}.png"))) messageEncoder.Save(messageOutput);
                    var restoreSip = Descendants<CheckBox>(sipPage).Single(box =>
                        Equals(box.Content, "记住配置，并在启动时恢复上次 SIP 开关状态"));
                    restoreSip.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    Assert.True(settingsModel.SipGatewayRestoreOnStartup);
                    Assert.Equal(Visibility.Visible, restoreSip.Visibility);
                    Assert.True(restoreSip.ActualHeight >= 16); // Detached render pages have IsVisible=false.
                    restoreSip.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    Assert.False(settingsModel.SipGatewayRestoreOnStartup);
                    Descendants<ScrollViewer>(sipPage).First().ScrollToBottom();
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); sipPage.UpdateLayout();
                    var trunkPanel = (StackPanel)sipPage.FindName("SipTrunkPanel");
                    Assert.Equal(trunk ? Visibility.Visible : Visibility.Collapsed, trunkPanel.Visibility);
                    Assert.True(((StackPanel)sipPage.FindName("SipConfigurationPanel")).IsEnabled);
                    Assert.Contains(Descendants<TextBlock>(sipPage), text => text.Text == "SIP 对接 · 可选");
                    var moduleRoutes = Descendants<ItemsControl>(sipPage).Single(items =>
                        System.Windows.Automation.AutomationProperties.GetName(items) == "内部 VoIP 模组线路");
                    Assert.Equal(2, moduleRoutes.Items.Count);
                    var expose = Descendants<CheckBox>(moduleRoutes).Where(box => Equals(box.Content, "接入内部 VoIP")).ToArray();
                    Assert.Equal(2, expose.Length);
                    expose[0].SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    Assert.False(settingsModel.SipModuleRoutes[0].Enabled);
                    Assert.True(settingsModel.SipModuleRoutes[1].Enabled);
                    settingsModel.SipModuleRoutes[0].Enabled = true;
                    sipPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    sipPage.UpdateLayout();
                    Assert.True(settingsModel.SipModuleRoutes[0].Enabled);
                    Assert.True(expose[0].IsChecked);
                    Assert.Contains(Descendants<TextBox>(moduleRoutes), box => box.Text == "1001");
                    Assert.Contains(Descendants<TextBox>(moduleRoutes), box => box.Text == "1002");
                    var sipBitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32); sipBitmap.Render(sipPage);
                    var sipEncoder = new PngBitmapEncoder(); sipEncoder.Frames.Add(BitmapFrame.Create(sipBitmap));
                    using var sipOutput = File.Create(Path.Combine(artifacts, $"sip-settings-{sipTheme}-{width}-{(trunk ? "trunk" : "direct")}.png"));
                    sipEncoder.Save(sipOutput);
                }
                var dependencyPage = new SystemStatusPage(model) { Width = 800, Height = 900 };
                dependencyPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                dependencyPage.Measure(new Size(800, 900));
                dependencyPage.Arrange(new Rect(0, 0, 800, 900));
                dependencyPage.UpdateLayout();
                var dependencyPanel = Descendants<Expander>(dependencyPage)
                    .Single(panel => Equals(panel.Header, "运行组件与系统驱动"));
                dependencyPanel.IsExpanded = true;
                dependencyPage.UpdateLayout();
                dependencyPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.True(dependencyPanel.ActualHeight > 100);
                var summaryText = Descendants<TextBlock>(dependencyPanel).Single(text =>
                    text.Text == model.RuntimeDependencySummary);
                Assert.Equal(((SolidColorBrush)application.Resources["AppTextPrimaryBrush"]).Color,
                    ((SolidColorBrush)summaryText.Foreground).Color);
                var dependencyBitmap = new RenderTargetBitmap(800, 900, 96, 96, PixelFormats.Pbgra32);
                dependencyBitmap.Render(dependencyPage);
                var dependencyEncoder = new PngBitmapEncoder();
                dependencyEncoder.Frames.Add(BitmapFrame.Create(dependencyBitmap));
                using (var output = File.Create(Path.Combine(artifacts, "runtime-dependencies-expanded.png")))
                    dependencyEncoder.Save(output);
                var offlineSlot = new VoSharp.Kernel.Pool.ModemSlot("offline-recovery", "COM999");
                typeof(VoSharp.Kernel.Pool.ModemSlot).GetProperty(nameof(VoSharp.Kernel.Pool.ModemSlot.Sim))!
                    .SetValue(offlineSlot, VoSharp.Sim.SimIdentity.FromImsiAndIccid("460110000000001", "test-offline-sim"));
                typeof(SystemStatusViewModel).GetField("_selectedSlot", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.SetValue(model, offlineSlot);
                model.SelectedTelephonyMode = model.TelephonyModes.Single(option => option.Mode == TelephonyMode.HostIms);
                Assert.Equal("重新扫描", model.TelephonyActionText);
                model.TelephonyOperationMessage = "上次操作的旧提示";
                Assert.Equal("设备已断开，请重新扫描。", model.TelephonyUsageHint);
                var recoveryPage = new SystemStatusPage(model) { Width = 800, Height = 900 };
                recoveryPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                recoveryPage.Measure(new Size(800, 900));
                recoveryPage.Arrange(new Rect(0, 0, 800, 900));
                recoveryPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                recoveryPage.UpdateLayout();
                Assert.Contains(Descendants<Button>(recoveryPage), button => Equals(button.Content, "重新扫描"));
                Assert.Contains(Descendants<TextBlock>(recoveryPage), text => text.Text == "设备已断开，请重新扫描。");
                var recoveryBitmap = new RenderTargetBitmap(800, 900, 96, 96, PixelFormats.Pbgra32);
                recoveryBitmap.Render(recoveryPage);
                var recoveryEncoder = new PngBitmapEncoder();
                recoveryEncoder.Frames.Add(BitmapFrame.Create(recoveryBitmap));
                using (var output = File.Create(Path.Combine(artifacts, "host-ims-offline-recovery.png")))
                    recoveryEncoder.Save(output);
                offlineSlot.DisposeAsync().AsTask().GetAwaiter().GetResult();
                var pendingScroll = typeof(SystemStatusPage).GetField("_scrollPending", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!;
                recoveryPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                recoveryPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                recoveryPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                model.IsAutoScrollPaused = true;
                model.IsAutoScrollPaused = false;
                Assert.Equal(0, pendingScroll.GetValue(recoveryPage));
                recoveryPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                recoveryPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                model.IsAutoScrollPaused = true;
                model.IsAutoScrollPaused = false;
                Assert.Equal(1, pendingScroll.GetValue(recoveryPage)); // Follow resumes after navigating back.
                recoveryPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                // Two independent modules in ONE window. Adding a second
                // module must refresh tiles even when the first stays selected.
                model.SelectedSlot = null;
                var firstModule = new VoSharp.Kernel.Pool.ModemSlot("module-a", "COM96");
                var secondModule = new VoSharp.Kernel.Pool.ModemSlot("module-b", "COM97");
                typeof(VoSharp.Kernel.Pool.ModemSlot).GetProperty(nameof(firstModule.DeviceModel))!.SetValue(firstModule, "QDC507");
                typeof(VoSharp.Kernel.Pool.ModemSlot).GetProperty(nameof(secondModule.DeviceModel))!.SetValue(secondModule, "EC20-CE");
                typeof(VoSharp.Kernel.Pool.ModemSlot).GetProperty(nameof(firstModule.Sim))!
                    .SetValue(firstModule, VoSharp.Sim.SimIdentity.FromImsiAndIccid("460110000000001", "ui-cn-card"));
                typeof(VoSharp.Kernel.Pool.ModemSlot).GetProperty(nameof(secondModule.Sim))!
                    .SetValue(secondModule, VoSharp.Sim.SimIdentity.FromImsiAndIccid("515660000000001", "ui-ph-card"));
                foreach (var module in new[] { firstModule, secondModule })
                {
                    Assert.True(service.Kernel.Pool.TryAddSlot(module));
                    if (!service.Slots.Contains(module)) service.Slots.Add(module);
                }
                model.SelectedSlot = firstModule;
                ApplicationThemeManager.Apply(ApplicationTheme.Light); VoWin.App.ApplyBrandTheme(ApplicationTheme.Light);
                var modulesPage = new SystemStatusPage(model) { Width = 1200, Height = 900 };
                modulesPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                modulesPage.Measure(new Size(1200, 900)); modulesPage.Arrange(new Rect(0, 0, 1200, 900));
                modulesPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); modulesPage.UpdateLayout();
                var instances = Descendants<ListBox>(modulesPage).Single(list => System.Windows.Automation.AutomationProperties.GetName(list) == "模组实例");
                Assert.Equal(2, instances.Items.Count);
                Assert.Contains(Descendants<TextBlock>(instances), text => text.Text == "QDC507");
                Assert.Contains(Descendants<TextBlock>(instances), text => text.Text == "EC20-CE");
                Assert.Contains(Descendants<TextBlock>(instances), text => text.Text.StartsWith("中国 · "));
                Assert.Contains(Descendants<TextBlock>(instances), text => text.Text.StartsWith("菲律宾 · "));
                instances.SelectedItem = service.GetModuleInstance(secondModule.Id);
                modulesPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.Same(secondModule, model.SelectedSlot);
                Assert.Same(service.GetModuleInstance(secondModule.Id), model.SelectedModuleInstance);
                var modulesBitmap = new RenderTargetBitmap(1200, 900, 96, 96, PixelFormats.Pbgra32); modulesBitmap.Render(modulesPage);
                var modulesEncoder = new PngBitmapEncoder(); modulesEncoder.Frames.Add(BitmapFrame.Create(modulesBitmap));
                using (var output = File.Create(Path.Combine(artifacts, "multi-module-instances.png"))) modulesEncoder.Save(output);
                foreach (var moduleTheme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var moduleWidth in new[] { 800, 1200 })
                {
                    ApplicationThemeManager.Apply(moduleTheme); VoWin.App.ApplyBrandTheme(moduleTheme);
                    var dualPage = new SystemStatusPage(model) { Width = moduleWidth, Height = 900 };
                    dualPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                    dualPage.Measure(new Size(moduleWidth, 900)); dualPage.Arrange(new Rect(0, 0, moduleWidth, 900));
                    dualPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); dualPage.UpdateLayout();
                    var chooser = Descendants<ComboBox>(dualPage).Single(box => ReferenceEquals(box.ItemsSource, model.Slots));
                    var primary = Descendants<Button>(dualPage).Single(button => ReferenceEquals(button.Command, model.ExecutePrimaryActionCommand));
                    var chooserBounds = chooser.TransformToAncestor(dualPage).TransformBounds(new Rect(chooser.RenderSize));
                    var primaryBounds = primary.TransformToAncestor(dualPage).TransformBounds(new Rect(primary.RenderSize));
                    Assert.True(chooserBounds.Right < primaryBounds.Left, "Module identity selector must not overlap actions.");
                    var dualBitmap = new RenderTargetBitmap(moduleWidth, 900, 96, 96, PixelFormats.Pbgra32); dualBitmap.Render(dualPage);
                    var dualPixels = new byte[moduleWidth * 900 * 4];
                    dualBitmap.CopyPixels(dualPixels, moduleWidth * 4, 0);
                    Assert.True(Enumerable.Range(0, moduleWidth * 900).Any(pixel => dualPixels[pixel * 4 + 3] > 128),
                        $"Rendered module page was blank: visible={dualPage.IsVisible}; opacity={dualPage.Opacity}; size={dualPage.RenderSize}.");
                    var dualEncoder = new PngBitmapEncoder(); dualEncoder.Frames.Add(BitmapFrame.Create(dualBitmap));
                    using var dualOutput = File.Create(Path.Combine(artifacts, $"dual-module-{moduleWidth}-{moduleTheme}.png"));
                    dualEncoder.Save(dualOutput);
                }
                model.VoWifiPreferredEpdgs = "epdg.epc.mnc066.mcc515.pub.3gppnetwork.org\nbackup.example.test";
                model.VoWifiAdditionalPlmns = "51566, 20404";
                foreach (var epdgTheme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var epdgWidth in new[] { 800, 1200 })
                {
                    ApplicationThemeManager.Apply(epdgTheme); VoWin.App.ApplyBrandTheme(epdgTheme);
                    var epdgPage = new SystemStatusPage(model) { Width = epdgWidth, Height = 900 };
                    epdgPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                    var expander = (Expander)epdgPage.FindName("VoWifiDiscoveryExpander");
                    expander.IsExpanded = true;
                    epdgPage.Measure(new Size(epdgWidth, 900)); epdgPage.Arrange(new Rect(0, 0, epdgWidth, 900));
                    epdgPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); epdgPage.UpdateLayout();
                    var viewer = Descendants<ScrollViewer>(epdgPage).First(view => view.Content is StackPanel && view.ScrollableHeight > 0);
                    viewer.ScrollToVerticalOffset(expander.TransformToAncestor(epdgPage).Transform(new Point(0, 0)).Y - 95);
                    epdgPage.UpdateLayout();
                    var hosts = Descendants<TextBox>(expander).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "优先尝试的 ePDG 地址");
                    Assert.True(hosts.ActualWidth > 250 && hosts.ActualWidth < epdgWidth);
                    Assert.True(hosts.AcceptsReturn);
                    var label = Descendants<TextBlock>(expander).Single(text => text.Text == "优先尝试的 ePDG（可选）");
                    Assert.Equal(((SolidColorBrush)application.Resources["AppTextPrimaryBrush"]).Color,
                        ((SolidColorBrush)label.Foreground).Color);
                    Assert.Single(Descendants<Button>(expander), button => button.Command == model.SaveVoWifiDiscoveryCommand);
                    Assert.Single(Descendants<Button>(expander), button => button.Command == model.ResetVoWifiDiscoveryCommand);
                    Assert.Single(Descendants<Button>(expander), button => button.Command == model.GenerateVoWifiEpdgCommand);
                    var generated = Descendants<TextBlock>(expander).Single(text => System.Windows.Automation.AutomationProperties.GetName(text) == "PLMN 生成的 ePDG 地址");
                    Assert.Contains("epdg.epc.mnc004.mcc204.pub.3gppnetwork.org", generated.Text);
                    var current = Descendants<TextBlock>(epdgPage).Single(text => System.Windows.Automation.AutomationProperties.GetName(text) == "当前 ePDG 地址");
                    Assert.Equal(model.VoWifiCurrentEpdg, current.Text);
                    Assert.Equal(model.VoWifiNetworkStack, Descendants<TextBlock>(epdgPage).Single(text =>
                        System.Windows.Automation.AutomationProperties.GetName(text) == "VoWiFi 网络协议栈").Text);
                    var image = new RenderTargetBitmap(epdgWidth, 900, 96, 96, PixelFormats.Pbgra32); image.Render(epdgPage);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var output = File.Create(Path.Combine(artifacts, $"epdg-discovery-{epdgWidth}-{epdgTheme}.png"))) encoder.Save(output);
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + current.TransformToAncestor(epdgPage).Transform(new Point(0, 0)).Y - 110);
                    epdgPage.UpdateLayout();
                    var overview = new RenderTargetBitmap(epdgWidth, 900, 96, 96, PixelFormats.Pbgra32); overview.Render(epdgPage);
                    var overviewEncoder = new PngBitmapEncoder(); overviewEncoder.Frames.Add(BitmapFrame.Create(overview));
                    using (var output = File.Create(Path.Combine(artifacts, $"epdg-current-{epdgWidth}-{epdgTheme}.png"))) overviewEncoder.Save(output);
                }
                typeof(VoSharp.Kernel.Pool.ModemSlot).GetProperty(nameof(firstModule.Sim))!.SetValue(firstModule, null);
                typeof(VoSharp.Kernel.Pool.ModemSlot).GetProperty(nameof(secondModule.Sim))!.SetValue(secondModule, null);
                // Shared gateway presenter: real bindings, synthetic cached observations,
                // no modem driver, registration, phone call or socket is created.
                var gatewayProxy = System.Reflection.DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
                var gatewayInput = new GatewayStatusInput
                {
                    SlotId = firstModule.Id, Title = "SIM A · Host IMS", Port = "COM96", IsOnline = true, HasSim = true,
                    CellularEnabled = true, CellularMode = TelephonyMode.HostIms, PreferredRoute = TelephonyMode.HostIms,
                    HasQmiEndpoint = true, QmiPreferred = true,
                    HostIms = new(true, "registered", "test-pcscf", null, null, null, SmsReceiveEnabled: true),
                    Bearer = new(true, new(42, System.Net.IPAddress.Parse("2001:db8::2"),
                        [System.Net.IPAddress.Parse("2001:db8::3")]), null, 14, DateTime.UtcNow, 2),
                    WifiState = VoSharp.Telephony.VoWifi.VoWifiState.ResolvingEpdg,
                    Registration = new(VoSharp.Modem.At.NetworkRegStatus.Home, "Test", "LTE", null)
                };
                ((PhoneCallServiceProxy)gatewayProxy).Handler = (method, arguments) => method.Name == nameof(IVoKernelService.GetGatewayStatus)
                    ? GatewayStatusSnapshot.Create((string?)arguments![0] == firstModule.Id ? gatewayInput : new())
                    : throw new NotSupportedException(method.Name);
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var width in new[] { 800, 1200 })
                foreach (var dense in new[] { false, true })
                {
                    ApplicationThemeManager.Apply(theme); VoWin.App.ApplyBrandTheme(theme);
                    var statusPanel = new GatewayStatusPanel { Service = gatewayProxy, Slot = firstModule, IsDense = dense, ShowSms = true };
                    var host = new Border { Padding = new Thickness(24), Child = statusPanel, Width = width };
                    host.SetResourceReference(Border.BackgroundProperty, "AppSurfaceBrush");
                    host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                    host.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); host.UpdateLayout();
                    Assert.Equal("已注册", statusPanel.Snapshot.Cellular.Value);
                    Assert.Equal("发现 ePDG", statusPanel.Snapshot.Wifi.Value);
                    Assert.Equal(6, Descendants<ItemsControl>(statusPanel).Single(list =>
                        System.Windows.Automation.AutomationProperties.GetName(list) == "通信分层状态").Items.Count);
                    Assert.Equal("PPP 已建立", statusPanel.Snapshot.Bearer.Value);
                    var layerDetails = (Expander)statusPanel.FindName("LayerDetails");
                    Assert.Equal(Visibility.Visible, layerDetails.Visibility);
                    Assert.False(layerDetails.IsExpanded);
                    Assert.Equal(Visibility.Collapsed, ((ContentPresenter)layerDetails.Template.FindName("DetailsContent", layerDetails)).Visibility);
                    var indicators = Descendants<StatusIndicator>(statusPanel).Where(indicator => indicator.ActualHeight > 0).ToArray();
                    Assert.Equal(6, indicators.Length);
                    foreach (var indicator in indicators)
                    {
                        var item = Assert.IsType<GatewayStatusItem>(indicator.Item);
                        var brushKey = item.Light switch
                        {
                            StatusLight.Ready => "AppSuccessBrush", StatusLight.Busy => "AppWarningBrush",
                            StatusLight.Error => "AppDangerBrush", _ => "AppTextTertiaryBrush"
                        };
                        var lamp = (System.Windows.Shapes.Ellipse)indicator.FindName("Lamp");
                        Assert.Equal(((SolidColorBrush)host.FindResource(brushKey)).Color, ((SolidColorBrush)lamp.Fill).Color);
                        Assert.Contains(item.Value, System.Windows.Automation.AutomationProperties.GetName(indicator));
                        Assert.Equal(item.Light == StatusLight.Ready ? Visibility.Collapsed : Visibility.Visible,
                            ((TextBlock)indicator.FindName("StateText")).Visibility);
                    }
                    foreach (var block in Descendants<TextBlock>(statusPanel).Where(block => block.IsVisible))
                    {
                        var bounds = block.TransformToAncestor(host).TransformBounds(new Rect(block.RenderSize));
                        Assert.True(bounds.Right <= width && bounds.Left >= 0, "Status text must wrap within its presenter.");
                    }
                    var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(Path.Combine(artifacts, $"gateway-status-{width}-{theme}-{dense}.png"))) encoder.Save(output);
                    if (!dense)
                    {
                        layerDetails.IsExpanded = true;
                        host.Measure(new Size(width, double.PositiveInfinity));
                        host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                        host.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); host.UpdateLayout();
                        Assert.Contains(Descendants<TextBlock>(layerDetails), block => block.Text.Contains("续期 14 次"));
                        Assert.Equal(Visibility.Visible, ((ContentPresenter)layerDetails.Template.FindName("DetailsContent", layerDetails)).Visibility);
                        foreach (var block in Descendants<TextBlock>(layerDetails).Where(block => block.IsVisible))
                        {
                            var bounds = block.TransformToAncestor(host).TransformBounds(new Rect(block.RenderSize));
                            Assert.True(bounds.Right <= width && bounds.Left >= 0, "Expanded details must wrap within the page.");
                        }
                        var detailsImage = new RenderTargetBitmap(width, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        detailsImage.Render(host);
                        var detailsEncoder = new PngBitmapEncoder(); detailsEncoder.Frames.Add(BitmapFrame.Create(detailsImage));
                        using (var output = File.Create(Path.Combine(artifacts, $"gateway-details-{width}-{theme}.png"))) detailsEncoder.Save(output);
                    }
                    statusPanel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var timer = (DispatcherTimer)typeof(GatewayStatusPanel).GetField("_timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(statusPanel)!;
                    Assert.True(timer.IsEnabled);
                    statusPanel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Assert.False(timer.IsEnabled);
                    statusPanel.Slot = secondModule;
                    Assert.Equal("未选择设备", statusPanel.Snapshot.Device.Value); // Never reuse A's registration on B.
                    statusPanel.Slot = null; Assert.Equal("未选择设备", statusPanel.Snapshot.Device.Value);
                }
                var originalGatewayInput = gatewayInput;
                foreach (var scenario in new[]
                {
                    (Name: "reconnecting", Input: gatewayInput with
                    {
                        WifiState = VoSharp.Telephony.VoWifi.VoWifiState.Failed,
                        WifiKeepOnlineRequested = true, WifiError = "Test IKE timeout"
                    }, Expected: "等待自动重连"),
                    (Name: "authorization-paused", Input: gatewayInput with
                    {
                        WifiState = VoSharp.Telephony.VoWifi.VoWifiState.Failed,
                        WifiKeepOnlineRequested = true, WifiRecoveryPaused = true, WifiError = "AUTHORIZATION_REJECTED"
                    }, Expected: "自动重连已暂停"),
                    (Name: "source-missing", Input: gatewayInput with
                    {
                        IsOnline = false, WifiState = VoSharp.Telephony.VoWifi.VoWifiState.Disconnected,
                        WifiKeepOnlineRequested = true
                    }, Expected: "设备断开 · 等待恢复")
                })
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                {
                    gatewayInput = scenario.Input;
                    ApplicationThemeManager.Apply(theme); VoWin.App.ApplyBrandTheme(theme);
                    var panel = new GatewayStatusPanel { Service = gatewayProxy, Slot = firstModule, IsDense = true, ShowSms = true };
                    var host = new Border { Padding = new Thickness(24), Child = panel, Width = 800 };
                    host.SetResourceReference(Border.BackgroundProperty, "AppSurfaceBrush");
                    host.Measure(new Size(800, double.PositiveInfinity)); host.Arrange(new Rect(0, 0, 800, host.DesiredSize.Height));
                    host.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); host.UpdateLayout();
                    Assert.Equal(scenario.Expected, panel.Snapshot.Wifi.Value);
                    foreach (var block in Descendants<TextBlock>(panel).Where(block => block.IsVisible))
                    {
                        var bounds = block.TransformToAncestor(host).TransformBounds(new Rect(block.RenderSize));
                        Assert.True(bounds.Right <= 800 && bounds.Left >= 0, "Recovery state must fit the narrow header.");
                    }
                    var image = new RenderTargetBitmap(800, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    image.Render(host);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(artifacts, $"gateway-{scenario.Name}-{theme}.png")); encoder.Save(output);
                }
                gatewayInput = originalGatewayInput;
                var modemModel = new ModemManagerViewModel(service) { SelectedSlot = firstModule };
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var width in new[] { 800, 1200 })
                {
                    ApplicationThemeManager.Apply(theme); VoWin.App.ApplyBrandTheme(theme);
                    var modemPage = new ModemManagerPage(modemModel) { Width = width, Height = 900 };
                    modemPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                    modemPage.Measure(new Size(width, 900)); modemPage.Arrange(new Rect(0, 0, width, 900));
                    modemPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); modemPage.UpdateLayout();
                    var panel = (GatewayStatusPanel)modemPage.FindName("GatewayStatus");
                    Assert.NotNull(panel); Assert.Same(firstModule, panel.Slot);
                    Assert.Equal("设备已断开", panel.Snapshot.Device.Value);
                    var headerPanel = (GatewayStatusPanel)modemPage.FindName("HeaderGatewayStatus");
                    Assert.NotNull(headerPanel); Assert.Same(firstModule, headerPanel.Slot);
                    Assert.True(headerPanel.IsCompact);
                    Assert.Contains("默认呼出", headerPanel.Snapshot.ListSummary);
                    Assert.Contains("VoWiFi", headerPanel.Snapshot.ListSummary);
                    Assert.Contains(Descendants<TabItem>(modemPage), tab => Equals(tab.Header, "VoWiFi 诊断"));
                    var image = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32); image.Render(modemPage);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var output = File.Create(Path.Combine(artifacts, $"gateway-modem-{width}-{theme}.png"))) encoder.Save(output);
                    var deviceTabs = Descendants<TabControl>(modemPage).Single();
                    for (var tabIndex = 0; tabIndex < deviceTabs.Items.Count; tabIndex++)
                    {
                        deviceTabs.SelectedIndex = tabIndex;
                        modemPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); modemPage.UpdateLayout();
                        Assert.True(modemPage.IsAncestorOf(headerPanel));
                        Assert.True(headerPanel.ActualHeight > 0);
                        Assert.Same(firstModule, headerPanel.Slot);
                    }
                    deviceTabs.SelectedIndex = 0;
                }
                var proxyModel = new ProxyViewModel(service);
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var width in new[] { 800, 1200 })
                {
                    ApplicationThemeManager.Apply(theme); VoWin.App.ApplyBrandTheme(theme);
                    var proxyPage = new ProxyPage(proxyModel) { Width = width, Height = 900 };
                    proxyPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                    proxyPage.Measure(new Size(width, 900)); proxyPage.Arrange(new Rect(0, 0, width, 900));
                    proxyPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); proxyPage.UpdateLayout();
                    var scopeHint = Descendants<TextBlock>(proxyPage).Single(text => text.Text.StartsWith("Host IMS 使用模组蜂窝承载"));
                    var hintBounds = scopeHint.TransformToAncestor(proxyPage).TransformBounds(new Rect(scopeHint.RenderSize));
                    Assert.True(hintBounds.Right <= width - 24 && hintBounds.Left >= 24);
                    var image = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32); image.Render(proxyPage);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(artifacts, $"gateway-proxy-{width}-{theme}.png")); encoder.Save(output);
                }
                // A failed floating-window hangup must remain visible/retryable.
                // Construct controls only; never show a native window or place a call.
                var floating = new VoWin.Views.Windows.InCallFloatingWindow();
                Assert.False(floating.ShowActivated);
                var floatingType = floating.GetType();
                var floatingPrivate = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var muteCalls = new List<bool>();
                floatingType.GetField("_onMuteChanged", floatingPrivate)!.SetValue(floating,
                    (Func<bool, bool>)(muted => { muteCalls.Add(muted); return true; }));
                floatingType.GetMethod("BtnMute_Click", floatingPrivate)!.Invoke(floating, [floating, new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)]);
                Assert.Equal([true], muteCalls);
                Assert.True((bool)floatingType.GetField("_isMuted", floatingPrivate)!.GetValue(floating)!);
                floatingType.GetField("_onMuteChanged", floatingPrivate)!.SetValue(floating, (Func<bool, bool>)(_ => false));
                floatingType.GetMethod("BtnMute_Click", floatingPrivate)!.Invoke(floating, [floating, new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)]);
                Assert.True((bool)floatingType.GetField("_isMuted", floatingPrivate)!.GetValue(floating)!);
                typeof(VoWin.Views.Windows.InCallFloatingWindow).GetField("_onHangup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(floating, (Func<Task>)(() => Task.FromException(new IOException("test hangup failure"))));
                typeof(VoWin.Views.Windows.InCallFloatingWindow).GetMethod("BtnHangup_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(floating, [floating, new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)]);
                Assert.Equal("挂断失败", ((TextBlock)floating.FindName("TxtDuration")).Text);
                Assert.Equal(true, ((Button)floating.FindName("BtnHangup")).ReadLocalValue(UIElement.IsEnabledProperty));
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                {
                    ApplicationThemeManager.Apply(theme); VoWin.App.ApplyBrandTheme(theme);
                    var content = (FrameworkElement)floating.Content;
                    content.Measure(new Size(340, 76)); content.Arrange(new Rect(0, 0, 340, 76));
                    content.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); content.UpdateLayout();
                    var hangupBounds = ((Button)floating.FindName("BtnHangup")).TransformToAncestor(content)
                        .TransformBounds(new Rect(((Button)floating.FindName("BtnHangup")).RenderSize));
                    Assert.True(hangupBounds.Right <= 324 && hangupBounds.Bottom <= 60, "Floating controls must fit inside the window margins.");
                    var image = new RenderTargetBitmap(340, 76, 96, 96, PixelFormats.Pbgra32); image.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = File.Create(Path.Combine(artifacts, $"call-floating-{theme}.png")); encoder.Save(stream);
                }
                floating.Close();
                // Incoming action failures stay visible/retryable. A previous
                // module's delayed failure cannot overwrite a newer popup.
                var incomingWindow = new VoWin.Views.Windows.IncomingCallFloatingWindow();
                var incomingType = incomingWindow.GetType();
                var privateInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var actionMethod = incomingType.GetMethod("PerformActionAsync", privateInstance)!;
                incomingType.GetField("_onAnswer", privateInstance)!.SetValue(incomingWindow,
                    (Func<Task>)(() => Task.FromException(new IOException("test answer failure"))));
                ((Task)actionMethod.Invoke(incomingWindow, [true])!).GetAwaiter().GetResult();
                Assert.Contains("接听失败", ((TextBlock)incomingWindow.FindName("TxtCallSubtext")).Text);
                Assert.True(((Button)incomingWindow.FindName("BtnAnswer")).IsEnabled);
                var oldReject = new TaskCompletionSource(); var newAnswer = new TaskCompletionSource();
                incomingType.GetField("_onReject", privateInstance)!.SetValue(incomingWindow, (Func<Task>)(() => oldReject.Task));
                var oldAction = (Task)actionMethod.Invoke(incomingWindow, [false])!;
                incomingType.GetField("_presentationVersion", privateInstance)!.SetValue(incomingWindow, 1L);
                incomingType.GetField("_onAnswer", privateInstance)!.SetValue(incomingWindow, (Func<Task>)(() => newAnswer.Task));
                var newAction = (Task)actionMethod.Invoke(incomingWindow, [true])!;
                oldReject.SetException(new IOException("old module failure"));
                ((Dispatcher)incomingWindow.Dispatcher).Invoke(() => { }, DispatcherPriority.ContextIdle);
                oldAction.GetAwaiter().GetResult();
                Assert.Equal("正在接听…", ((TextBlock)incomingWindow.FindName("TxtCallSubtext")).Text);
                Assert.False(((Button)incomingWindow.FindName("BtnAnswer")).IsEnabled);
                newAnswer.SetException(new IOException("new module failure"));
                incomingWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                newAction.GetAwaiter().GetResult();
                Assert.Contains("接听失败", ((TextBlock)incomingWindow.FindName("TxtCallSubtext")).Text);
                Assert.True(((Button)incomingWindow.FindName("BtnAnswer")).IsEnabled);
                incomingWindow.Close();
                // Ordinary SMS and OTP notifications reuse the same window,
                // without stale verification-code controls or focus stealing.
                var smsWindow = new VoWin.Views.Windows.IncomingSmsFloatingWindow();
                Assert.False(smsWindow.ShowActivated);
                smsWindow.PresentMessage(new() { SenderOrRecipient = "10086", Text = "Your OTP code is 123456" }, "SIM A");
                Assert.Equal(Visibility.Visible, ((StackPanel)smsWindow.FindName("PanelOtpDisplay")).Visibility);
                Assert.Equal("123456", ((TextBlock)smsWindow.FindName("TxtOtpCode")).Text);
                smsWindow.PresentMessage(new() { SenderOrRecipient = "10010", Text = "A normal incoming message" }, "SIM B");
                Assert.Equal(Visibility.Collapsed, ((StackPanel)smsWindow.FindName("PanelOtpDisplay")).Visibility);
                Assert.Equal(string.Empty, ((TextBlock)smsWindow.FindName("TxtOtpCode")).Text);
                Assert.Equal("SIM B", ((TextBlock)smsWindow.FindName("TxtSlotBadge")).Text);
                Assert.Equal("复制短信内容", ((TextBlock)smsWindow.FindName("TxtBtnText")).Text);
                Assert.Equal("A normal incoming message", ((TextBlock)smsWindow.FindName("TxtMessageBody")).Text);
                smsWindow.Close();
                var smsConversation = new VoWin.Models.SmsConversationModel { ContactNumber = "10086" };
                var outgoingSms = new VoWin.Models.SmsMessageModel
                {
                    Text = "Offline SMS layout test", IsOutgoing = true,
                    DeliveryState = VoWin.Models.SmsDeliveryState.Sending, SlotId = firstModule.Id
                };
                smsConversation.Messages.Add(outgoingSms);
                service.Conversations.Add(smsConversation);
                var messagesModel = new MessagesViewModel(service)
                {
                    SelectedConversation = smsConversation, SelectedSlot = firstModule,
                    StatusMessage = "发送失败: offline route rejected. Please check the selected module."
                };
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var width in new[] { 800, 1200 })
                {
                    ApplicationThemeManager.Apply(theme); VoWin.App.ApplyBrandTheme(theme);
                    outgoingSms.DeliveryState = VoWin.Models.SmsDeliveryState.Sending;
                    var messagesPage = new MessagesPage(messagesModel) { Width = width, Height = 900 };
                    messagesPage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                    messagesPage.Measure(new Size(width, 900)); messagesPage.Arrange(new Rect(0, 0, width, 900));
                    messagesPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); messagesPage.UpdateLayout();
                    var status = (TextBlock)messagesPage.FindName("SmsSubmissionStatus");
                    Assert.Equal(Visibility.Visible, status.Visibility);
                    Assert.Equal(messagesModel.StatusMessage, status.Text);
                    Assert.Equal(TextWrapping.Wrap, status.TextWrapping);
                    var smsSelector = (ComboBox)messagesPage.FindName("SmsSlotSelector");
                    var smsCount = (Border)messagesPage.FindName("SmsCharacterCount");
                    var selectorBounds = smsSelector.TransformToAncestor(messagesPage).TransformBounds(new Rect(smsSelector.RenderSize));
                    var countBounds = smsCount.TransformToAncestor(messagesPage).TransformBounds(new Rect(smsCount.RenderSize));
                    Assert.True(selectorBounds.Right + 4 <= countBounds.Left, "SIM selector must not overlap the character count.");
                    Assert.Contains(Descendants<TextBlock>(messagesPage).SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Run>()),
                        run => run.Text == "发送中...");
                    outgoingSms.DeliveryState = VoWin.Models.SmsDeliveryState.Failed;
                    messagesPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); messagesPage.UpdateLayout();
                    Assert.Contains(Descendants<TextBlock>(messagesPage).SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Run>()),
                        run => run.Text == "发送失败");
                    var smsBitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32); smsBitmap.Render(messagesPage);
                    var smsEncoder = new PngBitmapEncoder(); smsEncoder.Frames.Add(BitmapFrame.Create(smsBitmap));
                    using (var output = File.Create(Path.Combine(artifacts, $"sms-submission-{width}-{theme}.png"))) smsEncoder.Save(output);
                    messagesModel.StatusMessage = string.Empty;
                    messagesPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    Assert.Equal(Visibility.Collapsed, status.Visibility);
                    messagesModel.StatusMessage = "发送失败: offline route rejected. Please check the selected module.";
                }
                typeof(VoKernelService).GetProperty(nameof(VoKernelService.CurrentCallState))!.SetValue(service, VoSharp.Telephony.Calls.CallState.Active);
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var width in new[] { 800, 1200 })
                {
                    ApplicationThemeManager.Apply(theme); VoWin.App.ApplyBrandTheme(theme);
                    var phoneModel = new PhoneViewModel(service)
                    {
                        MicrophoneStatus = new()
                        {
                            CallId = "offline-layout-call", DeviceName = "Realtek · offline layout", IsCapturing = true,
                            CapturedBuffers = 50, ForwardedBuffers = 50, EncodedFrames = 50, AudioPacketsSent = 50,
                            InputPeak = 2000
                        }
                    };
                    var phonePage = new PhonePage(phoneModel) { Width = width, Height = 900 };
                    phonePage.SetResourceReference(Control.BackgroundProperty, "AppSurfaceBrush");
                    phonePage.Measure(new Size(width, 900)); phonePage.Arrange(new Rect(0, 0, width, 900));
                    phonePage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); phonePage.UpdateLayout();
                    var micPanel = (Border)phonePage.FindName("MicrophoneUplinkPanel");
                    Assert.Equal(Visibility.Visible, micPanel.Visibility);
                    Assert.True(micPanel.ActualWidth >= 280);
                    var inputLevel = (ProgressBar)phonePage.FindName("MicrophoneInputLevel");
                    Assert.True(inputLevel.Value > 0);
                    var micText = (TextBlock)phonePage.FindName("MicrophoneUplinkStatusText");
                    Assert.Contains("已发出", micText.Text);
                    phoneModel.MicrophoneStatus = phoneModel.MicrophoneStatus with { IsMessagePlayback = true, DeviceName = "自动应答录音 · 直接发送到对端" };
                    phonePage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); phonePage.UpdateLayout();
                    Assert.Contains("自动应答录音", micText.Text);
                    Assert.True(Descendants<Button>(phonePage).Single(button => Equals(button.Content, "切换到麦克风")).IsEnabled);
                    phoneModel.MicrophoneStatus = phoneModel.MicrophoneStatus with { IsMessagePlayback = false };
                    phoneModel.MicrophoneStatus = phoneModel.MicrophoneStatus with { IsCapturing = false, Error = "offline stopped" };
                    phonePage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); phonePage.UpdateLayout();
                    var micRetry = Descendants<Button>(phonePage).Single(button => Equals(button.Content, "重新打开麦克风"));
                    Assert.True(micRetry.IsEnabled); Assert.Contains("异常", micText.Text);
                    var phoneBitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32); phoneBitmap.Render(phonePage);
                    var phoneEncoder = new PngBitmapEncoder(); phoneEncoder.Frames.Add(BitmapFrame.Create(phoneBitmap));
                    using (var output = File.Create(Path.Combine(artifacts, $"phone-microphone-{width}-{theme}.png"))) phoneEncoder.Save(output);
                    var panelBounds = micPanel.TransformToAncestor(phonePage).TransformBounds(new Rect(micPanel.RenderSize));
                    Assert.True(panelBounds.Bottom < 780, "Microphone panel must leave room for call controls.");
                    // At minimum-height windows, details may scroll; hangup must never be clipped or covered.
                    phonePage.Height = 680;
                    phonePage.Measure(new Size(width, 680)); phonePage.Arrange(new Rect(0, 0, width, 680)); phonePage.UpdateLayout();
                    var hangupButton = (Button)phonePage.FindName("HangupButton");
                    var hangupBounds = hangupButton.TransformToAncestor(phonePage).TransformBounds(new Rect(hangupButton.RenderSize));
                    Assert.True(hangupBounds.Bottom < 680 && hangupBounds.Top >= 0);
                    Assert.Equal(48, hangupButton.ActualHeight);
                    var shortBitmap = new RenderTargetBitmap(width, 680, 96, 96, PixelFormats.Pbgra32); shortBitmap.Render(phonePage);
                    var shortEncoder = new PngBitmapEncoder(); shortEncoder.Frames.Add(BitmapFrame.Create(shortBitmap));
                    using (var output = File.Create(Path.Combine(artifacts, $"phone-gateway-short-{width}-{theme}.png"))) shortEncoder.Save(output);
                    phoneModel.MicrophoneStatus = new();
                    phonePage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    Assert.Equal(Visibility.Collapsed, micPanel.Visibility);
                    typeof(VoKernelService).GetProperty(nameof(VoKernelService.CurrentCallState))!
                        .SetValue(service, VoSharp.Telephony.Calls.CallState.Incoming);
                    typeof(VoKernelService).GetProperty(nameof(VoKernelService.HasIncomingCall))!.SetValue(service, true);
                    typeof(PhoneViewModel).GetMethod("NotifyCallStateVisuals", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(phoneModel, null);
                    phonePage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); phonePage.UpdateLayout();
                    var answerButton = (Button)phonePage.FindName("AnswerIncomingButton");
                    Assert.Equal(Visibility.Visible, ((StackPanel)answerButton.Parent).Visibility);
                    Assert.True(answerButton.IsEnabled);
                    Assert.Equal(Visibility.Collapsed, hangupButton.Visibility);
                    var answerBounds = answerButton.TransformToAncestor(phonePage).TransformBounds(new Rect(answerButton.RenderSize));
                    Assert.True(answerBounds.Bottom < 680 && answerBounds.Top >= 0);
                    Assert.Equal(48, answerButton.ActualHeight);
                    var callTitle = (TextBlock)phonePage.FindName("CallStatusTitleText");
                    Assert.Equal("来电请求", callTitle.Text);
                    var informationScroll = (ScrollViewer)phonePage.FindName("CallInformationScroll");
                    var titleBounds = callTitle.TransformToAncestor(informationScroll).TransformBounds(new Rect(callTitle.RenderSize));
                    Assert.True(titleBounds.Top >= 0 && titleBounds.Bottom <= informationScroll.ViewportHeight,
                        "Incoming caller/status must remain visible without scrolling at the minimum height.");
                    var incomingBitmap = new RenderTargetBitmap(width, 680, 96, 96, PixelFormats.Pbgra32); incomingBitmap.Render(phonePage);
                    var incomingEncoder = new PngBitmapEncoder(); incomingEncoder.Frames.Add(BitmapFrame.Create(incomingBitmap));
                    using (var output = File.Create(Path.Combine(artifacts, $"phone-incoming-group-{width}-{theme}.png"))) incomingEncoder.Save(output);
                    typeof(VoKernelService).GetProperty(nameof(VoKernelService.HasIncomingCall))!.SetValue(service, false);
                    typeof(VoKernelService).GetProperty(nameof(VoKernelService.CurrentCallState))!
                        .SetValue(service, VoSharp.Telephony.Calls.CallState.Active);
                }
                typeof(VoKernelService).GetProperty(nameof(VoKernelService.CurrentCallState))!.SetValue(service, VoSharp.Telephony.Calls.CallState.Idle);
                // New pages are rendered in this same STA/Application: WPF permits one Application per AppDomain.
                var remoteProxy = System.Reflection.DispatchProxy.Create<IRemoteControlService, PhoneCallServiceProxy>();
                ((PhoneCallServiceProxy)remoteProxy).Handler = (method, _) => method.Name switch
                {
                    "get_Settings" => new RemoteControlSettings(),
                    "get_WeixinStatus" or "get_QqStatus" or "get_TelegramStatus" or "get_BarkStatus" => "未启用",
                    "get_WeixinIndicator" => new GatewayStatusItem("微信", "未启用", "未配置"),
                    "get_QqIndicator" => new GatewayStatusItem("QQ", "未启用", "未配置"),
                    "get_TelegramIndicator" => new GatewayStatusItem("Telegram", "未启用", "未配置"),
                    "get_BarkIndicator" => new GatewayStatusItem("Bark", "未启用", "仅推送"),
                    "get_LastActivity" => "尚无活动", "get_PairingCodeStatus" => "未开启配对",
                    "add_StatusChanged" or "remove_StatusChanged" => null,
                    _ => throw new NotSupportedException(method.Name)
                };
                var remoteModel = new RemoteControlViewModel(remoteProxy);
                remoteModel.OnNavigatedToAsync().GetAwaiter().GetResult();
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var width in new[] { 800, 1200 })
                {
                    ApplicationThemeManager.Apply(theme); VoWin.App.ApplyBrandTheme(theme);
                    var smsApiPage = new SettingsPage(settingsModel) { Width = width, Height = 900 };
                    smsApiPage.Measure(new Size(width, 900)); smsApiPage.Arrange(new Rect(0, 0, width, 900));
                    smsApiPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); smsApiPage.UpdateLayout();
                    var apiCard = (Border)smsApiPage.FindName("SmsApiCard"); apiCard.BringIntoView();
                    smsApiPage.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); smsApiPage.UpdateLayout();
                    var apiScroll = (ScrollViewer)smsApiPage.FindName("SettingsScrollViewer");
                    var apiBounds = apiCard.TransformToAncestor(smsApiPage).TransformBounds(new Rect(apiCard.RenderSize));
                    apiScroll.ScrollToVerticalOffset(apiScroll.VerticalOffset + apiBounds.Top - 90);
                    RenderExtra(smsApiPage, $"sms-api-{width}-{theme}", 1);
                    Assert.True(Descendants<Wpf.Ui.Controls.Button>(apiCard).Single(button => Equals(button.Content, "保存并应用 API")).IsEnabled);
                    Assert.DoesNotContain(Descendants<Expander>(apiCard), expander => expander.IsExpanded);
                    var controlPage = new RemoteControlPage(remoteModel) { Width = width, Height = 900 };
                    var channelTabs = (TabControl)controlPage.FindName("ChannelTabs");
                    Assert.Equal(4, channelTabs.Items.Count);
                    for (var tab = 0; tab < 4; tab++)
                    {
                        channelTabs.SelectedIndex = tab;
                        RenderExtra(controlPage, $"control-alerts-{width}-{theme}-{tab}", 1);
                    }
                    foreach (var dpi in new[] { 1.25, 1.5 })
                    {
                        var proxyPage = new ProxyPage(new ProxyViewModel(service)) { Width = width, Height = 900 };
                        var aboutPage = new AboutPage(new AboutViewModel()) { Width = width, Height = 900 };
                        foreach (var page in new Page[] { proxyPage, aboutPage })
                        {
                            Assert.True(page.UseLayoutRounding); Assert.True(page.SnapsToDevicePixels);
                            Assert.IsType<SolidColorBrush>(page.Background);
                            Assert.Equal(255, ((SolidColorBrush)page.Background).Color.A);
                            RenderExtra(page, $"crisp-{page.GetType().Name}-{width}-{theme}", dpi);
                            Assert.DoesNotContain(Descendants<Border>(page), border => border.Effect != null && Descendants<TextBlock>(border).Any());
                        }
                    }
                }

                void RenderExtra(Page page, string name, double dpi)
                {
                    page.Measure(new Size(page.Width, page.Height)); page.Arrange(new Rect(0, 0, page.Width, page.Height));
                    page.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); page.UpdateLayout();
                    var image = new RenderTargetBitmap((int)(page.Width * dpi), (int)(page.Height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                    image.Render(page);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(artifacts, name + $"-{(int)(dpi * 100)}.png")); encoder.Save(file);
                }
                bindingListener.Flush();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
                Assert.Equal(string.Empty, bindingErrors.ToString());
                application.Shutdown();
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // The full software-rendered matrix now includes gateway, SMS and ePDG
        // previews. Keep a finite deadline without failing slower desktops at
        // the former 45-second limit; every layout/binding assertion still runs.
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(150));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
