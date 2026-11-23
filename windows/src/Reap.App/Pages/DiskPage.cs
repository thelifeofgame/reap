using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reap.Core;
using Reap.Windows;

namespace Reap.App.Pages;

public sealed class DiskPage : Page
{
    private bool _loaded;

    public DiskPage(AppState state) : base(state, "磁盘清理")
    {
        Toolbar.Children.Add(UI.Button("重新检查", () => State.LoadDisk(), glyph: UI.Glyph.Refresh));
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            if (State.Worktrees.Count == 0 && State.Caches.Count == 0) State.LoadDisk();
        };
    }

    protected override void Render()
    {
        Body.Children.Clear();
        Body.Children.Add(UI.StatRow(
            UI.StatCard("可释放空间", Format.Bytes(State.DiskReclaimable), UI.Glyph.Drive, UI.Accent),
            UI.StatCard("Agent worktree", State.Worktrees.Count.ToString(), UI.Glyph.Branch, UI.Accent),
            UI.StatCard("开发缓存", State.CachesLoading ? "测量中" : $"{State.Caches.Count} 项", UI.Glyph.Box, UI.Accent)));
        Body.Children.Add(UI.Text("只有点击后才会删除。正在使用的不会列为可删；有未提交改动的 worktree 移到回收站；删除 worktree 不会删分支。",
            13, color: UI.Secondary, wrap: true));

        Body.Children.Add(Section("Agent worktree", UI.Glyph.Branch, State.Worktrees, State.DiskLoading, "没有 Agent 创建的 worktree。"));
        Body.Children.Add(Section("可自动重建的缓存", UI.Glyph.Box, State.Caches, State.CachesLoading,
            $"没有超过 {Format.Bytes(Disk.SmallestCache)} 的缓存。"));
    }

    private UIElement Section(string title, string glyph, List<DiskItem> items, bool loading, string empty)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = UI.Row(8, UI.Icon(glyph, 14), UI.Text(title, 16, FontWeights.SemiBold));
        if (loading) left.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        header.Children.Add(left);
        var total = items.Aggregate(0UL, (s, i) => s + (i.Bytes ?? 0));
        if (total > 0)
        {
            var t = UI.Text(Format.Bytes(total), 13, color: UI.Secondary);
            Grid.SetColumn(t, 1);
            header.Children.Add(t);
        }

        UIElement content;
        if (items.Count == 0)
        {
            content = UI.Card(UI.Text(loading ? "正在检查…" : empty, 13, color: UI.Secondary), 12);
        }
        else
        {
            var list = UI.Column(0);
            foreach (var item in items) list.Children.Add(Row(item));
            content = UI.Card(list, 4);
        }
        return UI.Column(8, header, content);
    }

    private UIElement Row(DiskItem item)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 8, 10, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (item.Agent is { } agent) grid.Children.Add(UI.AgentBadge(agent, compact: true));

        var detail = UI.Text(item.Note ?? item.Detail, 12, color: UI.Secondary);
        ToolTipService.SetToolTip(detail, string.Join("\n", item.Paths.Select(Disk.Abbreviate)));
        var info = UI.Column(2, UI.Text(item.Title, 14, FontWeights.SemiBold), detail);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        UIElement? chip = item.State switch
        {
            DiskState.Busy => UI.Pill(item.BusyReason ?? "使用中", Microsoft.UI.Colors.Gray, UI.Glyph.Lock),
            DiskState.Uncommitted => UI.Pill("有未提交改动", UI.Idle, UI.Glyph.Warning),
            _ => null,
        };
        if (chip is FrameworkElement c)
        {
            Grid.SetColumn(c, 2);
            grid.Children.Add(c);
        }

        var size = UI.Text(item.Bytes is { } b ? Format.Bytes(b) : "—", 13, color: UI.Secondary);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(size, 3);
        grid.Children.Add(size);

        var reveal = UI.IconButton(UI.Glyph.Folder, "在资源管理器中打开", () => UI.Open(item.Paths.FirstOrDefault() ?? ""));
        Grid.SetColumn(reveal, 4);
        grid.Children.Add(reveal);

        UIElement action;
        if (State.Removing.Contains(item.Id))
        {
            action = new ProgressRing { IsActive = true, Width = 18, Height = 18, Margin = new Thickness(20, 0, 20, 0) };
        }
        else
        {
            var button = UI.Button(item.State == DiskState.Uncommitted ? "移走" : "清理", () => ConfirmRemove(item));
            button.MinWidth = 64;
            button.IsEnabled = item.State != DiskState.Busy;
            action = button;
        }
        Grid.SetColumn((FrameworkElement)action, 5);
        grid.Children.Add(action);
        return grid;
    }

    private async void ConfirmRemove(DiskItem item)
    {
        var size = item.Bytes is { } b ? $"（{Format.Bytes(b)}）" : "";
        var (title, message, primary) = item.Kind switch
        {
            DiskKind.Worktree when item.State == DiskState.Uncommitted =>
                ($"把「{item.Title}」移到回收站？{size}", "里面有未提交的改动，所以移到回收站而不是直接删除。分支保留。", "移到回收站"),
            DiskKind.Worktree => ($"删除「{item.Title}」？{size}", "用 git worktree remove 删除，分支保留。", "删除"),
            _ => ($"删除「{item.Title}」？{size}", (item.Note ?? "") + " 删除后无法恢复。", "删除"),
        };
        if (await UI.Confirm(XamlRoot, title, message, primary)) State.Remove(item);
    }
}
