import Foundation

/// 仓库主目录以外的 git worktree。
public struct Worktree: Hashable, Sendable {
    public let root: String
    public let name: String
    public let branch: String?
    /// 仓库主目录。
    public let repository: String?
    /// 目录已经被删了，但还有进程在里面跑。
    public let missing: Bool

    /// `folder` 所在的 worktree；在主目录里或不在仓库里时返回 nil。
    public static func containing(_ folder: String) -> Worktree? {
        guard folder.hasPrefix("/") else { return nil }
        let fm = FileManager.default
        var at = folder
        while at.count > 1 {
            var isDirectory: ObjCBool = false
            if fm.fileExists(atPath: at + "/.git", isDirectory: &isDirectory) {
                // 主目录里 .git 是目录，worktree 里是一个指回仓库的文件。
                return isDirectory.boolValue ? nil : read(root: at)
            }
            at = (at as NSString).deletingLastPathComponent
        }
        if !fm.fileExists(atPath: folder), let marker = folder.range(of: "/.claude/worktrees/") {
            let name = folder[marker.upperBound...].split(separator: "/").first.map(String.init) ?? ""
            guard !name.isEmpty else { return nil }
            let repository = String(folder[..<marker.lowerBound])
            return Worktree(root: repository + "/.claude/worktrees/" + name, name: name, branch: nil,
                            repository: repository, missing: true)
        }
        return nil
    }

    public static func read(root: String) -> Worktree? {
        guard let records = gitRecords(root: root), let marker = records.range(of: "/.git/worktrees/") else { return nil }
        var branch: String?
        if let head = try? String(contentsOfFile: records + "/HEAD", encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines), head.hasPrefix("ref: refs/heads/") {
            branch = String(head.dropFirst("ref: refs/heads/".count))
        }
        return Worktree(root: root, name: (root as NSString).lastPathComponent, branch: branch,
                        repository: String(records[..<marker.lowerBound]), missing: false)
    }

    /// git 记录这个 worktree 的目录："<仓库>/.git/worktrees/<名字>"。
    static func gitRecords(root: String) -> String? {
        guard let pointer = try? String(contentsOfFile: root + "/.git", encoding: .utf8),
              let line = pointer.split(separator: "\n").first(where: { $0.hasPrefix("gitdir:") }) else { return nil }
        let path = line.dropFirst("gitdir:".count).trimmingCharacters(in: .whitespaces)
        return path.hasPrefix("/") ? path : root + "/" + path
    }

    /// 分支名与目录名重复时只显示目录名。
    public var label: String {
        guard let branch, branch != name, branch != "worktree-" + name else { return name }
        return "\(name) · \(branch)"
    }
}
