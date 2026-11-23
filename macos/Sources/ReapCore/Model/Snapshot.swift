import Foundation

/// 一个进程在某一刻的样子。格式与 spec/DETECTION.md 的快照一致。
public struct ProcessRecord: Codable, Equatable, Sendable {
    public var pid: Int32
    public var ppid: Int32
    public var pgid: Int32
    /// 可执行文件完整路径，读不到时为空串。
    public var exe: String
    public var cmd: String
    /// nil 表示读不到（系统程序、受保护进程）。
    public var env: [String: String]?
    /// 是否当前用户的进程。
    public var own: Bool
    /// 启动时间（Unix 秒）。判定不用它，结束进程前用它确认 PID 没被复用。
    public var startTime: Double

    public init(pid: Int32, ppid: Int32, pgid: Int32, exe: String, cmd: String,
                env: [String: String]?, own: Bool = true, startTime: Double = 0) {
        self.pid = pid
        self.ppid = ppid
        self.pgid = pgid
        self.exe = exe
        self.cmd = cmd
        self.env = env
        self.own = own
        self.startTime = startTime
    }

    enum CodingKeys: String, CodingKey { case pid, ppid, pgid, exe, cmd, env, own, startTime }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        pid = try c.decode(Int32.self, forKey: .pid)
        ppid = try c.decode(Int32.self, forKey: .ppid)
        pgid = try c.decode(Int32.self, forKey: .pgid)
        exe = try c.decodeIfPresent(String.self, forKey: .exe) ?? ""
        cmd = try c.decodeIfPresent(String.self, forKey: .cmd) ?? ""
        env = try c.decodeIfPresent([String: String].self, forKey: .env)
        own = try c.decodeIfPresent(Bool.self, forKey: .own) ?? true
        startTime = try c.decodeIfPresent(Double.self, forKey: .startTime) ?? 0
    }

    /// 路径统一成正斜杠，Windows 快照也能用同一套规则。
    var normalizedExe: String { exe.replacingOccurrences(of: "\\", with: "/") }

    /// 用来和 hostNames 比较的可执行文件名。Windows 文件名不区分大小写，去掉 .exe 后转小写；其他平台原样。
    func hostName(platform: String) -> String {
        let source = exe.isEmpty ? (cmd.split(separator: " ").first.map(String.init) ?? "") : normalizedExe
        let name = (source as NSString).lastPathComponent
        guard platform == "windows" else { return name }
        let lower = name.lowercased()
        return lower.hasSuffix(".exe") ? String(lower.dropLast(4)) : lower
    }
}

public struct WatchedGroup: Codable, Equatable, Sendable {
    public var agent: String
    public var session: String?
    public var hostPid: Int32

    public init(agent: String, session: String?, hostPid: Int32) {
        self.agent = agent
        self.session = session
        self.hostPid = hostPid
    }
}

public struct Snapshot: Codable, Sendable {
    public var platform: String
    public var selfPid: Int32
    public var processes: [ProcessRecord]
    /// 宿主 PID → 会话 ID。
    public var hostSessions: [Int32: String]
    /// 正在写 Claude 命令输出文件的进程 → 会话 ID。
    public var outputSessions: [Int32: String]
    public var watchedGroups: [Int32: WatchedGroup]

    public init(platform: String, selfPid: Int32, processes: [ProcessRecord],
                hostSessions: [Int32: String] = [:], outputSessions: [Int32: String] = [:],
                watchedGroups: [Int32: WatchedGroup] = [:]) {
        self.platform = platform
        self.selfPid = selfPid
        self.processes = processes
        self.hostSessions = hostSessions
        self.outputSessions = outputSessions
        self.watchedGroups = watchedGroups
    }

    enum CodingKeys: String, CodingKey {
        case platform, selfPid, processes, hostSessions, outputSessions, watchedGroups
    }

    // JSON 的对象键只能是字符串，这里把 "123" 转成 Int32。
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        platform = try c.decode(String.self, forKey: .platform)
        selfPid = try c.decode(Int32.self, forKey: .selfPid)
        processes = try c.decode([ProcessRecord].self, forKey: .processes)
        hostSessions = Self.intKeys(try c.decodeIfPresent([String: String].self, forKey: .hostSessions) ?? [:])
        outputSessions = Self.intKeys(try c.decodeIfPresent([String: String].self, forKey: .outputSessions) ?? [:])
        watchedGroups = Self.intKeys(try c.decodeIfPresent([String: WatchedGroup].self, forKey: .watchedGroups) ?? [:])
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(platform, forKey: .platform)
        try c.encode(selfPid, forKey: .selfPid)
        try c.encode(processes, forKey: .processes)
        try c.encode(Self.stringKeys(hostSessions), forKey: .hostSessions)
        try c.encode(Self.stringKeys(outputSessions), forKey: .outputSessions)
        try c.encode(Self.stringKeys(watchedGroups), forKey: .watchedGroups)
    }

    private static func intKeys<V>(_ d: [String: V]) -> [Int32: V] {
        Dictionary(uniqueKeysWithValues: d.compactMap { k, v in Int32(k).map { ($0, v) } })
    }

    private static func stringKeys<V>(_ d: [Int32: V]) -> [String: V] {
        Dictionary(uniqueKeysWithValues: d.map { (String($0.key), $0.value) })
    }
}

/// 一个进程属于哪个 Agent 的哪个会话，以及启动它的宿主是否还活着。
public struct Owner: Codable, Equatable, Hashable, Sendable {
    public var agent: AgentKind
    public var session: String?
    public var alive: Bool

    public init(agent: AgentKind, session: String?, alive: Bool) {
        self.agent = agent
        self.session = session
        self.alive = alive
    }
}
