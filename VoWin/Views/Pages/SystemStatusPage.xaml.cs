using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages
{
    public partial class SystemStatusPage : INavigableView<SystemStatusViewModel>
    {
        public SystemStatusViewModel ViewModel { get; }
        private ScrollViewer? _logScrollViewer;
        private int _scrollPending;

        public SystemStatusPage(SystemStatusViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();

            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            DisableAncestorScrollViewers();
            HookLogAutoScroll();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

                if (ViewModel.LogEntries != null)
                {
                    ViewModel.LogEntries.CollectionChanged -= OnLogEntriesChanged;
                }

                if (ViewModel.FilteredLogs is INotifyCollectionChanged filteredNcc)
                {
                    filteredNcc.CollectionChanged -= OnFilteredLogsChanged;
                }
            }
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemStatusViewModel.IsAutoScrollPaused))
            {
                if (ViewModel?.IsAutoScrollPaused == false)
                {
                    ScheduleAutoScroll();
                }
            }
        }

        private void DisableAncestorScrollViewers()
        {
            DependencyObject? current = this;
            while (current != null)
            {
                current = VisualTreeHelper.GetParent(current);
                if (current is ScrollViewer sv)
                {
                    sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    sv.CanContentScroll = false;
                }
            }
        }

        private void HookLogAutoScroll()
        {
            if (ViewModel?.LogEntries != null)
            {
                ViewModel.LogEntries.CollectionChanged -= OnLogEntriesChanged;
                ViewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;
            }

            if (ViewModel?.FilteredLogs is INotifyCollectionChanged filteredNcc)
            {
                filteredNcc.CollectionChanged -= OnFilteredLogsChanged;
                filteredNcc.CollectionChanged += OnFilteredLogsChanged;
            }

            ScheduleAutoScroll();
        }

        private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (ViewModel?.IsAutoScrollPaused == true) return;

            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            {
                ScheduleAutoScroll();
            }
        }

        private void OnFilteredLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (ViewModel?.IsAutoScrollPaused == true) return;

            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            {
                ScheduleAutoScroll();
            }
        }

        private void ScheduleAutoScroll()
        {
            if (ViewModel?.IsAutoScrollPaused == true) return;

            if (Interlocked.Exchange(ref _scrollPending, 1) != 0)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ScrollLogToEnd();
                }
                finally
                {
                    Interlocked.Exchange(ref _scrollPending, 0);
                }
            }, DispatcherPriority.Background);
        }

        private void ScrollLogToEnd()
        {
            if (ViewModel?.IsAutoScrollPaused == true) return;
            if (LogList.Items.Count == 0) return;

            var sv = FindLogScrollViewer();
            if (sv != null)
            {
                sv.ScrollToEnd();
            }

            try
            {
                if (LogList.Items.Count > 0)
                {
                    LogList.ScrollIntoView(LogList.Items[^1]);
                }
            }
            catch
            {
                // Ignore collection shift race condition during high-rate drain
            }
        }

        private ScrollViewer? FindLogScrollViewer()
        {
            if (_logScrollViewer != null) return _logScrollViewer;
            _logScrollViewer = FindVisualChild<ScrollViewer>(LogList);
            return _logScrollViewer;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }

        private void OnTerminalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = FindLogScrollViewer();
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta / 2.0));
                e.Handled = true;
            }
        }
    }
}
