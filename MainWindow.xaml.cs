using System.Windows;
using GameOp.Pages;
using GameOp.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace GameOp;

public partial class MainWindow : FluentWindow
{
    private static readonly Type[] PageOrder =
        [typeof(DashboardPage), typeof(LibraryPage), typeof(EmulatorsPage), typeof(TweaksPage), typeof(SettingsPage)];

    private int _pageIndex;
    private GamepadNavigator? _pad;

    public MainWindow()
    {
        AppState.Instance.Initialize();
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        Loaded += async (_, _) =>
        {
            RootNavigation.Navigate(typeof(DashboardPage));
            _pad = new GamepadNavigator(this, SwitchPage, () => RootNavigation.GoBack());
            _pad.Start();
            AppState.Instance.StartWatcher();
            await AppState.Instance.DetectEmulatorsAsync();
            AppState.Instance.StartUpdateTimer();
            await AppState.Instance.CheckUpdatesAsync(AppState.Instance.Settings.AutoUpdateEmulators);
        };
        RootNavigation.Navigated += (_, e) =>
        {
            var i = Array.IndexOf(PageOrder, e.Page?.GetType());
            if (i >= 0) _pageIndex = i;
        };
    }

    private void SwitchPage(int delta)
    {
        _pageIndex = (_pageIndex + delta + PageOrder.Length) % PageOrder.Length;
        RootNavigation.Navigate(PageOrder[_pageIndex]);
    }
}
