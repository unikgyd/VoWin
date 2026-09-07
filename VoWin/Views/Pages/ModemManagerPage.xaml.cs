using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages
{
    public partial class ModemManagerPage : INavigableView<ModemManagerViewModel>
    {
        public ModemManagerViewModel ViewModel { get; }

        public ModemManagerPage(ModemManagerViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
        }
    }
}
