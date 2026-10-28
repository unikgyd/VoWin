using System.Windows;

namespace VoWin.Views.Windows;

public partial class RuntimeSetupWindow : Window
{
    public RuntimeSetupWindow() => InitializeComponent();
    public void SetStatus(string message) => Status.Text = message;
}
