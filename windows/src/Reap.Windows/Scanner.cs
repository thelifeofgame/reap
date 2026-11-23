using System.Diagnostics;
using Reap.Core;

namespace Reap.Windows;

public static class Scanner
{
    public static ScanResult Scan()
    {
        var watch = Stopwatch.StartNew();
        var details = WindowsProcesses.List();
        var processes = details.Select(d => d.Record).ToList();
        var cwdOf = details.ToDictionary(d => d.Record.Pid, d => d.Cwd);
        var table = processes.ToDictionary(p => p.Pid);

        // 先读会话记录再认宿主：npm 安装的 Claude Code 进程名是 node.exe，只能靠记录认出来。
        var records = new Dictionary<int, AgentFiles.ClaudeRecord>();
        var hostSessions = new Dictionary<int, string>();
        foreach (var pid in AgentFiles.ClaudeRecordPids())
        {
            if (!table.TryGetValue(pid, out var p) || AgentFiles.Claude(pid, p.StartTime) is not { } record) continue;
            records[pid] = record;
            if (record.SessionId is { } session) hostSessions[pid] = session;
        }
        var hosts = Classifier.Hosts(processes, "windows", hostSessions);

        // Windows 上没有进程组，也不方便查别的进程打开了哪些文件，所以输出文件和进程组两条线索为空。
        var snapshot = new Snapshot
        {
            Platform = "windows",
            SelfPid = Environment.ProcessId,
            Processes = processes,
            HostSessions = hostSessions,
        };
        var owners = Classifier.Classify(snapshot);

        var items = Rows.Fold(owners.Select(o =>
        {
            var p = table[o.Key];
            return new ProcessItem
            {
                Pid = p.Pid, Ppid = p.Ppid, Pgid = p.Pgid, StartTime = p.StartTime, Exe = p.Exe, Cmd = p.Cmd,
                Owner = o.Value, Marked = Classifier.IsMarked(p, o.Value.Agent),
            };
        }));

        var ports = Ports.Listening();
        foreach (var item in items)
        {
            item.Memory = item.AllPids.Aggregate(0UL, (sum, pid) => sum + WindowsProcesses.Memory(pid));
            item.Ports = item.AllPids.SelectMany(pid => ports.GetValueOrDefault(pid) ?? []).Distinct().Order().ToList();
            item.Cwd = cwdOf.GetValueOrDefault(item.Pid) ?? "";
            item.Worktree = Worktree.Containing(item.Cwd);
        }

        var leftovers = Rows.Group(items.Where(i => !i.Owner.Alive), (agent, session) =>
        {
            var meta = agent switch
            {
                AgentKind.Claude => AgentFiles.ClaudeMeta(session),
                AgentKind.Codex => AgentFiles.CodexMeta(session),
                _ => new AgentFiles.SessionMeta(null, null),
            };
            return (meta.Cwd is { } cwd ? Rows.LastComponent(cwd) : null, meta.Title);
        });

        var alive = items.Where(i => i.Owner.Alive && i.Age() >= 60).ToList();
        return new ScanResult
        {
            Leftovers = leftovers,
            Sessions = Sessions(table, hosts, records, hostSessions, cwdOf, alive),
            ScannedAt = DateTimeOffset.Now,
            Duration = watch.Elapsed,
        };
    }

    private static List<ActiveSession> Sessions(Dictionary<int, ProcessRecord> table, Dictionary<int, Classifier.Host> hosts,
                                                Dictionary<int, AgentFiles.ClaudeRecord> records, Dictionary<int, string> hostSessions,
                                                Dictionary<int, string> cwdOf, List<ProcessItem> alive)
    {
        List<int> Ancestors(int pid)
        {
            var output = new List<int>();
            var seen = new HashSet<int> { pid };
            var at = pid;
            while (table.TryGetValue(at, out var p) && p.Ppid > 0 && table.ContainsKey(p.Ppid) && seen.Add(p.Ppid))
            {
                output.Add(p.Ppid);
                at = p.Ppid;
            }
            return output;
        }

        // 只列命令行宿主，且不列子 Agent（它们属于启动它们的那个会话）。
        var tops = hosts.Where(h => h.Value.Cli && !Ancestors(h.Key).Any(a => hosts.TryGetValue(a, out var up) && up.Cli && up.Agent == h.Value.Agent))
            .ToDictionary(h => h.Key, h => h.Value);
        var hostOfSession = new Dictionary<string, int>();
        foreach (var (pid, r) in records)
            if (r.SessionId is { } s) hostOfSession.TryAdd(s, pid);

        var byHost = new Dictionary<int, List<ProcessItem>>();
        foreach (var item in alive)
        {
            var top = Ancestors(item.Pid).LastOrDefault(a => tops.TryGetValue(a, out var t) && t.Agent == item.Owner.Agent);
            int? host = top != 0 ? top : null;
            if (host is null && item.Owner.Session is { } s && hostOfSession.TryGetValue(s, out var bySession)) host = bySession;
            if (host is not { } h) continue;
            if (!byHost.TryGetValue(h, out var list)) byHost[h] = list = [];
            list.Add(item);
        }

        var sessions = new List<ActiveSession>();
        foreach (var (pid, host) in tops)
        {
            var p = table[pid];
            var record = records.GetValueOrDefault(pid);
            var cwd = record?.Cwd ?? cwdOf.GetValueOrDefault(pid) ?? "";
            var hostItem = new ProcessItem
            {
                Pid = p.Pid, Ppid = p.Ppid, Pgid = p.Pgid, StartTime = p.StartTime, Exe = p.Exe, Cmd = p.Cmd,
                Owner = new Owner(host.Agent, hostSessions.GetValueOrDefault(pid), true),
                Memory = WindowsProcesses.Memory(pid), Cwd = cwd,
            };
            var managed = Ancestors(pid).Any(a => hosts.TryGetValue(a, out var up) && !up.Cli && up.Agent == host.Agent);
            var (status, other) = record?.Status switch
            {
                "busy" => (SessionStatus.Busy, (string?)null),
                "idle" => (SessionStatus.Idle, null),
                null => (SessionStatus.Unknown, null),
                var s => (SessionStatus.Other, s),
            };
            var title = record?.Name ?? (record?.SessionId is { } sid ? AgentFiles.ClaudeMeta(sid).Title : null);
            sessions.Add(new ActiveSession
            {
                Host = hostItem,
                Agent = host.Agent,
                Session = record?.SessionId,
                Project = managed || cwd.Length <= 3 ? host.Agent.DisplayName() + (managed ? " App" : "") : Rows.LastComponent(cwd),
                Title = title,
                Status = status,
                OtherStatus = other,
                StatusSince = record?.StatusSince,
                Managed = managed,
                Items = (byHost.GetValueOrDefault(pid) ?? []).OrderBy(i => i.StartTime).ToList(),
            });
        }
        // 空闲最久的在前，其次是运行最久的。
        return sessions
            .OrderByDescending(s => s.IdleSeconds() ?? -1)
            .ThenBy(s => s.Host.StartTime)
            .ToList();
    }
}
