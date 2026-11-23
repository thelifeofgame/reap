import Foundation

/// 把判定出的进程整理成列表行和卡片。纯函数，便于测试。
public enum Rows {
    /// 1. 应用的辅助进程（浏览器每个标签页一个）并进应用那一行。
    /// 2. Claude Code 包在命令外的 shell：同组还有别的进程时并进去；只剩它时显示里面的命令。
    public static func fold(_ input: [ProcessItem]) -> [ProcessItem] {
        var items = input.sorted { $0.pid < $1.pid }
        var index: [Int32: Int] = [:]
        for (i, p) in items.enumerated() { index[p.pid] = i }

        var folded = Set<Int>()
        for i in items.indices {
            var app: Int?
            var at = i
            var seen = Set<Int>()
            while seen.insert(at).inserted, let up = index[items[at].ppid],
                  let root = items[up].bundleRoot, items[i].exe.hasPrefix(root) {
                app = up
                at = up
            }
            guard let app else { continue }
            items[app].helpers.append(.init(pid: items[i].pid, startTime: items[i].startTime, cmd: items[i].cmd))
            folded.insert(i)
        }
        items = items.indices.filter { !folded.contains($0) }.map { items[$0] }

        folded = []
        for i in items.indices where !items[i].marked {
            guard let inner = unwrap(items[i]) else { continue }
            let host = items.indices.first {
                $0 != i && !folded.contains($0) && items[$0].pgid == items[i].pgid && unwrap(items[$0]) == nil
            }
            if let host {
                items[host].helpers.append(.init(pid: items[i].pid, startTime: items[i].startTime, cmd: items[i].cmd))
                folded.insert(i)
            } else {
                items[i].wrapped = inner
            }
        }
        return items.indices.filter { !folded.contains($0) }.map { items[$0] }
    }

    /// Claude Code 的包装形如 `zsh -c ... && eval '<命令>' < /dev/null && pwd -P >| <文件>`。
    static func unwrap(_ item: ProcessItem) -> String? {
        guard item.pid == item.pgid, ["zsh", "bash", "sh"].contains(item.executableName),
              let start = item.cmd.range(of: " eval '") else { return nil }
        var inner = item.cmd[start.upperBound...]
        if let end = inner.range(of: "' < /dev/null", options: .backwards) ?? inner.range(of: "'", options: .backwards) {
            inner = inner[..<end.lowerBound]
        }
        let text = inner.replacingOccurrences(of: "'\"'\"'", with: "'").replacingOccurrences(of: "\n", with: " ")
        return text.isEmpty ? nil : text
    }

    /// 按 Agent、会话、worktree 分卡片；没有会话 ID 时按工作目录分。最老的卡片在前。
    public static func group(_ items: [ProcessItem], meta: (AgentKind, String) -> (project: String?, title: String?)) -> [LeftoverGroup] {
        var buckets: [String: [ProcessItem]] = [:]
        for item in items {
            let where_ = item.owner.session.map { "s:" + $0 } ?? "d:" + item.cwd
            buckets[[item.owner.agent.rawValue, where_, item.worktree?.root ?? ""].joined(separator: "\n"), default: []].append(item)
        }
        return buckets.map { key, members in
            let first = members[0]
            let info: (project: String?, title: String?) = first.owner.session.map { meta(first.owner.agent, $0) } ?? (nil, nil)
            let folder = info.project ?? members.lazy.map(\.cwd).first { !$0.isEmpty }.map { ($0 as NSString).lastPathComponent }
            return LeftoverGroup(
                id: key,
                agent: first.owner.agent,
                session: first.owner.session,
                project: folder ?? "未知项目",
                title: info.title,
                worktree: first.worktree,
                items: members.sorted { $0.startTime < $1.startTime }
            )
        }
        .sorted { ($0.oldestStart, $0.id) < ($1.oldestStart, $1.id) }
    }
}
