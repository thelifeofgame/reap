import Foundation

/// 磁盘上可以清理的一项。
public struct DiskItem: Identifiable, Hashable, Sendable {
    public enum Kind: Int, CaseIterable, Sendable {
        case worktree, scratch, cache
    }

    public enum State: Hashable, Sendable {
        case removable
        /// 有未提交改动：移到废纸篓，不直接删。
        case uncommitted
        /// 正在使用，不提供删除；附带原因。
        case busy(String)
    }

    public let id: String
    public let kind: Kind
    public let agent: AgentKind?
    public let title: String
    public var detail: String
    public let paths: [String]
    public var state: State
    /// 删掉的代价，例如“下次安装会重新下载”。
    public var note: String?
    /// worktree 所属仓库的主目录。
    public var repository: String?
    /// 测量完之前为 nil。
    public var bytes: UInt64?
}

public enum Disk {
    /// 正在使用的东西。任何会话或进程还要用的，都不会出现在可删列表里。
    public struct Context: Sendable {
        public var folders: [String] = []
        public var sessions: Set<String> = []

        public init(folders: [String] = [], sessions: Set<String> = []) {
            self.folders = folders
            self.sessions = sessions
        }

        public init(scan: ScanResult) {
            let items = scan.leftovers.flatMap(\.items) + scan.sessions.flatMap(\.items)
            folders = (items.map(\.cwd) + scan.sessions.map(\.host.cwd)).filter { $0.count > 1 }
            sessions = Set(items.compactMap(\.owner.session) + scan.sessions.compactMap(\.session))
        }

        func uses(_ path: String) -> Bool {
            folders.contains { $0 == path || $0.hasPrefix(path + "/") }
        }
    }

    static let home = FileManager.default.homeDirectoryForCurrentUser.path

    // MARK: Worktrees

    public static func worktrees(_ context: Context) -> [DiskItem] {
        let fm = FileManager.default
        var found: [(path: String, repository: String?, agent: AgentKind)] = []
        for repository in repositories(extra: context.folders) {
            let folder = repository + "/.claude/worktrees"
            for name in ((try? fm.contentsOfDirectory(atPath: folder)) ?? []).sorted() where !name.hasPrefix(".") {
                let path = folder + "/" + name
                if isDirectory(path) { found.append((path, repository, .claude)) }
            }
        }
        // Codex：~/.codex/worktrees/<id>/<仓库名>
        let codexRoot = home + "/.codex/worktrees"
        for id in ((try? fm.contentsOfDirectory(atPath: codexRoot)) ?? []).sorted() where !id.hasPrefix(".") {
            for name in (try? fm.contentsOfDirectory(atPath: codexRoot + "/" + id)) ?? [] where !name.hasPrefix(".") {
                let path = "\(codexRoot)/\(id)/\(name)"
                guard isDirectory(path), fm.fileExists(atPath: path + "/.git") else { continue }
                found.append((path, Worktree.read(root: path)?.repository, .codex))
            }
        }

        // 每个都要问 git 有没有未提交改动，大仓库一次要一秒左右，所以并行。
        var items = [DiskItem?](repeating: nil, count: found.count)
        let lock = NSLock()
        DispatchQueue.concurrentPerform(iterations: found.count) { i in
            let item = worktree(at: found[i].path, repository: found[i].repository, agent: found[i].agent, context)
            lock.lock()
            items[i] = item
            lock.unlock()
        }
        return items.compactMap { $0 }
    }

    private static func worktree(at path: String, repository: String?, agent: AgentKind, _ context: Context) -> DiskItem {
        let name = (path as NSString).lastPathComponent
        let known = Worktree.read(root: path)
        var state = DiskItem.State.removable
        if context.uses(path) {
            state = .busy("有会话或进程正在里面运行")
        } else if let reason = lockReason(of: path) {
            state = .busy(reason)
        } else if known == nil || Git.path == nil {
            state = .uncommitted
        } else if let git = Git.path, !Shell.run(git, ["--no-optional-locks", "-C", path, "status", "--porcelain"]).isEmpty {
            state = .uncommitted
        }

        var parts = [((repository ?? known?.repository ?? path) as NSString).lastPathComponent]
        if let branch = known?.branch, branch != name, branch != "worktree-" + name { parts.append(branch) }
        if let touched = lastTouched(path) {
            parts.append(Format.ago(Date().timeIntervalSince(touched)) + "未改动")
        }
        return DiskItem(id: path, kind: .worktree, agent: agent, title: name, detail: parts.joined(separator: " · "),
                        paths: [path], state: state, repository: repository ?? known?.repository)
    }

