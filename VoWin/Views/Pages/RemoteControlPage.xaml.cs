using System.Windows.Controls;
using System.Windows.Input;
using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages;

public partial class RemoteControlPage : INavigableView<RemoteControlViewModel>
{
    public RemoteControlViewModel ViewModel { get; }

    public RemoteControlPage(RemoteControlViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        QqSecretBox.Password = viewModel.QqClientSecret;
    }

    private void OnQqSecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) ViewModel.QqClientSecret = box.Password;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (RemoteScrollViewer is ScrollViewer viewer)
        {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }
}
