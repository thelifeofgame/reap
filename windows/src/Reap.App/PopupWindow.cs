using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reap.Core;

namespace Reap.App;

/// <summary>点托盘图标弹出的小窗：看一眼残留数量、一键结束、跳到主窗口。失去焦点就收起。</summary>
public sealed class PopupWindow : Window
{
    private const int PopupWidth = 380;
    private const int PopupHeight = 480;

    private readonly AppState _state;
    private readonly Action<Pane> _openMain;
    private readonly StackPanel _body = new() { Spacing = 12, Padding = new Thickness(16) };

    public PopupWindow(AppState state, Action<Pane> openMain)
    {
        _state = state;
        _openMain = openMain;
        Title = "Reap";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        AppWindow.Closing += (sender, e) =>
        {
            e.Cancel = true;
            sender.Hide();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) AppWindow.Hide();
        };
        Content = new ScrollViewer { Content = _body };
        _state.Changed += Render;
        Render();
    }

    /// <summary>贴着任务栏右下角（通知区域上方）弹出。</summary>
    public void Toggle()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
            return;
        }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = (int)(PopupWidth * scale);
        var h = (int)(PopupHeight * scale);
        var margin = (int)(12 * scale);
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(work.X + work.Width - w - margin, work.Y + work.Height - h - margin, w, h));
        AppWindow.Show();
        Activate();
        _state.Refresh();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void Render()
    {
        _body.Children.Clear();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(UI.Text("Reap", 16, FontWeights.SemiBold));
        UIElement refresh = _state.Scanning
            ? new ProgressRing { IsActive = true, Width = 16, Height = 16 }
            : UI.IconButton(UI.Glyph.Refresh, "重新扫描", () => _state.Refresh());
        Grid.SetColumn((FrameworkElement)refresh, 1);
        header.Children.Add(refresh);
        _body.Children.Add(header);

        var leftovers = _state.Leftovers;
        var clean = leftovers.Count == 0;
        var summary = new Grid { ColumnSpacing = 12 };
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.Children.Add(UI.Icon(clean ? UI.Glyph.Check : UI.Glyph.Warning, 28, UI.Brush(clean ? UI.Busy : UI.Danger)));
        var text = clean
            ? UI.Column(2, UI.Text("很干净", 18, FontWeights.SemiBold), UI.Text("没有已关闭会话留下的进程", 12, color: UI.Secondary))
            : UI.Column(2, UI.Text($"{_state.LeftoverCount} 个残留进程", 18, FontWeights.SemiBold),
                UI.Text($"来自 {leftovers.Count} 个已关闭会话，占用 {Format.Bytes(_state.LeftoverMemory)}", 12, color: UI.Secondary));
        Grid.SetColumn(text, 1);
        summary.Children.Add(text);
        _body.Children.Add(UI.Card(summary, 12));

        foreach (var group in leftovers.Take(5))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(UI.AgentBadge(group.Agent, compact: true));
            var ports = group.Items.SelectMany(i => i.Ports).Take(3).Select(p => $":{p}");
            var line = string.Join(" · ", new[] { group.Project, string.Join(" ", ports), $"{group.ProcessCount} 个进程" }.Where(s => s.Length > 0));
            var info = UI.Column(0, UI.Text(group.Title ?? group.Project, 13, FontWeights.SemiBold), UI.Text(line, 11, color: UI.Secondary));
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            var mem = UI.Text(Format.Bytes(group.Memory), 12, color: UI.Secondary);
            Grid.SetColumn(mem, 2);
            row.Children.Add(mem);
            var kill = UI.IconButton(UI.Glyph.Cancel, "结束这一组", () => _state.Kill(group.Items), UI.Danger);
            Grid.SetColumn(kill, 3);
            row.Children.Add(kill);
            _body.Children.Add(row);
        }
        if (leftovers.Count > 5)
        {
            var more = new HyperlinkButton { Content = $"还有 {leftovers.Count - 5} 组，在主窗口查看", FontSize = 12 };
            more.Click += (_, _) => Open(Pane.Leftovers);
            _body.Children.Add(more);
        }

        var idle = _state.IdleSessions;
        if (idle.Count > 0)
        {
            _body.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                Message = $"{idle.Count} 个会话空闲超过 {Format.Duration(_state.Settings.IdleLimitMinutes * 60)}",
                ActionButton = UI.Button("结束", () => _state.End(idle)),
            });
        }

        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var open = UI.Button("打开主窗口", () => Open(Pane.Leftovers));
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        var killAll = UI.Button(clean ? "无需清理" : "全部结束", () => _state.KillAll(), accent: true);
        killAll.HorizontalAlignment = HorizontalAlignment.Stretch;
        killAll.IsEnabled = !clean;
        Grid.SetColumn(killAll, 1);
        actions.Children.Add(open);
        actions.Children.Add(killAll);
        _body.Children.Add(actions);
    }

    private void Open(Pane pane)
    {
        AppWindow.Hide();
        _openMain(pane);
    }
}
