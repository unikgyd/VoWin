using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VoSharp.Kernel.Pool;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Views.Controls;

/// <summary>One shared read-only presenter; its timer exists only while the visual is loaded.</summary>
public partial class GatewayStatusPanel : UserControl
{
    public static readonly DependencyProperty ServiceProperty = DependencyProperty.Register(nameof(Service),
        typeof(IVoKernelService), typeof(GatewayStatusPanel), new PropertyMetadata(null, OnScopeChanged));
    public static readonly DependencyProperty SlotProperty = DependencyProperty.Register(nameof(Slot),
        typeof(ModemSlot), typeof(GatewayStatusPanel), new PropertyMetadata(null, OnScopeChanged));
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact),
        typeof(bool), typeof(GatewayStatusPanel), new PropertyMetadata(false, OnScopeChanged));
    public static readonly DependencyProperty ShowSmsProperty = DependencyProperty.Register(nameof(ShowSms),
        typeof(bool), typeof(GatewayStatusPanel), new PropertyMetadata(false, OnScopeChanged));
    public static readonly DependencyProperty IsDenseProperty = DependencyProperty.Register(nameof(IsDense),
        typeof(bool), typeof(GatewayStatusPanel), new PropertyMetadata(false, OnScopeChanged));
    private static readonly DependencyPropertyKey SnapshotPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Snapshot),
        typeof(GatewayStatusSnapshot), typeof(GatewayStatusPanel), new PropertyMetadata(GatewayStatusSnapshot.Create(new())));
    public static readonly DependencyProperty SnapshotProperty = SnapshotPropertyKey.DependencyProperty;
    public IVoKernelService? Service { get => (IVoKernelService?)GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }
    public ModemSlot? Slot { get => (ModemSlot?)GetValue(SlotProperty); set => SetValue(SlotProperty, value); }
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public bool ShowSms { get => (bool)GetValue(ShowSmsProperty); set => SetValue(ShowSmsProperty, value); }
    public bool IsDense { get => (bool)GetValue(IsDenseProperty); set => SetValue(IsDenseProperty, value); }
    public GatewayStatusSnapshot Snapshot => (GatewayStatusSnapshot)GetValue(SnapshotProperty);
    private readonly DispatcherTimer _timer;

    public GatewayStatusPanel()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        Refresh();
    }

    private static void OnScopeChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
        => ((GatewayStatusPanel)source).Refresh();

    internal void Refresh()
    {
        if (Overview == null) return;
        var next = Service?.GetGatewayStatus(Slot?.Id) ?? GatewayStatusSnapshot.Create(new());
        if (Snapshot != next) SetValue(SnapshotPropertyKey, next);
        Overview.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactSummary.Visibility = IsCompact ? Visibility.Visible : Visibility.Collapsed;
        SmsHint.Visibility = ShowSms ? Visibility.Visible : Visibility.Collapsed;
        ScopeText.Visibility = IsDense ? Visibility.Collapsed : Visibility.Visible;
        RouteText.Visibility = IsDense ? Visibility.Collapsed : Visibility.Visible;
        LayerDetails.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
    }
}
