using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoWin.Views.Pages;
using VoWin.Views.Windows;
using Wpf.Ui;

namespace VoWin.Services
{
    /// <summary>
    /// Managed host of the application.
    /// </summary>
    public class ApplicationHostService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;

        private INavigationWindow? _navigationWindow;

        public ApplicationHostService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Triggered when the application host is ready to start the service.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await HandleActivationAsync();
        }

        /// <summary>
        /// Triggered when the application host is performing a graceful shutdown.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates main window during activation.
        /// </summary>
        private async Task HandleActivationAsync()
        {
            if (!Application.Current.Windows.OfType<MainWindow>().Any())
            {
                _navigationWindow = (
                    _serviceProvider.GetService(typeof(INavigationWindow)) as INavigationWindow
                )!;
                _navigationWindow!.ShowWindow();
                if (_navigationWindow is Window win)
                {
                    Application.Current.MainWindow = win;
                }

                _navigationWindow.Navigate(typeof(SystemStatusPage));

                var kernelService = _serviceProvider.GetService<IVoKernelService>();
                if (kernelService != null)
                {
                    await kernelService.InitializeAsync();
                }
            }

            await Task.CompletedTask;
        }
    }
}
