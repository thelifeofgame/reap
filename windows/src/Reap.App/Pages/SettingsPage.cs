using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reap.Core;

namespace Reap.App.Pages;

public sealed class SettingsPage : Page
{
    public SettingsPage(AppState state) : base(state, "设置") { }

    // 设置页的控件自己持有状态，只在打开时搭一次，避免重绘打断输入。
    private bool _built;

    protected override void Render()
    {
        if (_built) return;
        _built = true;
        var s = State.Settings;

        var sources = UI.Column(4);
        foreach (var agent in Enum.GetValues<AgentKind>())
        {
            var toggle = new ToggleSwitch { IsOn = s.EnabledAgents.Contains(agent), OnContent = "", OffContent = "" };
            toggle.Toggled += (_, _) =>
            {
                s.EnabledAgents.Remove(agent);
                if (toggle.IsOn) s.EnabledAgents.Add(agent);
                State.SaveSettings();
            };
            sources.Children.Add(Line(UI.Row(8, UI.AgentBadge(agent), UI.Text(Describe(agent), 12, color: UI.Secondary)), toggle));
        }

        var auto = new ToggleSwitch { IsOn = s.AutoEndIdle, OnContent = "", OffContent = "" };
        auto.Toggled += (_, _) =>
        {
            s.AutoEndIdle = auto.IsOn;
            State.SaveSettings();
        };
        var limit = new NumberBox
        {
            Value = s.IdleLimitMinutes, Minimum = Settings.MinimumIdleLimit, Maximum = 24 * 60, SmallChange = 30, LargeChange = 60,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 160,
        };
        limit.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) return;
            s.IdleLimitMinutes = Math.Max(Settings.MinimumIdleLimit, (int)e.NewValue);
            State.SaveSettings();
        };

        var login = new ToggleSwitch { IsOn = Settings.LaunchAtLogin, OnContent = "", OffContent = "" };
        login.Toggled += (_, _) => Settings.LaunchAtLogin = login.IsOn;

        var version = typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "开发版";
        var credit = new HyperlinkButton { Content = "检测思路参考 seshka（MIT）", NavigateUri = new Uri("https://github.com/jigjoy-ai/seshka"), Padding = new Thickness(0) };

        Body.Children.Add(Group("检测来源", sources));
        Body.Children.Add(Group("空闲会话", UI.Column(4,
            Line(UI.Text("自动结束空闲会话", 14), auto),
            Line(UI.Text("空闲多少分钟算闲置", 14), limit),
            UI.Text("只对能读到状态的 Claude Code 会话生效。对话记录不会丢，之后可以 claude --resume 接着聊。", 12, color: UI.Secondary, wrap: true))));
        Body.Children.Add(Group("通用", Line(UI.Text("登录 Windows 时启动（只进托盘）", 14), login)));
        Body.Children.Add(Group("关于", UI.Column(6,
            Line(UI.Text("版本", 14), UI.Text(version, 14, color: UI.Secondary)),
            Line(UI.Text("数据", 14), UI.Text("只在本机读取进程和文件，不联网，不上传任何东西。", 13, color: UI.Secondary)),
            Line(UI.Text("致谢", 14), credit))));
    }

    private static UIElement Group(string title, UIElement content) =>
        UI.Column(8, UI.Text(title, 16, FontWeights.SemiBold), UI.Card(content, 14));

    private static Grid Line(UIElement left, UIElement right)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        if (right is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 1);
        }
        grid.Children.Add(right);
        return grid;
    }

    private static string Describe(AgentKind agent) => agent switch
    {
        AgentKind.Claude => "按 CLAUDECODE 环境变量和会话记录识别",
        AgentKind.Codex => "按 CODEX_* 环境变量识别，浏览器等应用不计",
        _ => "按 CURSOR_AGENT=1 识别，Cursor 未公开文档，可能漏报",
    };
}
