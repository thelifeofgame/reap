import Foundation

/// 记下命令行宿主启动的每条命令所在的进程组。
///
/// Claude Code 等工具会把每条命令放进独立的进程组，命令再启动的东西也留在组里，
/// 即使父进程已经退出。对环境不可读、输出也不写到会话目录的进程，进程组是唯一的线索。
/// 组号本身只是某个短命 shell 的 PID，所以只信任亲眼看到被宿主创建的组。
/// 只对本工具运行期间启动的命令有效，不跨重启保存。
final class CommandWatch: @unchecked Sendable {
    static let shared = CommandWatch()

    private struct Host {
        let agent: AgentKind
        let session: String?
    }

    private let lock = NSLock()
    private let queue = DispatchQueue(label: "dev.reap.command-watch")
    private var groups: [Int32: WatchedGroup] = [:]
    private var hosts: [Int32: Host] = [:]
    private var sources: [Int32: DispatchSourceProcess] = [:]

    /// 监视这些宿主，停止监视已经不在的。
    func watch(_ current: [Int32: (agent: AgentKind, session: String?)]) {
        lock.lock()
        hosts = current.mapValues { Host(agent: $0.agent, session: $0.session) }
        let gone = sources.filter { current[$0.key] == nil }
        for pid in gone.keys { sources[pid] = nil }
        let new = current.keys.filter { sources[$0] == nil }
        var added: [DispatchSourceProcess] = []
        for pid in new {
            let source = DispatchSource.makeProcessSource(identifier: pid, eventMask: .fork, queue: queue)
            source.setEventHandler { [weak self] in self?.note(startedBy: pid) }
            sources[pid] = source
            added.append(source)
        }
        lock.unlock()
        gone.values.forEach { $0.cancel() }
        added.forEach { $0.resume() }
        for pid in new { queue.async { self.note(startedBy: pid) } }
    }

    /// 当前仍有成员的组。`alive` 是这一刻所有进程的组号。
    func snapshot(liveGroups: Set<Int32>) -> [Int32: WatchedGroup] {
        lock.lock()
        defer { lock.unlock() }
        groups = groups.filter { liveGroups.contains($0.key) }
        return groups
    }

    private func note(startedBy host: Int32) {
        let children = DarwinProcesses.children(of: host)
        lock.lock()
        defer { lock.unlock() }
        guard let info = hosts[host] else { return }
        for child in children where getpgid(child) == child && groups[child] == nil {
            groups[child] = WatchedGroup(agent: info.agent.rawValue, session: info.session, hostPid: host)
        }
    }
}
