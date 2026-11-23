namespace Reap.Core;

public enum AgentKind { Claude, Codex, Cursor }

/// <summary>环境变量标记。Value 为 null 时只要求变量存在。</summary>
public sealed record Marker(string Env, string? Value = null)
{
    public bool Matches(IReadOnlyDictionary<string, string> env) =>
        env.TryGetValue(Env, out var actual) && (Value is null || Value == actual);
}

/// <summary>一个 AI 编码工具的识别规则，字段与 spec/agents.json 一一对应，测试会校验两边一致。</summary>
public sealed class AgentRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Experimental { get; init; }
    public IReadOnlyList<string> HostNames { get; init; } = [];
    public IReadOnlyList<string> HostPathContains { get; init; } = [];
    public IReadOnlyList<Marker> Markers { get; init; } = [];
    public string? SessionEnv { get; init; }
    public string? HostPidEnv { get; init; }
    public IReadOnlyList<string> IgnorePathContains { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> IgnoreExePrefixes { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public AgentKind Kind => Enum.Parse<AgentKind>(Id, ignoreCase: true);

    private static readonly string[] WindowsBrowsers =
    [
        "C:/Program Files/Google/Chrome/", "C:/Program Files (x86)/Google/Chrome/",
        "C:/Program Files (x86)/Microsoft/Edge/", "C:/Program Files/Mozilla Firefox/", "C:/Program Files/WindowsApps/",
    ];

    /// <summary>判定时按此顺序匹配。Claude 在前：它的标记最精确。</summary>
    public static readonly IReadOnlyList<AgentRule> All =
    [
        new AgentRule
        {
            Id = "claude", Name = "Claude Code",
            HostNames = ["claude"],
            Markers = [new Marker("CLAUDECODE", "1")],
            SessionEnv = "CLAUDE_CODE_SESSION_ID", HostPidEnv = "CLAUDE_PID",
        },
        new AgentRule
        {
            Id = "codex", Name = "Codex",
            HostNames = ["codex"],
            HostPathContains = ["/ChatGPT.app/Contents/MacOS/", "/Codex.app/Contents/MacOS/"],
            Markers = [new Marker("CODEX_SHELL", "1"), new Marker("CODEX_SANDBOX"), new Marker("CODEX_THREAD_ID")],
            SessionEnv = "CODEX_THREAD_ID",
            IgnorePathContains = ["/ChatGPT.app/", "/Codex.app/", "/.codex/"],
            IgnoreExePrefixes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["darwin"] = ["/Applications/", "/System/"],
                ["windows"] = WindowsBrowsers,
            },
        },
        new AgentRule
        {
            Id = "cursor", Name = "Cursor", Experimental = true,
            HostNames = ["cursor-agent"],
            HostPathContains = ["/Cursor.app/Contents/MacOS/", "/Programs/cursor/Cursor.exe"],
            Markers = [new Marker("CURSOR_AGENT", "1")],
            IgnorePathContains = ["/Cursor.app/", "/.cursor/", "/Programs/cursor/"],
            IgnoreExePrefixes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["darwin"] = ["/Applications/", "/System/"],
                ["windows"] = WindowsBrowsers,
            },
        },
    ];

    public static AgentRule Of(AgentKind kind) => All.First(r => r.Kind == kind);
}

public static class AgentKindExtensions
{
    public static AgentRule Rule(this AgentKind kind) => AgentRule.Of(kind);
    public static string Id(this AgentKind kind) => kind.ToString().ToLowerInvariant();
    public static string DisplayName(this AgentKind kind) => kind.Rule().Name;

    public static string ShortName(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        _ => "Cursor",
    };

    public static AgentKind? Parse(string id) =>
        Enum.TryParse<AgentKind>(id, ignoreCase: true, out var kind) ? kind : null;
}
