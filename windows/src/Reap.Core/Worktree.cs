namespace Reap.Core;

/// <summary>仓库主目录以外的 git worktree。</summary>
public sealed record Worktree(string Root, string Name, string? Branch, string? Repository, bool Missing)
{
    /// <summary><paramref name="folder"/> 所在的 worktree；在主目录里或不在仓库里时返回 null。</summary>
    public static Worktree? Containing(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Path.IsPathRooted(folder)) return null;
        var at = folder.TrimEnd('/', '\\');
        while (!string.IsNullOrEmpty(at))
        {
            var git = Path.Combine(at, ".git");
            // 主目录里 .git 是目录，worktree 里是一个指回仓库的文件。
            if (Directory.Exists(git)) return null;
            if (File.Exists(git)) return Read(at);
            at = Path.GetDirectoryName(at) ?? "";
        }
        var normalized = folder.Replace('\\', '/');
        var marker = normalized.IndexOf("/.claude/worktrees/", StringComparison.Ordinal);
        if (!Directory.Exists(folder) && marker > 0)
        {
            var rest = normalized[(marker + "/.claude/worktrees/".Length)..];
            var name = rest.Split('/')[0];
            if (name.Length == 0) return null;
            var repository = folder[..marker];
            return new Worktree(Path.Combine(repository, ".claude", "worktrees", name), name, null, repository, true);
        }
        return null;
    }

    public static Worktree? Read(string root)
    {
        if (GitRecords(root) is not { } records) return null;
        var normalized = records.Replace('\\', '/');
        var marker = normalized.IndexOf("/.git/worktrees/", StringComparison.Ordinal);
        if (marker < 0) return null;
        string? branch = null;
        try
        {
            var head = File.ReadAllText(Path.Combine(records, "HEAD")).Trim();
            if (head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) branch = head["ref: refs/heads/".Length..];
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var name = Path.GetFileName(root.TrimEnd('/', '\\'));
        return new Worktree(root, name, branch, records[..marker], false);
    }

    /// <summary>git 记录这个 worktree 的目录：&lt;仓库&gt;/.git/worktrees/&lt;名字&gt;。</summary>
    public static string? GitRecords(string root)
    {
        string text;
        try { text = File.ReadAllText(Path.Combine(root, ".git")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
        if (line is null) return null;
        var path = line["gitdir:".Length..].Trim();
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
    }

    /// <summary>分支名与目录名重复时只显示目录名。</summary>
    public string Label => Branch is null || Branch == Name || Branch == "worktree-" + Name ? Name : $"{Name} · {Branch}";
}
