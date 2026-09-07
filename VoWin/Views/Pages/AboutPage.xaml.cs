using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages
{
    public partial class AboutPage : INavigableView<AboutViewModel>
    {
        public AboutViewModel ViewModel { get; }

        public AboutPage(AboutViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
        }
    }
}
