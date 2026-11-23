using System.Text.Json.Serialization;

namespace Reap.Core;

/// <summary>一个进程在某一刻的样子。格式与 spec/DETECTION.md 的快照一致。</summary>
public sealed class ProcessRecord
{
    public int Pid { get; set; }
    public int Ppid { get; set; }
    public int Pgid { get; set; }
    /// <summary>可执行文件完整路径，读不到时为空串。</summary>
    public string Exe { get; set; } = "";
    public string Cmd { get; set; } = "";
    /// <summary>null 表示读不到。Windows 上变量名由采集端统一转成大写。</summary>
    public Dictionary<string, string>? Env { get; set; }
    public bool Own { get; set; } = true;
    /// <summary>启动时间（Unix 秒）。结束进程前用它确认 PID 没被复用。</summary>
    public double StartTime { get; set; }

    [JsonIgnore]
    public string NormalizedExe => Exe.Replace('\\', '/');

    /// <summary>与 hostNames 比较用的可执行文件名。Windows 去掉 .exe 并转小写；其他平台原样。</summary>
    public string HostName(string platform)
    {
        var source = Exe.Length > 0 ? NormalizedExe : Cmd.Split(' ', 2)[0];
        var name = source[(source.LastIndexOf('/') + 1)..];
        if (platform != "windows") return name;
        name = name.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }
}

public sealed class WatchedGroup
{
    public string Agent { get; set; } = "";
    public string? Session { get; set; }
    public int HostPid { get; set; }
}

public sealed class Snapshot
{
    public string Platform { get; set; } = "windows";
    public int SelfPid { get; set; }
    public List<ProcessRecord> Processes { get; set; } = [];
    /// <summary>宿主 PID → 会话 ID。</summary>
    public Dictionary<int, string> HostSessions { get; set; } = [];
    /// <summary>正在写 Claude 命令输出文件的进程 → 会话 ID。</summary>
    public Dictionary<int, string> OutputSessions { get; set; } = [];
    public Dictionary<int, WatchedGroup> WatchedGroups { get; set; } = [];
}

/// <summary>进程属于哪个 Agent 的哪个会话，以及启动它的宿主是否还活着。</summary>
public sealed record Owner(AgentKind Agent, string? Session, bool Alive);
