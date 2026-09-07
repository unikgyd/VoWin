using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoWin.Models;
using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages
{
    public partial class MessagesPage : INavigableView<MessagesViewModel>
    {
        public MessagesViewModel ViewModel { get; }
        private INotifyCollectionChanged? _currentMessagesCollection;

        public MessagesPage(MessagesViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();

            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            Loaded += (s, e) =>
            {
                HookConversationMessages();
                ScrollToEnd();
            };
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.SelectedConversation))
            {
                HookConversationMessages();
                Dispatcher.BeginInvoke(new Action(ScrollToEnd));
            }
        }

        private void HookConversationMessages()
        {
            if (_currentMessagesCollection != null)
            {
                _currentMessagesCollection.CollectionChanged -= Messages_CollectionChanged;
                _currentMessagesCollection = null;
            }

            if (ViewModel.SelectedConversation?.Messages is INotifyCollectionChanged newColl)
            {
                _currentMessagesCollection = newColl;
                newColl.CollectionChanged += Messages_CollectionChanged;
            }
        }

        private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                Dispatcher.BeginInvoke(new Action(ScrollToEnd));
            }
        }

        private void ScrollToEnd()
        {
            if (MessagesListView?.Items != null && MessagesListView.Items.Count > 0)
            {
                MessagesListView.ScrollIntoView(MessagesListView.Items[^1]);
            }
        }

        private void OnMoreActionsClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void OnCopyContactNumberClick(object sender, RoutedEventArgs e)
        {
            ViewModel.CopyContactNumberCommand.Execute(null);
        }

        private void OnTestOtpNotificationClick(object sender, RoutedEventArgs e)
        {
            ViewModel.TestOtpNotificationCommand.Execute(null);
        }

        private void OnClearConversationClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearConversationCommand.Execute(null);
        }

        private void OnDeleteConversationClick(object sender, RoutedEventArgs e)
        {
            ViewModel.DeleteConversationCommand.Execute(ViewModel.SelectedConversation);
        }

        private void OnCopyOtpButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string code)
            {
                ViewModel.CopyOtpCodeCommand.Execute(code);
            }
        }

        private void OnRetryMessageClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SmsMessageModel msg)
            {
                ViewModel.RetryMessageCommand.Execute(msg);
            }
        }

        private void OnQuickCopyMessageClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SmsMessageModel msg)
            {
                ViewModel.CopyMessageCommand.Execute(msg);
            }
        }

        private void OnBubbleCopyClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target && target.DataContext is SmsMessageModel msg)
            {
                ViewModel.CopyMessageCommand.Execute(msg);
            }
        }

        private void OnBubbleDeleteClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target && target.DataContext is SmsMessageModel msg)
            {
                ViewModel.DeleteMessageCommand.Execute(msg);
            }
        }

        private void OnMessageInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                if (isCtrl || !isShift)
                {
                    e.Handled = true;
                    if (ViewModel.SendMessageCommand.CanExecute(null))
                    {
                        ViewModel.SendMessageCommand.Execute(null);
                    }
                }
            }
        }
    }
}
