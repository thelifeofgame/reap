using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using Reap.Core;

namespace Reap.Windows;

public enum DiskKind { Worktree, Cache }

public enum DiskState { Removable, Uncommitted, Busy }

/// <summary>磁盘上可以清理的一项。</summary>
public sealed record DiskItem
{
    public required string Id { get; init; }
    public DiskKind Kind { get; init; }
    public AgentKind? Agent { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public IReadOnlyList<string> Paths { get; init; } = [];
    public DiskState State { get; init; }
    /// <summary>State 为 Busy 时的原因。</summary>
    public string? BusyReason { get; init; }
    /// <summary>删掉的代价，例如“下次安装会重新下载”。</summary>
    public string? Note { get; init; }
    public string? Repository { get; init; }
    public ulong? Bytes { get; init; }
}

public static partial class Disk
{
    /// <summary>正在使用的东西。任何会话或进程还要用的，都不会出现在可删列表里。</summary>
    public sealed class Context
    {
        public List<string> Folders { get; init; } = [];

        public static Context From(ScanResult scan)
        {
            var items = scan.Leftovers.SelectMany(g => g.Items).Concat(scan.Sessions.SelectMany(s => s.Items));
            return new Context
            {
                Folders = items.Select(i => i.Cwd).Concat(scan.Sessions.Select(s => s.Host.Cwd))
                    .Where(f => f.Length > 3).Select(Normalize).ToList(),
            };
        }

