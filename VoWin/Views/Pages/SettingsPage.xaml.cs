using System.Windows.Controls;
using System.Windows.Input;
using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages
{
    public partial class SettingsPage : INavigableView<SettingsViewModel>
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsPage(SettingsViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        private void OnPagePreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (SettingsScrollViewer is ScrollViewer scv)
            {
                scv.ScrollToVerticalOffset(scv.VerticalOffset - (e.Delta / 3.0));
                e.Handled = true;
            }
        }
    }
}

