using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reap.Core;

namespace Reap.App.Pages;

public sealed class LeftoversPage : Page
{
    private string _search = "";
    private AgentKind? _agent;

    public LeftoversPage(AppState state) : base(state, "残留进程")
    {
        var search = new AutoSuggestBox { PlaceholderText = "搜索命令、端口、PID、项目", Width = 260, QueryIcon = new SymbolIcon(Symbol.Find) };
        search.TextChanged += (s, _) =>
        {
            _search = s.Text.Trim();
            Render();
        };
        Toolbar.Children.Add(AgentFilter(a =>
        {
            _agent = a;
            Render();
        }));
        Toolbar.Children.Add(search);
        Toolbar.Children.Add(UI.Button("全部结束", ConfirmKillAll, danger: true, glyph: UI.Glyph.Cancel));
    }

    private List<LeftoverGroup> Groups() => State.Leftovers
        .Where(g => _agent is null || g.Agent == _agent)
        .Select(g =>
        {
            if (_search.Length == 0 || g.Project.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || (g.Title?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false)) return g;
            var items = g.Items.Where(i => i.Cmd.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || i.Pid.ToString() == _search || i.Ports.Any(p => p.ToString() == _search)).ToList();
            return items.Count == 0 ? null : new LeftoverGroup
            {
                Id = g.Id, Agent = g.Agent, Session = g.Session, Project = g.Project, Title = g.Title, Worktree = g.Worktree, Items = items,
            };
        })
        .OfType<LeftoverGroup>()
        .ToList();

    protected override void Render()
    {
        Body.Children.Clear();
        Body.Children.Add(UI.StatRow(
            UI.StatCard("残留进程", State.LeftoverCount.ToString(), UI.Glyph.Warning, State.LeftoverCount > 0 ? UI.Danger : UI.Busy),
            UI.StatCard("占用内存", Format.Bytes(State.LeftoverMemory), UI.Glyph.Memory, UI.Accent),
            UI.StatCard("本次已释放", Format.Bytes(State.FreedTotal), UI.Glyph.Leaf, UI.Busy)));

        if (!State.HasScanned)
        {
            Body.Children.Add(new ProgressRing { IsActive = true, Margin = new Thickness(0, 48, 0, 0) });
            return;
        }
        var groups = Groups();
        if (groups.Count == 0)
        {
            Body.Children.Add(State.Leftovers.Count == 0
                ? UI.Empty(UI.Glyph.Check, "很干净", "没有 AI 编码会话留下的进程。关掉的会话如果留下开发服务器、监听器或浏览器，会出现在这里。", UI.Busy)
                : UI.Empty(UI.Glyph.Check, "没有匹配的进程", "换个关键词或来源试试。", UI.Busy));
            return;
        }
        foreach (var group in groups) Body.Children.Add(Card(group));
    }

    private UIElement Card(LeftoverGroup group)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(UI.AgentBadge(group.Agent));

        var titleRow = UI.Row(8, UI.Text(group.Project, 15, FontWeights.SemiBold));
        if (group.Worktree is { } wt) titleRow.Children.Add(UI.Pill(wt.Label, UI.Accent, UI.Glyph.Branch));
        var sub = new List<string>();
        if (group.Title is { } t) sub.Add(t);
        if (group.ShortSession is { } s) sub.Add(s);
        sub.Add($"已关闭 · 最早 {Format.Duration((int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - group.OldestStart))} 前启动");
        var info = UI.Column(2, titleRow, UI.Text(string.Join("  ", sub), 12, color: UI.Secondary));
        Grid.SetColumn(info, 1);
        header.Children.Add(info);

        var size = UI.Column(0, UI.Text(Format.Bytes(group.Memory), 15, FontWeights.SemiBold), UI.Text($"{group.ProcessCount} 个进程", 12, color: UI.Secondary));
        size.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(size, 2);
        header.Children.Add(size);

        var end = UI.Button("结束本组", () => State.Kill(group.Items), danger: true);
        Grid.SetColumn(end, 3);
        header.Children.Add(end);

        var list = UI.Column(0);
        foreach (var item in group.Items) list.Children.Add(ProcessRow.Build(item, State));
        return UI.Card(UI.Column(10, header, new MenuFlyoutSeparator(), list));
    }

    private async void ConfirmKillAll()
    {
        if (State.Leftovers.Count == 0) return;
        if (await UI.Confirm(XamlRoot, $"结束全部 {State.LeftoverCount} 个残留进程？",
                "这些进程所属的会话都已关闭。有窗口的先请求关闭，2 秒内没退出的会被强制结束。",
                $"全部结束（释放约 {Format.Bytes(State.LeftoverMemory)}）"))
            State.KillAll();
    }
}