        public bool Uses(string path)
        {
            var p = Normalize(path);
            return Folders.Any(f => f == p || f.StartsWith(p + "/", StringComparison.Ordinal));
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static readonly string Home = AgentFiles.Home;
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // MARK: Worktrees

    public static List<DiskItem> Worktrees(Context context)
    {
        var found = new List<(string Path, string? Repository, AgentKind Agent)>();
        foreach (var repository in Repositories(context.Folders))
        {
            var folder = Path.Combine(repository, ".claude", "worktrees");
            if (!Directory.Exists(folder)) continue;
            foreach (var dir in Directory.EnumerateDirectories(folder).Order())
                if (!Path.GetFileName(dir).StartsWith('.')) found.Add((dir, repository, AgentKind.Claude));
        }
        // Codex：~/.codex/worktrees/<id>/<仓库名>
        var codexRoot = Path.Combine(Home, ".codex", "worktrees");
        if (Directory.Exists(codexRoot))
        {
            foreach (var id in Directory.EnumerateDirectories(codexRoot).Order())
            foreach (var dir in Directory.EnumerateDirectories(id))
            {
                if (Path.GetFileName(dir).StartsWith('.') || !File.Exists(Path.Combine(dir, ".git"))) continue;
                found.Add((dir, Worktree.Read(dir)?.Repository, AgentKind.Codex));
            }
        }
        // 每个都要问 git 有没有未提交改动，所以并行。
        return found.AsParallel().AsOrdered().Select(f => WorktreeItem(f.Path, f.Repository, f.Agent, context)).ToList();
    }

    private static DiskItem WorktreeItem(string path, string? repository, AgentKind agent, Context context)
    {
        var name = Path.GetFileName(path);
        var known = Worktree.Read(path);
        var state = DiskState.Removable;
        string? reason = null;
        if (context.Uses(path))
        {
            (state, reason) = (DiskState.Busy, "有会话或进程正在里面运行");
        }
        else if (LockReason(path) is { } lockReason)
        {
            (state, reason) = (DiskState.Busy, lockReason);
        }
        else if (known is null || Git.Path is null)
        {
            state = DiskState.Uncommitted;
        }
        else if (Git.Run(["--no-optional-locks", "-C", path, "status", "--porcelain"]).Output.Length > 0)
        {
            state = DiskState.Uncommitted;
        }

        var repo = repository ?? known?.Repository;
        var parts = new List<string> { Rows.LastComponent(repo ?? path) };
        if (known?.Branch is { } branch && branch != name && branch != "worktree-" + name) parts.Add(branch);
        if (LastTouched(path) is { } touched) parts.Add(Format.Duration((int)(DateTime.UtcNow - touched).TotalSeconds) + "未改动");
        return new DiskItem
        {
            Id = path, Kind = DiskKind.Worktree, Agent = agent, Title = name, Detail = string.Join(" · ", parts),
            Paths = [path], State = state, BusyReason = reason, Repository = repo,
        };
    }

    [GeneratedRegex(@"\(pid (\d+)")]
    private static partial Regex LockPid();

    /// <summary>Agent 工作时会锁住 worktree，锁里写着它的 PID。进程还在就不能删。</summary>
    private static string? LockReason(string path)
    {
        if (Worktree.GitRecords(path) is not { } records) return null;
        string reason;
        try { reason = File.ReadAllText(Path.Combine(records, "locked")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (LockPid().Match(reason) is not { Success: true } m || !int.TryParse(m.Groups[1].Value, out var pid)) return "已被 git 锁定";
        return WindowsProcesses.StartTime(pid) is not null ? "Agent 正在里面工作" : null;
    }

    private static DateTime? LastTouched(string path)
    {
        var files = new List<string> { path };
        if (Worktree.GitRecords(path) is { } records) files.AddRange([Path.Combine(records, "HEAD"), Path.Combine(records, "index")]);
        var times = files.Where(f => File.Exists(f) || Directory.Exists(f)).Select(File.GetLastWriteTimeUtc).ToList();
        return times.Count == 0 ? null : times.Max();
    }

    private static readonly ConcurrentDictionary<string, string> FolderOfProject = new();

    /// <summary>所有 Claude 会话工作过的仓库主目录。项目目录名不能反推路径，但会话文件每行都写了 cwd。</summary>
    private static List<string> Repositories(IEnumerable<string> extra)
    {
        var folders = new HashSet<string>(extra, StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(Home, ".claude", "projects");
        if (Directory.Exists(root))
        {
            foreach (var project in Directory.EnumerateDirectories(root))
            {
                if (FolderOfProject.TryGetValue(project, out var known))
                {
                    folders.Add(known);
                    continue;
                }
                var file = Directory.EnumerateFiles(project, "*.jsonl").FirstOrDefault();
                if (file is null) continue;
                string head;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var buffer = new byte[Math.Min(256 * 1024, stream.Length)];
                    head = System.Text.Encoding.UTF8.GetString(buffer, 0, stream.Read(buffer, 0, buffer.Length));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (AgentFiles.FirstString(head, "cwd") is not { } cwd) continue;
                FolderOfProject[project] = cwd;
                folders.Add(cwd);
            }
        }

        var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var at = folder;
            while (!string.IsNullOrEmpty(at))
            {
                var git = Path.Combine(at, ".git");
                if (Directory.Exists(git))
                {
                    repositories.Add(at);
                    break;
                }
                if (File.Exists(git))
                {
                    if (Worktree.Read(at)?.Repository is { } main) repositories.Add(main);
                    break;
                }
                at = Path.GetDirectoryName(at) ?? "";
            }
        }
        return repositories.Order().ToList();
    }

    // MARK: 缓存

    private sealed record Cache(string Name, string[] Paths, string Note);

    /// <summary>只列工具下次用到时会自动重新下载的缓存。需要手动重装的（例如 Playwright 浏览器）不在其列。</summary>
    private static Cache[] KnownCaches() =>
    [
        new("npm 缓存", [Path.Combine(LocalAppData, "npm-cache", "_cacache")], "下次安装时 npm 会重新下载。"),
        new("pnpm 存储", [Path.Combine(LocalAppData, "pnpm", "store"), Path.Combine(LocalAppData, "pnpm-cache")], "已有项目不受影响，下次安装时重新下载。"),
        new("Yarn 缓存", [Path.Combine(LocalAppData, "Yarn", "Cache"), Path.Combine(LocalAppData, "Yarn", "Berry", "cache")], "下次安装时 Yarn 会重新下载。"),
        new("Bun 缓存", [Path.Combine(Home, ".bun", "install", "cache")], "下次安装时 Bun 会重新下载。"),
        new("Gradle 缓存", [Path.Combine(Home, ".gradle", "caches")], "下次构建时 Gradle 会重新下载依赖。"),
        new("NuGet 包缓存", [Path.Combine(Home, ".nuget", "packages")], "下次还原时 NuGet 会重新下载。"),
        new("pip 缓存", [Path.Combine(LocalAppData, "pip", "Cache")], "下次安装时 pip 会重新下载。"),
        new("uv 缓存", [Path.Combine(LocalAppData, "uv", "cache")], "下次安装时 uv 会重新下载。"),
        new("Cargo 注册表", [Path.Combine(Home, ".cargo", "registry")], "下次构建时 Cargo 会重新下载 crate。"),
        new("Go 构建缓存", [Path.Combine(LocalAppData, "go-build")], "下次构建时重新编译。"),
        new("Electron 下载", [Path.Combine(LocalAppData, "electron", "Cache")], "项目安装时会重新下载 Electron。"),
    ];

    /// <summary>小于这个大小的缓存不值得列出。</summary>
    public const ulong SmallestCache = 100UL * 1024 * 1024;

    public static List<DiskItem> Caches() => KnownCaches()
        .Select(c => (Cache: c, Paths: c.Paths.Where(Directory.Exists).ToList()))
        .Where(c => c.Paths.Count > 0)
        .AsParallel()
        .Select(c => new DiskItem
        {
            Id = "cache:" + c.Cache.Name, Kind = DiskKind.Cache, Title = c.Cache.Name,
            Detail = string.Join(", ", c.Paths.Select(Abbreviate)), Paths = c.Paths, Note = c.Cache.Note,
            Bytes = c.Paths.Aggregate(0UL, (sum, p) => sum + Measure(p)),
        })
        .Where(i => i.Bytes >= SmallestCache)
        .OrderByDescending(i => i.Bytes)
        .ToList();

    // MARK: 测量与删除

    public static ulong Measure(string path)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try { return (ulong)new DirectoryInfo(path).EnumerateFiles("*", options).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>删除前再确认一次没人在用。返回错误说明，成功返回 null。</summary>
    public static string? Remove(DiskItem item, Context context)
    {
        switch (item.Kind)
        {
            case DiskKind.Cache:
                return Delete(item.Paths);
            default:
                var path = item.Paths.FirstOrDefault();
                if (path is null) return "没有可删除的内容";
                var now = WorktreeItem(path, item.Repository, item.Agent ?? AgentKind.Claude, context);
                if (now.State != item.State) return "状态在列表生成后变了，没有删除任何东西。";
                var records = Worktree.GitRecords(path);
                switch (item.State)
                {
                    case DiskState.Busy:
                        return item.BusyReason;
                    case DiskState.Removable:
                        if (Git.Path is null || item.Repository is null) return "找不到 git";
                        // 已退出的 Agent 留下的锁会挡住 git。
                        if (records is not null && File.Exists(Path.Combine(records, "locked")))
                            Git.Run(["-C", item.Repository, "worktree", "unlock", path]);
                        // 里面有改动或未跟踪文件时 git 会拒绝。分支保留。
                        var result = Git.Run(["-C", item.Repository, "worktree", "remove", path]);
                        return result.ExitCode == 0 ? null : result.Error;
                    default:
                        try
                        {
                            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
                        {
                            return e.Message;
                        }
                        // 目录已进回收站，这里只清掉 git 的记录；分支保留。
                        if (Git.Path is not null && item.Repository is not null && records is not null)
                        {
                            try { File.Delete(Path.Combine(records, "locked")); }
                            catch (IOException) { }
                            Git.Run(["-C", item.Repository, "worktree", "prune"]);
                        }
                        return null;
                }
        }
    }

    private static string? Delete(IEnumerable<string> paths)
    {
        string? failure = null;
        foreach (var path in paths)
        {
            // 只删家目录和 LocalAppData 下面的东西。
            var p = Normalize(path);
            if (!p.StartsWith(Normalize(Home) + "/", StringComparison.Ordinal) &&
                !p.StartsWith(Normalize(LocalAppData) + "/", StringComparison.Ordinal)) continue;
            try { Directory.Delete(path, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failure ??= e.Message; }
        }
        return failure;
    }

    public static string Abbreviate(string path) =>
        path.StartsWith(Home, StringComparison.OrdinalIgnoreCase) ? "~" + path[Home.Length..] : path;
}

public static class Git
{
    public static readonly string? Path = Find();

    private static string? Find()
    {
        var names = new[] { "git.exe", "git" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        foreach (var name in names)
        {
            var candidate = System.IO.Path.Combine(dir.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        var program = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
        return File.Exists(program) ? program : null;
    }

    public sealed record Result(int ExitCode, string Output, string Error);

    public static Result Run(IEnumerable<string> arguments)
    {
        if (Path is null) return new Result(-1, "", "找不到 git");
        var info = new ProcessStartInfo(Path)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info);
        if (process is null) return new Result(-1, "", "无法启动 git");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new Result(process.ExitCode, output.Result.Trim(), error.Trim());
    }
}
