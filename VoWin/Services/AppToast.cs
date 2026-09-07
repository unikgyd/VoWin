using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace VoWin.Services
{
    /// <summary>
    /// 提供应用内统一的 Fluent Design Toast / Snackbar 消息通知服务
    /// </summary>
    public static class AppToast
    {
        public static void Show(string title, string message, ControlAppearance appearance = ControlAppearance.Success, TimeSpan? timeout = null)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                void Action()
                {
                    try
                    {
                        var snackbarService = App.Services.GetService<ISnackbarService>();
                        if (snackbarService == null) return;

                        IconElement icon = appearance switch
                        {
                            ControlAppearance.Success => new SymbolIcon(SymbolRegular.CheckmarkCircle24) { FontSize = 20 },
                            ControlAppearance.Danger => new SymbolIcon(SymbolRegular.DismissCircle24) { FontSize = 20 },
                            ControlAppearance.Caution => new SymbolIcon(SymbolRegular.Warning24) { FontSize = 20 },
                            _ => new SymbolIcon(SymbolRegular.Info24) { FontSize = 20 }
                        };

                        snackbarService.Show(
                            title,
                            message,
                            appearance,
                            icon,
                            timeout ?? TimeSpan.FromSeconds(2.5)
                        );
                    }
                    catch
                    {
                        // Ignore any snackbar dispatch failure gracefully
                    }
                }

                if (app.Dispatcher.CheckAccess())
                {
                    Action();
                }
                else
                {
                    app.Dispatcher.BeginInvoke(new Action(Action));
                }
            }
            catch
            {
                // Ignore
            }
        }

        public static void ShowCopySuccess(string itemName)
        {
            Show("已复制到剪贴板", $"{itemName} 已成功复制", ControlAppearance.Success);
        }
    }
}
