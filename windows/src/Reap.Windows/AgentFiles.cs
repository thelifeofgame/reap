using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reap.Windows;

/// <summary>读 Agent 自己写在磁盘上的会话记录。这些都不是公开接口，每个字段都按可缺失处理。</summary>
public static partial class AgentFiles
{
    public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public sealed record ClaudeRecord(string? SessionId, string? Cwd, string? Status, DateTimeOffset? StatusSince, string? Name);

    /// <summary>~/.claude/sessions 下有记录的 PID。</summary>
    public static IEnumerable<int> ClaudeRecordPids()
    {
        var dir = Path.Combine(Home, ".claude", "sessions");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid)) yield return pid;
    }

    /// <summary>~/.claude/sessions/&lt;pid&gt;.json。PID 会复用，所以要核对记录里的启动时间就是这个进程的。</summary>
    public static ClaudeRecord? Claude(int pid, double startTime)
    {
        var file = Path.Combine(Home, ".claude", "sessions", $"{pid}.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var startedAt = Number(root, "startedAt") / 1000;
            if (startedAt is not { } s || Math.Abs(s - startTime) >= 10) return null;
            var since = Number(root, "statusUpdatedAt") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : (DateTimeOffset?)null;
            return new ClaudeRecord(Text(root, "sessionId"), Text(root, "cwd"), Text(root, "status"), since,
                Text(root, "name") is { Length: > 0 } name ? name : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d) ? d : null;
    }

    public sealed record SessionMeta(string? Cwd, string? Title);

    private static readonly ConcurrentDictionary<string, SessionMeta> Cache = new();

    /// <summary>会话的工作目录和标题，从 ~/.claude/projects/*/&lt;id&gt;.jsonl 读。</summary>
    public static SessionMeta ClaudeMeta(string session) => Cached("claude:" + session, () =>
    {
        var root = Path.Combine(Home, ".claude", "projects");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(dir, session + ".jsonl");
            if (!File.Exists(file)) continue;
            var head = ReadHead(file, 256 * 1024);
            var tail = ReadTail(file, 2 * 1024 * 1024);
            return new SessionMeta(FirstString(head, "cwd"), LastString(tail, "customTitle") ?? LastString(tail, "aiTitle"));
        }
        return null;
    });

    /// <summary>~/.codex/sessions/年/月/日/rollout-&lt;时间&gt;-&lt;线程 ID&gt;.jsonl，第一行 session_meta 里有 cwd。</summary>
    public static SessionMeta CodexMeta(string thread) => Cached("codex:" + thread, () =>
    {
        var root = Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(root)) return null;
        var file = Directory.EnumerateFiles(root, $"rollout-*-{thread}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
        return file is null ? null : new SessionMeta(FirstString(ReadHead(file, 64 * 1024), "cwd"), null);
    });

    /// <summary>只缓存找到的结果：新会话的文件可能稍后才出现。</summary>
    private static SessionMeta Cached(string key, Func<SessionMeta?> read)
    {
        if (Cache.TryGetValue(key, out var hit)) return hit;
        SessionMeta? meta;
        try { meta = read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { meta = null; }
        if (meta is null) return new SessionMeta(null, null);
        Cache[key] = meta;
        return meta;
    }

    private static string ReadHead(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(bytes, stream.Length)];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>会话文件可能有几百 MB，只读结尾。</summary>
    private static string ReadTail(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - bytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static Regex Pattern(string key) => new($"\"{Regex.Escape(key)}\":(\"(?:[^\"\\\\]|\\\\.)*\")");

    private static string? Decode(string literal)
    {
        try { return JsonSerializer.Deserialize<string>(literal) is { Length: > 0 } s ? s : null; }
        catch (JsonException) { return null; }
    }

    public static string? FirstString(string text, string key) =>
        Pattern(key).Match(text) is { Success: true } m ? Decode(m.Groups[1].Value) : null;

    public static string? LastString(string text, string key) =>
        Pattern(key).Matches(text) is { Count: > 0 } all ? Decode(all[^1].Groups[1].Value) : null;
}
