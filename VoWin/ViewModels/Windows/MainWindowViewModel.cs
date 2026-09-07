using System.Collections.ObjectModel;
using VoWin.Views.Pages;
using Wpf.Ui.Controls;

namespace VoWin.ViewModels.Windows
{
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _applicationTitle = "VoWin";

        [ObservableProperty]
        private ObservableCollection<object> _menuItems = new()
        {
            new NavigationViewItem()
            {
                Content = "系统状态",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Pulse24 },
                TargetPageType = typeof(SystemStatusPage)
            },
            new NavigationViewItem()
            {
                Content = "设备管理",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Sim24 },
                TargetPageType = typeof(ModemManagerPage)
            },
            new NavigationViewItem()
            {
                Content = "电话",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Call24 },
                TargetPageType = typeof(PhonePage)
            },
            new NavigationViewItem()
            {
                Content = "短信",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Chat24 },
                TargetPageType = typeof(MessagesPage)
            },
            new NavigationViewItem()
            {
                Content = "代理分流",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Globe24 },
                TargetPageType = typeof(ProxyPage)
            },
            new NavigationViewItem()
            {
                Content = "远程控制",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Bot24 },
                TargetPageType = typeof(RemoteControlPage)
            }
        };

        [ObservableProperty]
        private ObservableCollection<object> _footerMenuItems = new()
        {
            new NavigationViewItem()
            {
                Content = "设置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(SettingsPage)
            },
            new NavigationViewItem()
            {
                Content = "关于",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Info24 },
                TargetPageType = typeof(AboutPage)
            }
        };

        [ObservableProperty]
        private ObservableCollection<Wpf.Ui.Controls.MenuItem> _trayMenuItems = new()
        {
            new Wpf.Ui.Controls.MenuItem { Header = "主界面", Tag = "tray_home" }
        };
    }
}
