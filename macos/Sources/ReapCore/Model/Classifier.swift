import Foundation

/// 按 spec/DETECTION.md 判定每个进程的归属。纯函数：只看快照，不碰系统。
public enum Classifier {
    public struct Host: Equatable, Sendable {
        public let agent: AgentKind
        /// 按 hostNames 识别的命令行宿主，可以收养它名下环境不可读的命令；按路径识别的是 App。
        public let cli: Bool
    }

    /// `hostSessions` 里有记录的进程也是 Claude 宿主（npm 安装时进程名是 node）。
    public static func hosts(_ processes: [ProcessRecord], platform: String, hostSessions: [Int32: String] = [:],
                             rules: [AgentRule] = AgentRule.all) -> [Int32: Host] {
        var result: [Int32: Host] = [:]
        for p in processes {
            if hostSessions[p.pid] != nil {
                result[p.pid] = Host(agent: .claude, cli: true)
                continue
            }
            let exe = pathKey(p.normalizedExe, platform)
            let name = p.hostName(platform: platform)
            for rule in rules {
                if rule.hostNames.contains(name) {
                    result[p.pid] = Host(agent: rule.kind, cli: true)
                    break
                }
                if !exe.isEmpty, rule.hostPathContains.contains(where: { exe.contains(pathKey($0, platform)) }) {
                    result[p.pid] = Host(agent: rule.kind, cli: false)
                    break
                }
            }
        }
        return result
    }

    /// Windows 路径不区分大小写，比较前统一小写。
    static func pathKey(_ path: String, _ platform: String) -> String {
        platform == "windows" ? path.lowercased() : path
    }

    /// 进程环境里是否带有该 Agent 的标记。
    public static func isMarked(_ p: ProcessRecord, by agent: AgentKind) -> Bool {
        guard let env = p.env else { return false }
        return agent.rule.markers.contains { $0.matches(env) }
    }

