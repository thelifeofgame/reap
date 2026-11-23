import Darwin
import Foundation

public enum Scanner {
    /// 读当前所有进程，拼成 spec 里的快照格式。
    static func snapshot() -> (Snapshot, claudeRecords: [Int32: AgentFiles.ClaudeRecord]) {
        let uid = getuid()
        let me = getpid()
        var buffer: [UInt8] = []
        var processes: [ProcessRecord] = []
        for entry in DarwinProcesses.list() where entry.pid > 0 {
            let own = entry.uid == uid
            let args = own ? DarwinProcesses.arguments(of: entry.pid, buffer: &buffer) : nil
            let exe = args.map(\.exe).flatMap { $0.isEmpty ? nil : $0 } ?? (own ? DarwinProcesses.path(of: entry.pid) : "")
            let cmd = args.map { $0.argv.joined(separator: " ") }.flatMap { $0.isEmpty ? nil : $0 } ?? entry.comm
            processes.append(ProcessRecord(pid: entry.pid, ppid: entry.ppid, pgid: entry.pgid, exe: exe, cmd: cmd,
                                           env: args?.env, own: own, startTime: entry.startTime))
        }

        // 先读会话记录再认宿主：npm 安装的 Claude Code 进程名是 node，只能靠记录认出来。
        var records: [Int32: AgentFiles.ClaudeRecord] = [:]
        var hostSessions: [Int32: String] = [:]
        let startOf = Dictionary(processes.map { ($0.pid, $0.startTime) }, uniquingKeysWith: { a, _ in a })
        for pid in AgentFiles.claudeRecordPids() {
            guard let start = startOf[pid], let record = AgentFiles.claudeRecord(pid: pid, startTime: start) else { continue }
            records[pid] = record
            if let session = record.sessionID { hostSessions[pid] = session }
        }
        let hosts = Classifier.hosts(processes, platform: "darwin", hostSessions: hostSessions)

        var outputSessions: [Int32: String] = [:]
        for p in processes where p.own && hosts[p.pid] == nil && p.pid != me {
            if let session = AgentFiles.claudeOutputSession(pid: p.pid) { outputSessions[p.pid] = session }
        }

        var watch: [Int32: (agent: AgentKind, session: String?)] = [:]
        for (pid, host) in hosts where host.cli {
            watch[pid] = (agent: host.agent, session: hostSessions[pid])
        }
        CommandWatch.shared.watch(watch)
        let watched = CommandWatch.shared.snapshot(liveGroups: Set(processes.map(\.pgid)))

        let snapshot = Snapshot(platform: "darwin", selfPid: me, processes: processes, hostSessions: hostSessions,
                                outputSessions: outputSessions, watchedGroups: watched)
        return (snapshot, records)
    }

    public static func scan(now: Date = Date()) -> ScanResult {
        let started = Date()
        let (snapshot, records) = snapshot()
        let owners = Classifier.classify(snapshot)
        let hosts = Classifier.hosts(snapshot.processes, platform: snapshot.platform, hostSessions: snapshot.hostSessions)
        var table: [Int32: ProcessRecord] = [:]
        for p in snapshot.processes { table[p.pid] = p }

        func item(_ p: ProcessRecord, _ owner: Owner) -> ProcessItem {
            ProcessItem(pid: p.pid, ppid: p.ppid, pgid: p.pgid, startTime: p.startTime, exe: p.exe, cmd: p.cmd,
                        owner: owner, marked: Classifier.isMarked(p, by: owner.agent))
        }

        var items = Rows.fold(owners.compactMap { pid, owner in table[pid].map { item($0, owner) } })

        // 补充展示用的信息，只对找到的进程做。
        let ports = DarwinProcesses.listeningPorts(of: items.flatMap(\.allPids))
        for i in items.indices {
            items[i].memory = items[i].allPids.reduce(0) { $0 + DarwinProcesses.footprint(of: $1) }
            items[i].ports = Array(Set(items[i].allPids.flatMap { ports[$0] ?? [] })).sorted()
            items[i].cwd = DarwinProcesses.cwd(of: items[i].pid)
            items[i].worktree = Worktree.containing(items[i].cwd)
        }

        var result = ScanResult()
        result.leftovers = Rows.group(items.filter { !$0.owner.alive }) { agent, session in
            let meta = agent == .codex ? AgentFiles.codexMeta(thread: session)
                : agent == .claude ? AgentFiles.claudeMeta(session: session) : .init()
            return (meta.cwd.map { ($0 as NSString).lastPathComponent }, meta.title)
        }
        result.sessions = sessions(snapshot: snapshot, table: table, hosts: hosts, records: records,
                                   alive: items.filter { $0.owner.alive && $0.age(now: now) >= 60 })
        result.scannedAt = now
        result.duration = Date().timeIntervalSince(started)
        return result
    }

