using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reap.Core;
using Windows.UI;

namespace Reap.App.Pages;

public sealed class SessionsPage : Page
{
    private AgentKind? _agent;
    private readonly HashSet<int> _expanded = [];

    public SessionsPage(AppState state) : base(state, "活跃会话")
    {
        Toolbar.Children.Add(AgentFilter(a =>
        {
            _agent = a;
            Render();
        }));
    }

    protected override void Render()
    {
        Body.Children.Clear();
        var idle = State.IdleSessions;
        var limit = Format.Duration(State.Settings.IdleLimitMinutes * 60);
        Body.Children.Add(UI.StatRow(
            UI.StatCard("活跃会话", State.Sessions.Count.ToString(), UI.Glyph.Chat, UI.Accent),
            UI.StatCard("工作中", State.Sessions.Count(s => s.Status == SessionStatus.Busy).ToString(), UI.Glyph.Pulse, UI.Busy),
            UI.StatCard($"空闲超过 {limit}", idle.Count.ToString(), UI.Glyph.Moon, UI.Idle)));

        if (idle.Count > 0)
        {
            var bar = new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = $"{idle.Count} 个会话已空闲超过 {limit}",
                Message = $"还占着 {Format.Bytes(idle.Aggregate(0UL, (s, x) => s + x.Memory))}。",
                ActionButton = UI.Button("全部结束", ConfirmEndIdle),
            };
            Body.Children.Add(bar);
        }

        if (!State.HasScanned)
        {
            Body.Children.Add(new ProgressRing { IsActive = true, Margin = new Thickness(0, 48, 0, 0) });
            return;
        }
        var sessions = State.Sessions.Where(s => _agent is null || s.Agent == _agent).ToList();
        if (sessions.Count == 0)
        {
            Body.Children.Add(UI.Empty(UI.Glyph.Moon, "没有打开的会话",
                "Claude Code、Codex 或 Cursor Agent 运行时，会在这里显示状态和它开着的进程。", Colors.Gray));
            return;
        }
        foreach (var session in sessions) Body.Children.Add(Card(session));
    }

    private static (string Text, Color Color) StatusOf(ActiveSession s) => s.Status switch
    {
        SessionStatus.Busy => ("工作中", UI.Busy),
        SessionStatus.Idle => ("空闲 " + Format.Duration(s.IdleSeconds() ?? 0), UI.Idle),
        SessionStatus.Other => (s.OtherStatus ?? "未知", Colors.Gray),
        _ => (s.Managed ? "由 App 管理" : "状态未知", Colors.Gray),
    };

    private UIElement Card(ActiveSession session)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(UI.AgentBadge(session.Agent));

        var (statusText, statusColor) = StatusOf(session);
        var status = UI.Pill(statusText, statusColor, "●");
        if (session.Managed) ToolTipService.SetToolTip(status, "App 内置的后台进程，结束它会关掉整个 App，请在 App 里操作。");
        var sub = $"PID {session.Host.Pid} · 已运行 {Format.Duration(session.Host.Age())}";
        if (session.Title is { } t) sub = t + "  " + sub;
        var info = UI.Column(2, UI.Row(8, UI.Text(session.Project, 15, FontWeights.SemiBold), status), UI.Text(sub, 12, color: UI.Secondary));
        Grid.SetColumn(info, 1);
        header.Children.Add(info);

        var size = UI.Column(0, UI.Text(Format.Bytes(session.Memory), 15, FontWeights.SemiBold),
            UI.Text($"{session.Items.Count} 个后台进程", 12, color: UI.Secondary));
        size.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(size, 2);
        header.Children.Add(size);

        var actions = UI.Row(4);
        if (session.Agent == AgentKind.Claude && session.Session is { } id)
            actions.Children.Add(UI.IconButton(UI.Glyph.Copy, "复制恢复命令 claude --resume", () => UI.Copy($"claude --resume {id}")));
        if (!session.Managed)
        {
            var end = UI.Button("结束会话", () => ConfirmEnd(session), danger: true);
            end.IsEnabled = !State.Killing.Contains(session.Host.Pid);
            actions.Children.Add(end);
        }
        Grid.SetColumn(actions, 3);
        header.Children.Add(actions);

        var body = UI.Column(8, header);
        if (session.Host.Cwd.Length > 3)
            body.Children.Add(UI.Row(6, UI.Icon(UI.Glyph.Folder, 12, UI.Secondary), UI.Text(Reap.Windows.Disk.Abbreviate(session.Host.Cwd), 12, color: UI.Secondary)));
        if (session.Items.Count > 0)
        {
            var open = _expanded.Contains(session.Host.Pid);
            var toggle = new HyperlinkButton { Content = open ? "收起后台进程" : $"查看 {session.Items.Count} 个后台进程", Padding = new Thickness(0), FontSize = 12 };
            toggle.Click += (_, _) =>
            {
                if (!_expanded.Remove(session.Host.Pid)) _expanded.Add(session.Host.Pid);
                Render();
            };
            body.Children.Add(toggle);
            if (open)
                foreach (var item in session.Items) body.Children.Add(ProcessRow.Build(item, State));
        }
        return UI.Card(body);
    }

    private async void ConfirmEnd(ActiveSession session)
    {
        var message = session.Agent == AgentKind.Claude
            ? "对话记录不会丢，之后可以用 claude --resume 接着聊。"
            : "会话里正在进行的工作会中断。";
        if (await UI.Confirm(XamlRoot, "结束这个会话？", message, $"结束会话和它的 {session.Items.Count} 个进程"))
            State.End([session]);
    }

    private async void ConfirmEndIdle()
    {
        var idle = State.IdleSessions;
        if (idle.Count == 0) return;
        if (await UI.Confirm(XamlRoot, $"结束 {idle.Count} 个空闲会话？", "对话记录不会丢，之后可以用 claude --resume 接着聊。", "结束"))
            State.End(idle);
    }
}
