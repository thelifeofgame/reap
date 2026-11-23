using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reap.App.Pages;
using Reap.Core;

namespace Reap.App;

public enum Pane { Leftovers, Sessions, Disk, Settings }

/// <summary>主窗口：左侧导航 + 页面。关闭只是隐藏，程序继续在托盘里运行。</summary>
public sealed class MainWindow : Window
{
    private readonly AppState _state;
    private readonly NavigationView _nav;
    private readonly Dictionary<Pane, NavigationViewItem> _items = [];
    private readonly InfoBar _toast;
    private readonly TextBlock _status;
    private CancellationTokenSource? _toastTimer;

    public MainWindow(AppState state)
    {
        _state = state;
        Title = "Reap";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Reap.ico"));
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(Scale(1080), Scale(720)));
        AppWindow.Closing += (sender, e) =>
        {
            e.Cancel = true;
            sender.Hide();
        };

        var titleBar = new Grid { Height = 40, Padding = new Thickness(16, 0, 0, 0) };
        titleBar.Children.Add(UI.Row(10, new Image
        {
            Width = 16, Height = 16,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Reap.ico"))),
        }, UI.Text("Reap", 12)));
        SetTitleBar(titleBar);

        _nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            OpenPaneLength = 220,
            IsPaneToggleButtonVisible = false,
        };
        Add(Pane.Leftovers, "残留进程", UI.Glyph.Warning);
        Add(Pane.Sessions, "活跃会话", UI.Glyph.Chat);
        Add(Pane.Disk, "磁盘清理", UI.Glyph.Drive);
        Add(Pane.Settings, "设置", UI.Glyph.Settings);
        _status = UI.Text("", 11, color: UI.Secondary);
        var refresh = UI.IconButton(UI.Glyph.Refresh, "立即重新扫描", () => _state.Refresh());
        _nav.PaneFooter = UI.Row(4, refresh, _status);
        ((StackPanel)_nav.PaneFooter).Margin = new Thickness(12, 0, 0, 12);
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: Pane pane }) _nav.Content = Create(pane);
        };

        _toast = new InfoBar
        {
            IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Success,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24), MaxWidth = 560,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(titleBar);
        Grid.SetRow(_nav, 1);
        root.Children.Add(_nav);
        Grid.SetRow(_toast, 1);
        root.Children.Add(_toast);
        Content = root;

        _state.Changed += UpdateChrome;
        _state.Toast += ShowToast;
        Navigate(Pane.Leftovers);
        UpdateChrome();
    }

    private int Scale(int value)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return (int)(value * GetDpiForWindow(hwnd) / 96.0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void Add(Pane pane, string title, string glyph)
    {
        var item = new NavigationViewItem { Content = title, Icon = new FontIcon { Glyph = glyph }, Tag = pane };
        _items[pane] = item;
        _nav.MenuItems.Add(item);
    }

    private UIElement Create(Pane pane) => pane switch
    {
        Pane.Leftovers => new LeftoversPage(_state),
        Pane.Sessions => new SessionsPage(_state),
        Pane.Disk => new DiskPage(_state),
        _ => new SettingsPage(_state),
    };

    public void Navigate(Pane pane) => _nav.SelectedItem = _items[pane];

    public void ShowAndActivate()
    {
        AppWindow.Show();
        Activate();
    }

    private void UpdateChrome()
    {
        SetBadge(Pane.Leftovers, _state.LeftoverCount);
        SetBadge(Pane.Sessions, _state.Sessions.Count);
        _status.Text = _state.Scanning ? "正在扫描…" : $"更新于 {_state.Scan.ScannedAt:HH:mm:ss}";
    }

    private void SetBadge(Pane pane, int count) =>
        _items[pane].InfoBadge = count > 0 ? new InfoBadge { Value = count } : null;

    private async void ShowToast(string text, bool error)
    {
        _toastTimer?.Cancel();
        var timer = _toastTimer = new CancellationTokenSource();
        _toast.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        _toast.Message = text;
        _toast.IsOpen = true;
        try { await Task.Delay(4000, timer.Token); }
        catch (TaskCanceledException) { return; }
        _toast.IsOpen = false;
    }
}