    /// Agent 工作时会锁住 worktree，锁里写着它的 PID。进程还在就不能删。
    private static func lockReason(of path: String) -> String? {
        guard let records = Worktree.gitRecords(root: path),
              let reason = try? String(contentsOfFile: records + "/locked", encoding: .utf8) else { return nil }
        guard let match = reason.firstMatch(of: #/\(pid (\d+)/#), let pid = Int32(match.1) else {
            return "已被 git 锁定"
        }
        return Killer.isRunning(pid) ? "Agent 正在里面工作" : nil
    }

    private static func lastTouched(_ path: String) -> Date? {
        let fm = FileManager.default
        var files = [path]
        if let records = Worktree.gitRecords(root: path) { files += [records + "/HEAD", records + "/index"] }
        return files.compactMap { (try? fm.attributesOfItem(atPath: $0))?[.modificationDate] as? Date }.max()
    }

    nonisolated(unsafe) private static var folderOfProject: [String: String] = [:]
    private static let reading = NSLock()

    /// 所有 Claude 会话工作过的仓库主目录。项目目录名不能反推路径，但会话文件每行都写了 cwd。
    private static func repositories(extra: [String]) -> [String] {
        let fm = FileManager.default
        let root = home + "/.claude/projects"
        var folders = Set(extra)
        for project in (try? fm.contentsOfDirectory(atPath: root)) ?? [] {
            reading.lock()
            let known = folderOfProject[project]
            reading.unlock()
            if let known {
                folders.insert(known)
                continue
            }
            guard let file = (try? fm.contentsOfDirectory(atPath: root + "/" + project))?.first(where: { $0.hasSuffix(".jsonl") }),
                  let handle = FileHandle(forReadingAtPath: "\(root)/\(project)/\(file)") else { continue }
            let head = String(decoding: (try? handle.read(upToCount: 256 * 1024)) ?? Data(), as: UTF8.self)
            try? handle.close()
            guard let folder = AgentFiles.firstString(in: head, key: "cwd") else { continue }
            reading.lock()
            folderOfProject[project] = folder
            reading.unlock()
            folders.insert(folder)
        }

        var repositories = Set<String>()
        for folder in folders {
            var at = folder
            while at.count > 1 {
                var dir: ObjCBool = false
                if fm.fileExists(atPath: at + "/.git", isDirectory: &dir) {
                    if dir.boolValue {
                        repositories.insert(at)
                    } else if let main = Worktree.read(root: at)?.repository {
                        repositories.insert(main)
                    }
                    break
                }
                at = (at as NSString).deletingLastPathComponent
            }
        }
        return repositories.sorted()
    }

    // MARK: Claude 临时目录

    private static let scratchRoot = "/private/tmp/claude-\(getuid())"
    private static let sessionFolder = #/^[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}$/#

    /// 已关闭会话的临时目录：大于 20 MB 的项目单列一项，其余合成一项。
    public static func scratch(_ context: Context) -> [DiskItem] {
        let fm = FileManager.default
        var folders: [(project: String, path: String)] = []
        for project in (try? fm.contentsOfDirectory(atPath: scratchRoot)) ?? [] {
            for session in (try? fm.contentsOfDirectory(atPath: scratchRoot + "/" + project)) ?? []
            where session.wholeMatch(of: sessionFolder) != nil && isClosed(session, in: project, context) {
                folders.append((project, "\(scratchRoot)/\(project)/\(session)"))
            }
        }
        let sizes = measure(folders.map(\.path))
        var byProject: [String: (paths: [String], bytes: UInt64)] = [:]
        for folder in folders {
            byProject[folder.project, default: ([], 0)].paths.append(folder.path)
            byProject[folder.project]?.bytes += sizes[folder.path] ?? 0
        }

        var items: [DiskItem] = []
        var rest: (paths: [String], bytes: UInt64, projects: Int) = ([], 0, 0)
        for (project, found) in byProject {
            guard found.bytes >= 20 * 1_048_576 else {
                rest.paths += found.paths
                rest.bytes += found.bytes
                rest.projects += 1
                continue
            }
            items.append(DiskItem(id: "scratch:" + project, kind: .scratch, agent: .claude, title: projectName(project),
                                  detail: "\(found.paths.count) 个已关闭会话", paths: found.paths, state: .removable,
                                  bytes: found.bytes))
        }
        items.sort { ($0.bytes ?? 0) > ($1.bytes ?? 0) }
        if rest.bytes > 0 {
            items.append(DiskItem(id: "scratch:rest", kind: .scratch, agent: .claude,
                                  title: items.isEmpty ? "所有项目" : "其他 \(rest.projects) 个项目",
                                  detail: "\(rest.paths.count) 个已关闭会话", paths: rest.paths, state: .removable,
                                  bytes: rest.bytes))
        }
        return items
    }

