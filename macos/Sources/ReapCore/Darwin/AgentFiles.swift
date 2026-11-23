import Foundation

/// 读 Agent 自己写在磁盘上的会话记录。这些都不是公开接口，所以每个字段都按可缺失处理。
enum AgentFiles {
    static let home = FileManager.default.homeDirectoryForCurrentUser.path

    // MARK: Claude Code

    struct ClaudeRecord {
        let sessionID: String?
        let cwd: String?
        let status: String?
        let statusSince: Date?
        let name: String?
    }

    /// ~/.claude/sessions 下有记录的 PID。
    static func claudeRecordPids() -> [Int32] {
        let files = (try? FileManager.default.contentsOfDirectory(atPath: "\(home)/.claude/sessions")) ?? []
        return files.compactMap { $0.hasSuffix(".json") ? Int32($0.dropLast(5)) : nil }
    }

    /// ~/.claude/sessions/<pid>.json。PID 会复用，所以要核对记录里的启动时间就是这个进程的。
    static func claudeRecord(pid: Int32, startTime: Double) -> ClaudeRecord? {
        guard let data = FileManager.default.contents(atPath: "\(home)/.claude/sessions/\(pid).json"),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let startedAt = number(json["startedAt"]).map { $0 / 1000 }
        let procStart = json["procStart"] as? String
        let matches = startedAt.map { abs($0 - startTime) < 10 } ?? false
            || procStart.map { squeeze($0) == lstart(startTime) } ?? false
        guard matches else { return nil }
        return ClaudeRecord(
            sessionID: json["sessionId"] as? String,
            cwd: json["cwd"] as? String,
            status: json["status"] as? String,
            statusSince: number(json["statusUpdatedAt"]).map { Date(timeIntervalSince1970: $0 / 1000) },
            name: (json["name"] as? String).flatMap { $0.isEmpty ? nil : $0 }
        )
    }

    /// ps 的 lstart 格式，UTC、英文，Claude Code 记录里用的就是这个。
    private static func lstart(_ time: Double) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "EEE MMM d HH:mm:ss yyyy"
        return formatter.string(from: Date(timeIntervalSince1970: time))
    }

    private static func squeeze(_ text: String) -> String {
        text.split(separator: " ").joined(separator: " ")
    }

    private static func number(_ value: Any?) -> Double? {
        if let n = value as? NSNumber { return n.doubleValue }
        if let s = value as? String { return Double(s) }
        return nil
    }

    /// Claude Code 把每条后台命令的输出写到 .../claude-<uid>/<项目>/<会话>/tasks/<命令>.output。
    private static let outputPattern = #//claude-\d+/[^/]+/([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})/tasks/[^/]+$/#

    static func claudeOutputSession(pid: Int32) -> String? {
        for path in DarwinProcesses.outputPaths(of: pid) {
            if let match = path.firstMatch(of: outputPattern) { return String(match.1) }
        }
        return nil
    }

    struct SessionMeta: Sendable {
        var cwd: String?
        var title: String?
    }

    private static let cacheLock = NSLock()
    nonisolated(unsafe) private static var cache: [String: SessionMeta] = [:]

    /// 会话的工作目录和标题，从 ~/.claude/projects/*/<id>.jsonl 读。
    static func claudeMeta(session: String) -> SessionMeta {
        cached(key: "claude:" + session) {
            let fm = FileManager.default
            let root = home + "/.claude/projects"
            for dir in (try? fm.contentsOfDirectory(atPath: root)) ?? [] {
                let file = "\(root)/\(dir)/\(session).jsonl"
                guard fm.fileExists(atPath: file) else { continue }
                let head = readHead(file, bytes: 256 * 1024)
                var meta = SessionMeta()
                meta.cwd = firstString(in: head, key: "cwd")
                let tail = readTail(file, bytes: 2 * 1024 * 1024)
                meta.title = lastString(in: tail, key: "customTitle") ?? lastString(in: tail, key: "aiTitle")
                return meta
            }
            return nil
        }
    }

    // MARK: Codex

    /// ~/.codex/sessions/YYYY/MM/DD/rollout-<时间>-<线程 ID>.jsonl，第一行 session_meta 里有 cwd。
    static func codexMeta(thread: String) -> SessionMeta {
        cached(key: "codex:" + thread) {
            let root = home + "/.codex/sessions"
            guard let walker = FileManager.default.enumerator(atPath: root) else { return nil }
            while let item = walker.nextObject() as? String {
                guard item.hasSuffix("-\(thread).jsonl") else { continue }
                let head = readHead(root + "/" + item, bytes: 64 * 1024)
                return SessionMeta(cwd: firstString(in: head, key: "cwd"), title: nil)
            }
            return nil
        }
    }

    // MARK: Helpers

    /// 只缓存找到的结果：新会话的文件可能稍后才出现。
    private static func cached(key: String, _ read: () -> SessionMeta?) -> SessionMeta {
        cacheLock.lock()
        if let hit = cache[key] {
            cacheLock.unlock()
            return hit
        }
        cacheLock.unlock()
        guard let meta = read() else { return SessionMeta() }
        cacheLock.lock()
        cache[key] = meta
        cacheLock.unlock()
        return meta
    }

    private static func readHead(_ path: String, bytes: Int) -> String {
        guard let handle = FileHandle(forReadingAtPath: path) else { return "" }
        defer { try? handle.close() }
        return String(decoding: (try? handle.read(upToCount: bytes)) ?? Data(), as: UTF8.self)
    }

    /// 会话文件可能有几百 MB，只读结尾。
    private static func readTail(_ path: String, bytes: Int) -> String {
        guard let handle = FileHandle(forReadingAtPath: path) else { return "" }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        try? handle.seek(toOffset: size > UInt64(bytes) ? size - UInt64(bytes) : 0)
        return String(decoding: (try? handle.readToEnd()) ?? Data(), as: UTF8.self)
    }

    private static func stringPattern(_ key: String) -> Regex<(Substring, Substring)> {
        // 键名只来自本文件里的常量，不含正则特殊字符。
        try! Regex("\"\(key)\":(\"(?:[^\"\\\\]|\\\\.)*\")")
    }

    private static func decode(_ literal: Substring) -> String? {
        (try? JSONDecoder().decode(String.self, from: Data(literal.utf8))).flatMap { $0.isEmpty ? nil : $0 }
    }

    static func firstString(in text: String, key: String) -> String? {
        text.firstMatch(of: stringPattern(key)).flatMap { decode($0.1) }
    }

    static func lastString(in text: String, key: String) -> String? {
        text.matches(of: stringPattern(key)).last.flatMap { decode($0.1) }
    }
}
