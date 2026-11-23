using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Reap.App.Pages;

/// <summary>页面基类：顶部工具栏 + 可滚动内容；状态变化时重绘内容。</summary>
public abstract class Page : UserControl
{
    protected readonly AppState State;
    protected readonly StackPanel Body = new() { Spacing = 14, Padding = new Thickness(24, 8, 24, 32) };
    protected readonly StackPanel Toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    protected Page(AppState state, string title)
    {
        State = state;
        var header = new Grid { Padding = new Thickness(24, 16, 24, 8), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(UI.Text(title, 26, Microsoft.UI.Text.FontWeights.SemiBold));
        Grid.SetColumn(Toolbar, 1);
        header.Children.Add(Toolbar);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        var scroll = new ScrollViewer { Content = Body };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        Loaded += (_, _) =>
        {
            State.Changed += Render;
            Render();
        };
        Unloaded += (_, _) => State.Changed -= Render;
    }

    protected abstract void Render();

    /// <summary>来源筛选：全部 / Claude / Codex / Cursor。</summary>
    protected static SelectorBar AgentFilter(Action<Reap.Core.AgentKind?> onChange)
    {
        var bar = new SelectorBar();
        bar.Items.Add(new SelectorBarItem { Text = "全部", Tag = null, IsSelected = true });
        foreach (var agent in Enum.GetValues<Reap.Core.AgentKind>())
            bar.Items.Add(new SelectorBarItem { Text = agent.ToString(), Tag = agent });
        bar.SelectionChanged += (s, _) => onChange(s.SelectedItem?.Tag as Reap.Core.AgentKind?);
        return bar;
    }
}
