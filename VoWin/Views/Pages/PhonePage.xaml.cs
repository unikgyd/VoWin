using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoWin.Models;
using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages
{
    public partial class PhonePage : INavigableView<PhoneViewModel>
    {
        public PhoneViewModel ViewModel { get; }

        public PhonePage(PhoneViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
        }

        private void OnPhoneNumberKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (ViewModel.CanDial && ViewModel.DialCommand.CanExecute(null))
                {
                    ViewModel.DialCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private void OnMoreOptionsClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.IsOpen = true;
            }
        }

        private static CallRecordModel? GetCallRecord(object sender)
        {
            if (sender is MenuItem mi)
            {
                return mi.DataContext as CallRecordModel
                    ?? ((mi.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as CallRecordModel;
            }
            return null;
        }

        private void OnFillNumberClick(object sender, RoutedEventArgs e)
        {
            var record = GetCallRecord(sender);
            if (record != null)
            {
                ViewModel.FillNumberCommand.Execute(record);
            }
        }

        private void OnCopyNumberClick(object sender, RoutedEventArgs e)
        {
            var record = GetCallRecord(sender);
            if (record != null)
            {
                ViewModel.CopyNumberCommand.Execute(record);
            }
        }

        private void OnDeleteRecordClick(object sender, RoutedEventArgs e)
        {
            var record = GetCallRecord(sender);
            if (record != null)
            {
                ViewModel.DeleteRecordCommand.Execute(record);
            }
        }
    }
}