    public static func classify(_ snapshot: Snapshot, rules: [AgentRule] = AgentRule.all) -> [Int32: Owner] {
        var table: [Int32: ProcessRecord] = [:]
        for p in snapshot.processes { table[p.pid] = p }
        let selfPid = snapshot.selfPid

        func parent(_ pid: Int32) -> Int32? {
            guard let pp = table[pid]?.ppid, pp > 0, pp != pid, table[pp] != nil else { return nil }
            return pp
        }

        func ancestors(_ pid: Int32) -> [Int32] {
            var out: [Int32] = []
            var seen: Set<Int32> = [pid]
            var at = pid
            while let up = parent(at), seen.insert(up).inserted {
                out.append(up)
                at = up
            }
            return out
        }

        let hosts = Self.hosts(snapshot.processes, platform: snapshot.platform, hostSessions: snapshot.hostSessions, rules: rules)
        let hostAgent = hosts.mapValues(\.agent)
        let cliHosts = Set(hosts.filter(\.value.cli).keys)

        func hostAncestor(_ pid: Int32, _ kind: AgentKind) -> Int32? {
            ancestors(pid).first { hostAgent[$0] == kind }
        }

        // 没有记录的宿主（子 Agent）沿用最近的同类宿主的会话。
        var sessionOf: [Int32: String] = [:]
        for (host, kind) in hostAgent {
            if let session = snapshot.hostSessions[host] {
                sessionOf[host] = session
                continue
            }
            for up in ancestors(host) where hostAgent[up] == kind {
                if let session = snapshot.hostSessions[up] {
                    sessionOf[host] = session
                    break
                }
            }
        }

        func ignored(_ p: ProcessRecord, _ rule: AgentRule) -> Bool {
            let platform = snapshot.platform
            let exe = pathKey(p.normalizedExe, platform)
            guard !exe.isEmpty else { return false }
            if rule.ignorePathContains.contains(where: { exe.contains(pathKey($0, platform)) }) { return true }
            return (rule.ignoreExePrefixes[platform] ?? []).contains { exe.hasPrefix(pathKey($0, platform)) }
        }

        let candidates = snapshot.processes
            .filter { $0.own && $0.pid != selfPid && hostAgent[$0.pid] == nil && !ancestors($0.pid).contains(selfPid) }
            .map(\.pid)
            .sorted()
        let candidateSet = Set(candidates)

        var owners: [Int32: Owner] = [:]
        var found: [Int32] = []
        /// 环境里点名的宿主，进程被重新挂到 launchd 下时用它找回当前会话。
        var named: [Int32: Int32] = [:]

        // 1. 环境标记
        for pid in candidates {
            guard let p = table[pid], let env = p.env else { continue }
            for rule in rules where rule.markers.contains(where: { $0.matches(env) }) && !ignored(p, rule) {
                let kind = rule.kind
                let session = rule.sessionEnv.flatMap { env[$0] }.flatMap { $0.isEmpty ? nil : $0 }
                let hostPid = rule.hostPidEnv.flatMap { env[$0] }.flatMap { Int32($0) }
                let pointsAtHost = hostPid.map { hostAgent[$0] == kind } ?? false
                if pointsAtHost, let hostPid { named[pid] = hostPid }
                owners[pid] = Owner(agent: kind, session: session, alive: pointsAtHost || hostAncestor(pid, kind) != nil)
                found.append(pid)
                break
            }
        }

        // 2. Claude 命令输出文件
        let claudeRule = AgentKind.claude.rule
        let openClaude = Set(sessionOf.filter { hostAgent[$0.key] == .claude }.values)
        for pid in candidates where owners[pid] == nil {
            guard let session = snapshot.outputSessions[pid], let p = table[pid], !ignored(p, claudeRule) else { continue }
            let alive = hostAncestor(pid, .claude) != nil || openClaude.contains(session)
            owners[pid] = Owner(agent: .claude, session: session, alive: alive)
            found.append(pid)
        }

        // 3. 记下来的进程组，只用于环境不可读的进程
        for pid in candidates where owners[pid] == nil {
            guard let p = table[pid], p.env == nil, let group = snapshot.watchedGroups[p.pgid],
                  let kind = AgentKind(rawValue: group.agent), !ignored(p, kind.rule) else { continue }
            let alive = hostAgent[group.hostPid] == kind || hostAncestor(pid, kind) != nil
            owners[pid] = Owner(agent: kind, session: group.session, alive: alive)
            found.append(pid)
        }

        // 4. 传播
        var changed = true
        var rounds = 0
        while changed, rounds < 5 {
            changed = false
            rounds += 1

            var i = 0
            while i < found.count {
                let start = found[i]
                i += 1
                guard let owner = owners[start] else { continue }
                var at = start
                while let up = parent(at), up > 1, candidateSet.contains(up), owners[up] == nil,
                      let p = table[up], p.env == nil, !ignored(p, owner.agent.rule) {
                    owners[up] = owner
                    found.append(up)
                    changed = true
                    at = up
                }
            }

            for pid in candidates where owners[pid] == nil {
                guard let p = table[pid] else { continue }
                var result: Owner?
                for up in ancestors(pid) {
                    if let kind = hostAgent[up] {
                        if cliHosts.contains(up), p.env == nil, p.pgid != table[up]?.pgid, !ignored(p, kind.rule) {
                            result = Owner(agent: kind, session: sessionOf[up], alive: true)
                        }
                        break
                    }
                    if let hit = owners[up] {
                        if !ignored(p, hit.agent.rule) { result = hit }
                        break
                    }
                }
                guard let result else { continue }
                owners[pid] = result
                found.append(pid)
                changed = true
            }
        }

        // 5. 挂在活宿主下的，归到宿主当前的会话
        for pid in found {
            guard let owner = owners[pid] else { continue }
            let host = hostAncestor(pid, owner.agent) ?? named[pid].flatMap { hostAgent[$0] == owner.agent ? $0 : nil }
            guard let host else { continue }
            owners[pid] = Owner(agent: owner.agent, session: sessionOf[host] ?? owner.session, alive: true)
        }
        return owners
    }
}
