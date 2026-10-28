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

        private void UpdateLogLayout()
        {
            if (ExpandedTerminalPanel == null) return;
            var bottom = ActualWidth < 1100;
            Grid.SetColumn(ExpandedTerminalPanel, bottom ? 0 : 1);
            Grid.SetRow(ExpandedTerminalPanel, bottom ? 1 : 0);
            Grid.SetColumnSpan(ExpandedTerminalPanel, bottom ? 2 : 1);
            ExpandedTerminalPanel.Width = bottom ? double.NaN : 370;
            ExpandedTerminalPanel.Height = bottom ? 330 : double.NaN;
            ExpandedTerminalPanel.Margin = bottom ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        }

        public SystemStatusPage(SystemStatusViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
            SizeChanged += (_, _) => UpdateLogLayout();

            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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
            // The connection-details tab owns its own scroll viewer.
            for (var current = e.OriginalSource as DependencyObject; current != null && current != ExpandedTerminalPanel;
                 current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (current is ScrollViewer other && other != sv) return;
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta / 2.0));
                e.Handled = true;
            }
        }
    }
}