    /// Claude Code 把项目路径里的非字母数字字符换成 "-" 作为目录名，这里去掉家目录前缀给人看。
    private static func projectName(_ folder: String) -> String {
        let prefix = String(home.map { $0.isASCII && ($0.isLetter || $0.isNumber) ? $0 : "-" })
        var name = folder
        if name.hasPrefix(prefix) { name = String(name.dropFirst(prefix.count)) }
        name = name.trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        return name.isEmpty ? "~" : name
    }

    /// 会话已关闭、没有进程在跑、一小时内没写过，才能删。
    private static func isClosed(_ session: String, in project: String, _ context: Context) -> Bool {
        guard !context.sessions.contains(session) else { return false }
        let folder = "\(scratchRoot)/\(project)/\(session)"
        let fm = FileManager.default
        let touched = [folder, folder + "/tasks", folder + "/scratchpad"]
            .compactMap { (try? fm.attributesOfItem(atPath: $0))?[.modificationDate] as? Date }.max()
        return touched.map { Date().timeIntervalSince($0) > 3600 } ?? false
    }

    // MARK: 缓存

    private struct Cache {
        let name: String
        let paths: [String]
        let note: String
    }

    /// 只列工具下次用到时会自动重新下载的缓存。需要手动重装的（例如 Playwright 浏览器）不在其列。
    private static let knownCaches = [
        Cache(name: "npm 缓存", paths: [".npm/_cacache"], note: "下次安装时 npm 会重新下载。"),
        Cache(name: "pnpm 存储", paths: ["Library/pnpm/store", "Library/Caches/pnpm", ".local/share/pnpm/store"],
              note: "已有项目不受影响，下次安装时重新下载。"),
        Cache(name: "Yarn 缓存", paths: [".yarn/berry/cache", "Library/Caches/Yarn"], note: "下次安装时 Yarn 会重新下载。"),
        Cache(name: "Bun 缓存", paths: [".bun/install/cache"], note: "下次安装时 Bun 会重新下载。"),
        Cache(name: "Gradle 缓存", paths: [".gradle/caches"], note: "下次构建时 Gradle 会重新下载依赖。"),
        Cache(name: "Xcode DerivedData", paths: ["Library/Developer/Xcode/DerivedData"], note: "下次 Xcode 会从头构建。"),
        Cache(name: "CocoaPods 缓存", paths: ["Library/Caches/CocoaPods"], note: "下次安装时重新下载 pods。"),
        Cache(name: "Homebrew 下载", paths: ["Library/Caches/Homebrew"], note: "已安装的软件不受影响，只删下载的安装包。"),
        Cache(name: "pip 缓存", paths: ["Library/Caches/pip"], note: "下次安装时 pip 会重新下载。"),
        Cache(name: "uv 缓存", paths: [".cache/uv"], note: "下次安装时 uv 会重新下载。"),
        Cache(name: "Cargo 注册表", paths: [".cargo/registry"], note: "下次构建时 Cargo 会重新下载 crate。"),
        Cache(name: "Go 构建缓存", paths: ["Library/Caches/go-build"], note: "下次构建时重新编译。"),
        Cache(name: "Electron 下载", paths: ["Library/Caches/electron"], note: "项目安装时会重新下载 Electron。"),
    ]

    /// 小于这个大小的缓存不值得列出。
    public static let smallestCache: UInt64 = 100 * 1_048_576

