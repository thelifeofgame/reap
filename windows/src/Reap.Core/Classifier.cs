namespace Reap.Core;

/// <summary>按 spec/DETECTION.md 判定每个进程的归属。纯函数：只看快照，不碰系统。</summary>
public static class Classifier
{
    /// <param name="Cli">按 hostNames 识别的命令行宿主，可以收养它名下环境不可读的命令；按路径识别的是 App。</param>
    public sealed record Host(AgentKind Agent, bool Cli);

    /// <summary>Windows 路径不区分大小写，比较前统一小写。</summary>
    internal static string PathKey(string path, string platform) =>
        platform == "windows" ? path.ToLowerInvariant() : path;

    /// <summary><paramref name="hostSessions"/> 里有记录的进程也是 Claude 宿主（npm 安装时进程名是 node）。</summary>
    public static Dictionary<int, Host> Hosts(IEnumerable<ProcessRecord> processes, string platform,
                                              IReadOnlyDictionary<int, string>? hostSessions = null,
                                              IReadOnlyList<AgentRule>? rules = null)
    {
        rules ??= AgentRule.All;
        var result = new Dictionary<int, Host>();
        foreach (var p in processes)
        {
            if (hostSessions?.ContainsKey(p.Pid) == true)
            {
                result[p.Pid] = new Host(AgentKind.Claude, true);
                continue;
            }
            var exe = PathKey(p.NormalizedExe, platform);
            var name = p.HostName(platform);
            foreach (var rule in rules)
            {
                if (rule.HostNames.Contains(name))
                {
                    result[p.Pid] = new Host(rule.Kind, true);
                    break;
                }
                if (exe.Length > 0 && rule.HostPathContains.Any(f => exe.Contains(PathKey(f, platform), StringComparison.Ordinal)))
                {
                    result[p.Pid] = new Host(rule.Kind, false);
                    break;
                }
            }
        }
        return result;
    }

    public static bool IsMarked(ProcessRecord p, AgentKind agent) =>
        p.Env is { } env && agent.Rule().Markers.Any(m => m.Matches(env));

