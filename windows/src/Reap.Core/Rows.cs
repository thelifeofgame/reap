namespace Reap.Core;

/// <summary>把判定出的进程整理成列表行和卡片。纯函数，便于测试。</summary>
public static class Rows
{
    private static readonly HashSet<string> Shells = ["zsh", "bash", "sh"];

    /// <summary>
    /// 1. 同一程序拉起的同名子进程（浏览器、Electron 每个标签页一个）和 conhost 并进父进程那一行。
    /// 2. Agent 包在命令外的 shell：有子进程或同组进程时并进去；只剩它时显示里面的命令。
    /// </summary>
    public static List<ProcessItem> Fold(IEnumerable<ProcessItem> input)
    {
        var items = input.OrderBy(i => i.Pid).ToList();
        var index = new Dictionary<int, int>();
        for (var i = 0; i < items.Count; i++) index[items[i].Pid] = i;

        var folded = new HashSet<int>();
        for (var i = 0; i < items.Count; i++)
        {
            int? app = null;
            var at = i;
            var seen = new HashSet<int>();
            while (seen.Add(at) && index.TryGetValue(items[at].Ppid, out var up) && IsHelperOf(items[i], items[up]))
            {
                app = up;
                at = up;
            }
            if (app is not { } target) continue;
            items[target].Helpers.Add(new Helper(items[i].Pid, items[i].StartTime, items[i].Cmd));
            folded.Add(i);
        }
        items = items.Where((_, i) => !folded.Contains(i)).ToList();

        folded.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Marked || Unwrap(items[i]) is not { } inner) continue;
            var wrapper = items[i];
            // Windows 没有进程组，看父子关系；macOS/Linux 看同组。
            var host = Enumerable.Range(0, items.Count).Cast<int?>().FirstOrDefault(j =>
                j != i && !folded.Contains(j!.Value) && Unwrap(items[j.Value]) is null
                && (items[j.Value].Ppid == wrapper.Pid || items[j.Value].Pgid == wrapper.Pgid));
            if (host is { } h)
            {
                items[h].Helpers.Add(new Helper(wrapper.Pid, wrapper.StartTime, wrapper.Cmd));
                folded.Add(i);
            }
            else
            {
                wrapper.Wrapped = inner;
            }
        }
        return items.Where((_, i) => !folded.Contains(i)).ToList();
    }

    private static bool IsHelperOf(ProcessItem child, ProcessItem parent)
    {
        if (child.BareName == "conhost") return true;
        if (child.Exe.Length == 0 || parent.Exe.Length == 0) return false;
        // macOS：应用包内的辅助程序。
        var marker = parent.Exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        if (marker > 0) return child.Exe.StartsWith(parent.Exe[..(marker + 5)], StringComparison.Ordinal);
        // Windows：同一个 exe 拉起的子进程。
        return string.Equals(child.Exe, parent.Exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Claude Code 的包装形如 <c>bash -c ... &amp;&amp; eval '&lt;命令&gt;' &lt; /dev/null &amp;&amp; pwd -P &gt;| &lt;文件&gt;</c>。</summary>
    internal static string? Unwrap(ProcessItem item)
    {
        if (!Shells.Contains(item.BareName)) return null;
        var start = item.Cmd.IndexOf(" eval '", StringComparison.Ordinal);
        if (start < 0) return null;
        var inner = item.Cmd[(start + 7)..];
        var end = inner.LastIndexOf("' < /dev/null", StringComparison.Ordinal);
        if (end < 0) end = inner.LastIndexOf('\'');
        if (end >= 0) inner = inner[..end];
        var text = inner.Replace("'\"'\"'", "'").Replace('\n', ' ');
        return text.Length == 0 ? null : text;
    }

    /// <summary>按 Agent、会话、worktree 分卡片；没有会话 ID 时按工作目录分。最老的卡片在前。</summary>
    public static List<LeftoverGroup> Group(IEnumerable<ProcessItem> items,
                                            Func<AgentKind, string, (string? Project, string? Title)> meta)
    {
        return items
            .GroupBy(i => string.Join('\n', i.Owner.Agent.Id(),
                i.Owner.Session is { } s ? "s:" + s : "d:" + i.Cwd, i.Worktree?.Root ?? ""))
            .Select(g =>
            {
                var first = g.First();
                var info = first.Owner.Session is { } session ? meta(first.Owner.Agent, session) : (null, null);
                var folder = info.Project ?? g.Select(i => i.Cwd).Where(c => c.Length > 0).Select(LastComponent).FirstOrDefault();
                return new LeftoverGroup
                {
                    Id = g.Key,
                    Agent = first.Owner.Agent,
                    Session = first.Owner.Session,
                    Project = folder ?? "未知项目",
                    Title = info.Title,
                    Worktree = first.Worktree,
                    Items = g.OrderBy(i => i.StartTime).ToList(),
                };
            })
            .OrderBy(g => g.OldestStart).ThenBy(g => g.Id, StringComparer.Ordinal)
            .ToList();
    }

    public static string LastComponent(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? path : name;
    }
}
