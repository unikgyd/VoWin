using System.Windows.Input;
using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages
{
    public partial class ProxyPage : INavigableView<ProxyViewModel>
    {
        public ProxyViewModel ViewModel { get; }

        public ProxyPage(ProxyViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
        }

        private void OnRuleItemMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel?.SelectedCountryRoute != null)
            {
                ViewModel.OpenEditDialogCommand.Execute(ViewModel.SelectedCountryRoute);
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Escape && ViewModel?.IsEditingRoute == true)
            {
                ViewModel.CloseEditDialogCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