    public static func caches() -> [DiskItem] {
        let found = knownCaches.compactMap { cache -> DiskItem? in
            let paths = cache.paths.map { home + "/" + $0 }.filter { FileManager.default.fileExists(atPath: $0) }
            guard !paths.isEmpty else { return nil }
            return DiskItem(id: "cache:" + cache.name, kind: .cache, agent: nil, title: cache.name,
                            detail: paths.map(abbreviate).joined(separator: ", "), paths: paths, state: .removable,
                            note: cache.note)
        }
        let sizes = measure(found.flatMap(\.paths))
        return found.compactMap { item in
            var item = item
            item.bytes = item.paths.reduce(0) { $0 + (sizes[$1] ?? 0) }
            return (item.bytes ?? 0) >= smallestCache ? item : nil
        }
        .sorted { ($0.bytes ?? 0) > ($1.bytes ?? 0) }
    }

    // MARK: 测量与删除

    public static func measure(_ paths: [String]) -> [String: UInt64] {
        var sizes: [String: UInt64] = [:]
        for start in stride(from: 0, to: paths.count, by: 200) {
            let batch = Array(paths[start..<min(start + 200, paths.count)])
            for line in Shell.run("/usr/bin/du", ["-sk"] + batch).split(separator: "\n") {
                guard let tab = line.firstIndex(of: "\t"),
                      let kb = UInt64(line[..<tab].trimmingCharacters(in: .whitespaces)) else { continue }
                sizes[String(line[line.index(after: tab)...])] = kb * 1024
            }
        }
        return sizes
    }

    /// 删除前再确认一次没人在用。返回错误说明，成功返回 nil。
    public static func remove(_ item: DiskItem, _ context: Context) -> String? {
        let fm = FileManager.default
        switch item.kind {
        case .cache:
            return delete(item.paths)
        case .scratch:
            let closed = item.paths.filter { path in
                let parts = path.split(separator: "/").map(String.init)
                return parts.count >= 2 && isClosed(parts[parts.count - 1], in: parts[parts.count - 2], context)
            }
            return delete(closed)
        case .worktree:
            guard let path = item.paths.first else { return "没有可删除的内容" }
            let now = worktree(at: path, repository: item.repository, agent: item.agent ?? .claude, context)
            guard now.state == item.state else { return "状态在列表生成后变了，没有删除任何东西。" }
            let records = Worktree.gitRecords(root: path)
            switch item.state {
            case .busy(let reason):
                return reason
            case .removable:
                guard let git = Git.path, let repository = item.repository else { return "找不到 git" }
                // 已退出的 Agent 留下的锁会挡住 git。
                if let records, fm.fileExists(atPath: records + "/locked") {
                    _ = Shell.failure(git, ["-C", repository, "worktree", "unlock", path])
                }
                // 里面有改动或未跟踪文件时 git 会拒绝。分支保留。
                return Shell.failure(git, ["-C", repository, "worktree", "remove", path])
            case .uncommitted:
                do {
                    try fm.trashItem(at: URL(fileURLWithPath: path), resultingItemURL: nil)
                } catch {
                    return error.localizedDescription
                }
                // 目录已进废纸篓，这里只清掉 git 的记录；分支保留。
                if let git = Git.path, let repository = item.repository, let records {
                    try? fm.removeItem(atPath: records + "/locked")
                    _ = Shell.failure(git, ["-C", repository, "worktree", "prune"])
                }
                return nil
            }
        }
    }

    private static func delete(_ paths: [String]) -> String? {
        var failure: String?
        for path in paths where path.hasPrefix(home + "/") || path.hasPrefix(scratchRoot + "/") {
            do {
                try FileManager.default.removeItem(atPath: path)
            } catch {
                failure = failure ?? error.localizedDescription
            }
        }
        return failure
    }

    public static func abbreviate(_ path: String) -> String {
        path.hasPrefix(home) ? "~" + path.dropFirst(home.count) : path
    }

    private static func isDirectory(_ path: String) -> Bool {
        var dir: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &dir) && dir.boolValue
    }
}

enum Git {
    /// /usr/bin/git 总是存在，但没装开发者工具时它只会弹窗提示安装。
    static let path: String? = {
        let fm = FileManager.default
        let tools = Shell.run("/usr/bin/xcode-select", ["-p"]).trimmingCharacters(in: .whitespacesAndNewlines)
        if !tools.isEmpty, fm.fileExists(atPath: tools) { return "/usr/bin/git" }
        return ["/opt/homebrew/bin/git", "/usr/local/bin/git"].first { fm.isExecutableFile(atPath: $0) }
    }()
}
