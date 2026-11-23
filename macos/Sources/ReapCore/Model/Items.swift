import Foundation

/// 列表里的一行：一个进程，以及合并进来的辅助进程。
public struct ProcessItem: Identifiable, Hashable, Sendable {
    public struct Helper: Hashable, Sendable {
        public let pid: Int32
        public let startTime: Double
        public let cmd: String
    }

    public let pid: Int32
    public let ppid: Int32
    public let pgid: Int32
    public let startTime: Double
    public let exe: String
    public let cmd: String
    public let owner: Owner
    /// 环境是否带了 Agent 标记。带标记的进程是命令本身，不会被当成外层 shell 折叠。
    public let marked: Bool
    public var cwd: String = ""
    public var ports: [Int] = []
    public var memory: UInt64 = 0
    public var helpers: [Helper] = []
    /// Claude Code 包在命令外面的 shell：显示里面真正的命令。
    public var wrapped: String?
    public var worktree: Worktree?

    public var id: Int32 { pid }

    public func age(now: Date = Date()) -> Int {
        max(0, Int(now.timeIntervalSince1970 - startTime))
    }

    public var executableName: String {
        let name = ((exe.isEmpty ? cmd.split(separator: " ").first.map(String.init) ?? cmd : exe) as NSString).lastPathComponent
        return name == "Python" ? "python" : name
    }

    /// 给人看的命令：可执行文件名加参数，去掉长路径。
    public var displayCommand: String {
        if let wrapped { return wrapped }
        let parts = cmd.split(separator: " ", maxSplits: 1).map(String.init)
        let args = parts.count > 1 ? " " + parts[1] : ""
        if !exe.isEmpty, cmd.hasPrefix(exe) {
            return executableName + cmd.dropFirst(exe.count)
        }
        return executableName + args
    }

    /// 是某个 .app 的主程序时，返回 "/…/X.app/"。
    public var bundleRoot: String? {
        guard let marker = exe.range(of: ".app/Contents/MacOS/") else { return nil }
        return String(exe[..<marker.lowerBound]) + ".app/"
    }

    /// 来自 Agent 插件目录的进程，通常是会话自己起的 MCP 服务器。
    public var plugin: String? {
        guard let folder = cmd.range(of: "/.claude/plugins/") else { return nil }
        let parts = cmd[folder.upperBound...].split(separator: " ", maxSplits: 1).first?.split(separator: "/") ?? []
        return parts.count >= 3 && parts[0] == "cache" ? String(parts[2]) : "plugin"
    }

    public var allPids: [Int32] { [pid] + helpers.map(\.pid) }
}

/// 一个已关闭会话在一个位置留下的东西：列表里的一张卡片。
public struct LeftoverGroup: Identifiable, Hashable, Sendable {
    public let id: String
    public let agent: AgentKind
    public let session: String?
    public let project: String
    public let title: String?
    public let worktree: Worktree?
    public var items: [ProcessItem]

    public var memory: UInt64 { items.reduce(0) { $0 + $1.memory } }
    public var processCount: Int { items.reduce(0) { $0 + 1 + $1.helpers.count } }
    public var oldestStart: Double { items.map(\.startTime).min() ?? 0 }
    public var shortSession: String? { session.map { String($0.prefix(8)) } }
}

/// 仍在运行的 Agent 会话。
public struct ActiveSession: Identifiable, Hashable, Sendable {
    public enum Status: Hashable, Sendable {
        case busy, idle, unknown
        case other(String)
    }

    public let host: ProcessItem
    public let agent: AgentKind
    public let session: String?
    public let project: String
    public let title: String?
    public let status: Status
    public let statusSince: Date?
    /// 宿主是 App 的后台进程（例如 ChatGPT 里的 Codex），结束它等于关掉整个 App，不提供结束按钮。
    public let managed: Bool
    /// 已运行超过一分钟的子进程。
    public var items: [ProcessItem]

    public var id: Int32 { host.pid }
    public var memory: UInt64 { host.memory + items.reduce(0) { $0 + $1.memory } }

    public func idleSeconds(now: Date = Date()) -> Int? {
        guard status == .idle, let since = statusSince else { return nil }
        return max(0, Int(now.timeIntervalSince(since)))
    }
}

public struct ScanResult: Sendable {
    public var leftovers: [LeftoverGroup] = []
    public var sessions: [ActiveSession] = []
    public var scannedAt = Date()
    public var duration: TimeInterval = 0

    public init() {}

    public var leftoverMemory: UInt64 { leftovers.reduce(0) { $0 + $1.memory } }
    public var leftoverCount: Int { leftovers.reduce(0) { $0 + $1.processCount } }
}