    private static func sessions(snapshot: Snapshot, table: [Int32: ProcessRecord], hosts: [Int32: Classifier.Host],
                                 records: [Int32: AgentFiles.ClaudeRecord], alive: [ProcessItem]) -> [ActiveSession] {
        func ancestors(_ pid: Int32) -> [Int32] {
            var out: [Int32] = []
            var seen: Set<Int32> = [pid]
            var at = pid
            while let up = table[at]?.ppid, up > 0, table[up] != nil, seen.insert(up).inserted {
                out.append(up)
                at = up
            }
            return out
        }

        // 只列命令行宿主，且不列子 Agent（它们属于启动它们的那个会话）。
        let tops = hosts.filter { pid, host in
            host.cli && !ancestors(pid).contains { hosts[$0]?.agent == host.agent && hosts[$0]?.cli == true }
        }

        func topHost(of item: ProcessItem) -> Int32? {
            ancestors(item.pid).last { tops[$0]?.agent == item.owner.agent }
        }

        var byHost: [Int32: [ProcessItem]] = [:]
        let hostOfSession = Dictionary(records.compactMap { pid, r in r.sessionID.map { ($0, pid) } }, uniquingKeysWith: { a, _ in a })
        for item in alive {
            guard let host = topHost(of: item) ?? item.owner.session.flatMap({ hostOfSession[$0] }) else { continue }
            byHost[host, default: []].append(item)
        }

        return tops.compactMap { pid, host -> ActiveSession? in
            guard let p = table[pid] else { return nil }
            var hostItem = ProcessItem(pid: p.pid, ppid: p.ppid, pgid: p.pgid, startTime: p.startTime, exe: p.exe, cmd: p.cmd,
                                       owner: Owner(agent: host.agent, session: snapshot.hostSessions[pid], alive: true), marked: false)
            hostItem.memory = DarwinProcesses.footprint(of: pid)
            let record = records[pid]
            hostItem.cwd = record?.cwd ?? DarwinProcesses.cwd(of: pid)
            let session = record?.sessionID
            let meta = session.map { AgentFiles.claudeMeta(session: $0) }
            let status: ActiveSession.Status
            switch record?.status {
            case "busy": status = .busy
            case "idle": status = .idle
            case let other?: status = .other(other)
            case nil: status = .unknown
            }
            let managed = ancestors(pid).contains { hosts[$0]?.agent == host.agent && hosts[$0]?.cli == false }
            return ActiveSession(
                host: hostItem, agent: host.agent, session: session,
                project: managed || hostItem.cwd.count <= 1 ? host.agent.displayName + (managed ? " App" : "")
                    : (hostItem.cwd as NSString).lastPathComponent,
                title: record?.name ?? meta?.title,
                status: status, statusSince: record?.statusSince, managed: managed,
                items: (byHost[pid] ?? []).sorted { $0.startTime < $1.startTime }
            )
        }
        .sorted { a, b in
            // 空闲最久的在前，其次是运行最久的。
            let ia = a.idleSeconds() ?? -1, ib = b.idleSeconds() ?? -1
            return ia != ib ? ia > ib : a.host.startTime < b.host.startTime
        }
    }
}
