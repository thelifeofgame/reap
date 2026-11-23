using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reap.Core;

namespace Reap.App.Pages;

/// <summary>一个进程一行：命令、PID、运行时长、端口、内存、结束按钮；子进程可展开。</summary>
public static class ProcessRow
{
    public static UIElement Build(ProcessItem item, AppState state)
    {
        var busy = state.Killing.Contains(item.Pid);
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 6, 6, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var command = UI.Text(item.DisplayCommand, 13, mono: true);
        ToolTipService.SetToolTip(command, item.Cmd);
        var meta = UI.Row(6, UI.Text($"PID {item.Pid} · 运行 {Format.Duration(item.Age())}", 12, color: UI.Secondary));
        var helpers = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed, Margin = new Thickness(4, 4, 0, 0) };
        if (item.Helpers.Count > 0)
        {
            var toggle = new HyperlinkButton { Content = $"+{item.Helpers.Count} 个子进程", Padding = new Thickness(0), FontSize = 12 };
            toggle.Click += (_, _) => helpers.Visibility = helpers.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            meta.Children.Add(toggle);
            foreach (var h in item.Helpers)
                helpers.Children.Add(UI.Text($"↳ {h.Pid}  {h.Cmd}", 11, color: UI.Secondary, mono: true));
        }
        var left = UI.Column(2, command, meta, helpers);
        grid.Children.Add(left);

        var ports = UI.Row(4);
        foreach (var port in item.Ports.Take(3))
        {
            var link = new HyperlinkButton { Content = UI.Pill($":{port}", UI.Accent, UI.Glyph.Globe), Padding = new Thickness(0) };
            ToolTipService.SetToolTip(link, $"在浏览器打开 http://localhost:{port}");
            link.Click += (_, _) => UI.Open($"http://localhost:{port}");
            ports.Children.Add(link);
        }
        Grid.SetColumn(ports, 1);
        grid.Children.Add(ports);

        var memory = UI.Text(Format.Bytes(item.Memory), 13, FontWeights.SemiBold, UI.Secondary);
        memory.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(memory, 2);
        grid.Children.Add(memory);

        UIElement action = busy
            ? new ProgressRing { IsActive = true, Width = 18, Height = 18 }
            : UI.IconButton(UI.Glyph.Cancel, "结束进程", () => state.Kill([item]), UI.Danger);
        Grid.SetColumn((FrameworkElement)action, 3);
        grid.Children.Add(action);

        var menu = new MenuFlyout();
        menu.Items.Add(Item("复制 PID", () => UI.Copy(item.Pid.ToString())));
        menu.Items.Add(Item("复制完整命令", () => UI.Copy(item.Cmd)));
        if (item.Cwd.Length > 0) menu.Items.Add(Item("在资源管理器中打开工作目录", () => UI.Open(item.Cwd)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("结束进程", () => state.Kill([item])));
        grid.ContextFlyout = menu;
        return grid;
    }

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }
}