    public static Dictionary<int, Owner> Classify(Snapshot snapshot, IReadOnlyList<AgentRule>? rules = null)
    {
        rules ??= AgentRule.All;
        var platform = snapshot.Platform;
        var table = new Dictionary<int, ProcessRecord>();
        foreach (var p in snapshot.Processes) table[p.Pid] = p;
        var selfPid = snapshot.SelfPid;

        int? Parent(int pid) =>
            table.TryGetValue(pid, out var p) && p.Ppid > 0 && p.Ppid != pid && table.ContainsKey(p.Ppid) ? p.Ppid : null;

        List<int> Ancestors(int pid)
        {
            var output = new List<int>();
            var seen = new HashSet<int> { pid };
            var at = pid;
            while (Parent(at) is { } up && seen.Add(up))
            {
                output.Add(up);
                at = up;
            }
            return output;
        }

        var hosts = Hosts(snapshot.Processes, platform, snapshot.HostSessions, rules);
        var hostAgent = hosts.ToDictionary(h => h.Key, h => h.Value.Agent);
        var cliHosts = hosts.Where(h => h.Value.Cli).Select(h => h.Key).ToHashSet();

        int? HostAncestor(int pid, AgentKind kind)
        {
            foreach (var up in Ancestors(pid))
                if (hostAgent.TryGetValue(up, out var k) && k == kind) return up;
            return null;
        }

        // 没有记录的宿主（子 Agent）沿用最近的同类宿主的会话。
        var sessionOf = new Dictionary<int, string>();
        foreach (var (host, kind) in hostAgent)
        {
            if (snapshot.HostSessions.TryGetValue(host, out var own))
            {
                sessionOf[host] = own;
                continue;
            }
            foreach (var up in Ancestors(host))
            {
                if (hostAgent.TryGetValue(up, out var k) && k == kind && snapshot.HostSessions.TryGetValue(up, out var inherited))
                {
                    sessionOf[host] = inherited;
                    break;
                }
            }
        }

        bool Ignored(ProcessRecord p, AgentRule rule)
        {
            var exe = PathKey(p.NormalizedExe, platform);
            if (exe.Length == 0) return false;
            if (rule.IgnorePathContains.Any(f => exe.Contains(PathKey(f, platform), StringComparison.Ordinal))) return true;
            return rule.IgnoreExePrefixes.TryGetValue(platform, out var prefixes)
                && prefixes.Any(f => exe.StartsWith(PathKey(f, platform), StringComparison.Ordinal));
        }

        var candidates = snapshot.Processes
            .Where(p => p.Own && p.Pid != selfPid && !hostAgent.ContainsKey(p.Pid) && !Ancestors(p.Pid).Contains(selfPid))
            .Select(p => p.Pid)
            .Order()
            .ToList();
        var candidateSet = candidates.ToHashSet();

        var owners = new Dictionary<int, Owner>();
        var found = new List<int>();
        // 环境里点名的宿主，进程被挂到别处时用它找回当前会话。
        var named = new Dictionary<int, int>();

        // 1. 环境标记
        foreach (var pid in candidates)
        {
            var p = table[pid];
            if (p.Env is not { } env) continue;
            foreach (var rule in rules)
            {
                if (!rule.Markers.Any(m => m.Matches(env)) || Ignored(p, rule)) continue;
                var kind = rule.Kind;
                string? session = rule.SessionEnv is { } se && env.TryGetValue(se, out var s) && s.Length > 0 ? s : null;
                int? hostPid = rule.HostPidEnv is { } he && env.TryGetValue(he, out var h) && int.TryParse(h, out var hp) ? hp : null;
                var pointsAtHost = hostPid is { } hpid && hostAgent.TryGetValue(hpid, out var hk) && hk == kind;
                if (pointsAtHost) named[pid] = hostPid!.Value;
                owners[pid] = new Owner(kind, session, pointsAtHost || HostAncestor(pid, kind) is not null);
                found.Add(pid);
                break;
            }
        }

        // 2. Claude 命令输出文件
        var claudeRule = AgentKind.Claude.Rule();
        var openClaude = sessionOf.Where(e => hostAgent[e.Key] == AgentKind.Claude).Select(e => e.Value).ToHashSet();
        foreach (var pid in candidates)
        {
            if (owners.ContainsKey(pid) || !snapshot.OutputSessions.TryGetValue(pid, out var session)) continue;
            if (Ignored(table[pid], claudeRule)) continue;
            var alive = HostAncestor(pid, AgentKind.Claude) is not null || openClaude.Contains(session);
            owners[pid] = new Owner(AgentKind.Claude, session, alive);
            found.Add(pid);
        }

        // 3. 记下来的进程组，只用于环境不可读的进程
        foreach (var pid in candidates)
        {
            var p = table[pid];
            if (owners.ContainsKey(pid) || p.Env is not null) continue;
            if (!snapshot.WatchedGroups.TryGetValue(p.Pgid, out var group)) continue;
            if (AgentKindExtensions.Parse(group.Agent) is not { } kind || Ignored(p, kind.Rule())) continue;
            var alive = (hostAgent.TryGetValue(group.HostPid, out var gk) && gk == kind) || HostAncestor(pid, kind) is not null;
            owners[pid] = new Owner(kind, group.Session, alive);
            found.Add(pid);
        }

        // 4. 传播
        var changed = true;
        for (var round = 0; changed && round < 5; round++)
        {
            changed = false;

            for (var i = 0; i < found.Count; i++)
            {
                var owner = owners[found[i]];
                var at = found[i];
                while (Parent(at) is { } up && up > 1 && candidateSet.Contains(up) && !owners.ContainsKey(up)
                       && table[up].Env is null && !Ignored(table[up], owner.Agent.Rule()))
                {
                    owners[up] = owner;
                    found.Add(up);
                    changed = true;
                    at = up;
                }
            }

            foreach (var pid in candidates)
            {
                if (owners.ContainsKey(pid)) continue;
                var p = table[pid];
                Owner? result = null;
                foreach (var up in Ancestors(pid))
                {
                    if (hostAgent.TryGetValue(up, out var kind))
                    {
                        if (cliHosts.Contains(up) && p.Env is null && p.Pgid != table[up].Pgid && !Ignored(p, kind.Rule()))
                            result = new Owner(kind, sessionOf.GetValueOrDefault(up), true);
                        break;
                    }
                    if (owners.TryGetValue(up, out var hit))
                    {
                        if (!Ignored(p, hit.Agent.Rule())) result = hit;
                        break;
                    }
                }
                if (result is null) continue;
                owners[pid] = result;
                found.Add(pid);
                changed = true;
            }
        }

        // 5. 挂在活宿主下的，归到宿主当前的会话
        foreach (var pid in found)
        {
            var owner = owners[pid];
            var host = HostAncestor(pid, owner.Agent);
            if (host is null && named.TryGetValue(pid, out var n) && hostAgent.TryGetValue(n, out var nk) && nk == owner.Agent)
                host = n;
            if (host is not { } h) continue;
            owners[pid] = new Owner(owner.Agent, sessionOf.GetValueOrDefault(h) ?? owner.Session, true);
        }
        return owners;
    }
}
