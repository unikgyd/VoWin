using System.Windows;
using System.Windows.Controls;
using VoWin.Models;

namespace VoWin.Views.Controls;

/// <summary>Shared light semantics; full observation remains available to assistive technology and details.</summary>
public partial class StatusIndicator : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item),
        typeof(GatewayStatusItem), typeof(StatusIndicator), new PropertyMetadata(null));
    public GatewayStatusItem? Item { get => (GatewayStatusItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public StatusIndicator() => InitializeComponent();
}
