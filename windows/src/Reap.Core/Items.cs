namespace Reap.Core;

public sealed record Helper(int Pid, double StartTime, string Cmd);

/// <summary>列表里的一行：一个进程，以及合并进来的子进程。</summary>
public sealed class ProcessItem
{
    public int Pid { get; init; }
    public int Ppid { get; init; }
    public int Pgid { get; init; }
    public double StartTime { get; init; }
    public string Exe { get; init; } = "";
    public string Cmd { get; init; } = "";
    public required Owner Owner { get; init; }
    /// <summary>环境里带 Agent 标记的是命令本身，不会被当成外层 shell 折叠。</summary>
    public bool Marked { get; init; }
    public string Cwd { get; set; } = "";
    public List<int> Ports { get; set; } = [];
    public ulong Memory { get; set; }
    public List<Helper> Helpers { get; } = [];
    /// <summary>Agent 包在命令外面的 shell：显示里面真正的命令。</summary>
    public string? Wrapped { get; set; }
    public Worktree? Worktree { get; set; }

    public int Age(DateTimeOffset? now = null) =>
        Math.Max(0, (int)((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - StartTime));

    public string ExecutableName
    {
        get
        {
            var source = (Exe.Length > 0 ? Exe : Cmd.Split(' ', 2)[0]).Replace('\\', '/');
            return source[(source.LastIndexOf('/') + 1)..];
        }
    }

    /// <summary>不带 .exe、小写的程序名，用来识别 shell。</summary>
    public string BareName
    {
        get
        {
            var name = ExecutableName.ToLowerInvariant();
            return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
        }
    }

    /// <summary>给人看的命令：程序名加参数，去掉长路径。</summary>
    public string DisplayCommand
    {
        get
        {
            if (Wrapped is not null) return Wrapped;
            var cmd = Cmd.Trim();
            string args;
            if (cmd.StartsWith('"'))
            {
                var end = cmd.IndexOf('"', 1);
                args = end > 0 ? cmd[(end + 1)..] : "";
            }
            else if (Exe.Length > 0 && cmd.StartsWith(Exe, StringComparison.OrdinalIgnoreCase))
            {
                args = cmd[Exe.Length..];
            }
            else
            {
                var space = cmd.IndexOf(' ');
                args = space > 0 ? cmd[space..] : "";
            }
            return (ExecutableName + args).Trim();
        }
    }

    public IEnumerable<int> AllPids => Helpers.Select(h => h.Pid).Prepend(Pid);
}

/// <summary>一个已关闭会话在一个位置留下的东西：列表里的一张卡片。</summary>
public sealed class LeftoverGroup
{
    public required string Id { get; init; }
    public AgentKind Agent { get; init; }
    public string? Session { get; init; }
    public string Project { get; init; } = "";
    public string? Title { get; init; }
    public Worktree? Worktree { get; init; }
    public List<ProcessItem> Items { get; set; } = [];

    public ulong Memory => Items.Aggregate(0UL, (sum, i) => sum + i.Memory);
    public int ProcessCount => Items.Sum(i => 1 + i.Helpers.Count);
    public double OldestStart => Items.Count == 0 ? 0 : Items.Min(i => i.StartTime);
    public string? ShortSession => Session is null ? null : Session[..Math.Min(8, Session.Length)];
}

public enum SessionStatus { Busy, Idle, Unknown, Other }

/// <summary>仍在运行的 Agent 会话。</summary>
public sealed class ActiveSession
{
    public required ProcessItem Host { get; init; }
    public AgentKind Agent { get; init; }
    public string? Session { get; init; }
    public string Project { get; init; } = "";
    public string? Title { get; init; }
    public SessionStatus Status { get; init; }
    public string? OtherStatus { get; init; }
    public DateTimeOffset? StatusSince { get; init; }
    /// <summary>宿主是 App 的后台进程，结束它等于关掉整个 App，不提供结束按钮。</summary>
    public bool Managed { get; init; }
    /// <summary>已运行超过一分钟的子进程。</summary>
    public List<ProcessItem> Items { get; init; } = [];

    public ulong Memory => Items.Aggregate(Host.Memory, (sum, i) => sum + i.Memory);

    public int? IdleSeconds(DateTimeOffset? now = null) =>
        Status == SessionStatus.Idle && StatusSince is { } since
            ? Math.Max(0, (int)((now ?? DateTimeOffset.UtcNow) - since).TotalSeconds)
            : null;
}

public sealed class ScanResult
{
    public List<LeftoverGroup> Leftovers { get; init; } = [];
    public List<ActiveSession> Sessions { get; init; } = [];
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; init; }
}
