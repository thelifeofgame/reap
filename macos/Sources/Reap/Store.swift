import AppKit
import Combine
import Foundation
import ReapCore
import ServiceManagement

enum Pane: String, CaseIterable, Identifiable {
    case leftovers, sessions, disk, settings
    var id: String { rawValue }

    var title: String {
        switch self {
        case .leftovers: "残留进程"
        case .sessions: "活跃会话"
        case .disk: "磁盘清理"
        case .settings: "设置"
        }
    }

    var symbol: String {
        switch self {
        case .leftovers: "bolt.horizontal.circle"
        case .sessions: "bubble.left.and.text.bubble.right"
        case .disk: "internaldrive"
        case .settings: "gearshape"
        }
    }
}

struct Toast: Identifiable, Equatable {
    let id = UUID()
    let text: String
    var isError = false
}

@MainActor
final class Store: ObservableObject {
    static let minimumIdleLimit = 30

    @Published private(set) var scan = ScanResult()
    @Published private(set) var scanning = false
    @Published private(set) var hasScanned = false
    /// 正在结束的 PID，界面上显示为处理中。
    @Published private(set) var killing = Set<Int32>()
    @Published var section: Pane = .leftovers
    @Published var toast: Toast?

    @Published private(set) var worktrees: [DiskItem] = []
    @Published private(set) var scratch: [DiskItem] = []
    @Published private(set) var caches: [DiskItem] = []
    @Published private(set) var diskLoading = false
    @Published private(set) var cachesLoading = false
    @Published private(set) var removing = Set<String>()

    /// 本次运行累计释放的内存。
    @Published private(set) var freedTotal: UInt64 = 0
    @Published private(set) var killedTotal = 0
    @Published private(set) var lastAutoEnd: Date?

    @Published var enabledAgents: Set<AgentKind> {
        didSet { defaults.set(enabledAgents.map(\.rawValue), forKey: Keys.agents) }
    }
    @Published var autoEndIdle: Bool {
        didSet { defaults.set(autoEndIdle, forKey: Keys.autoEnd) }
    }
    @Published var idleLimitMinutes: Int {
        didSet {
            if idleLimitMinutes < Self.minimumIdleLimit { idleLimitMinutes = Self.minimumIdleLimit }
            defaults.set(idleLimitMinutes, forKey: Keys.idleLimit)
        }
    }
    @Published var showMemoryInMenuBar: Bool {
        didSet { defaults.set(showMemoryInMenuBar, forKey: Keys.menuMemory) }
    }

    private enum Keys {
        static let agents = "enabledAgents"
        static let autoEnd = "autoEndIdle"
        static let idleLimit = "idleLimitMinutes"
        static let menuMemory = "showMemoryInMenuBar"
    }

    private let defaults = UserDefaults.standard
    private var timer: AnyCancellable?

    init() {
        let saved = defaults.stringArray(forKey: Keys.agents)?.compactMap(AgentKind.init(rawValue:))
        enabledAgents = Set(saved ?? AgentKind.allCases)
        autoEndIdle = defaults.bool(forKey: Keys.autoEnd)
        let limit = defaults.integer(forKey: Keys.idleLimit)
        idleLimitMinutes = limit > 0 ? max(limit, Self.minimumIdleLimit) : 120
        showMemoryInMenuBar = defaults.bool(forKey: Keys.menuMemory)

        refresh()
        timer = Timer.publish(every: 15, on: .main, in: .common).autoconnect()
            .sink { [weak self] _ in self?.refresh() }
    }

    // MARK: 派生数据

    var leftovers: [LeftoverGroup] { scan.leftovers.filter { enabledAgents.contains($0.agent) } }
    var sessions: [ActiveSession] { scan.sessions.filter { enabledAgents.contains($0.agent) } }
    var leftoverCount: Int { leftovers.reduce(0) { $0 + $1.processCount } }
    var leftoverMemory: UInt64 { leftovers.reduce(0) { $0 + $1.memory } }

    var idleSessions: [ActiveSession] {
        sessions.filter { !$0.managed && ($0.idleSeconds() ?? -1) >= idleLimitMinutes * 60 }
    }

    var diskReclaimable: UInt64 {
        (worktrees + scratch + caches).filter { if case .busy = $0.state { false } else { true } }
            .reduce(0) { $0 + ($1.bytes ?? 0) }
    }

    // MARK: 扫描

    func refresh() {
        guard !scanning else { return }
        scanning = true
        Task.detached(priority: .utility) {
            let result = Scanner.scan()
            await MainActor.run {
                self.scan = result
                self.scanning = false
                self.hasScanned = true
                self.endIdleIfDue()
            }
        }
    }

    // MARK: 结束进程

    func kill(_ items: [ProcessItem], label: String? = nil) {
        let targets = items.filter { !killing.contains($0.pid) }
        guard !targets.isEmpty else { return }
        killing.formUnion(targets.map(\.pid))
        Task.detached(priority: .userInitiated) {
            let outcome = Killer.terminate(targets)
            await MainActor.run {
                self.killing.subtract(targets.map(\.pid))
                self.freedTotal += outcome.freed
                self.killedTotal += outcome.killed
                var text = label ?? "已结束 \(outcome.killed) 个进程"
                if outcome.freed > 0 { text += "，释放 \(Format.bytes(outcome.freed))" }
                if !outcome.skipped.isEmpty { text += "（\(outcome.skipped.count) 个已自行退出）" }
                self.show(text)
                self.refresh()
            }
        }
    }

    func killAll() {
        kill(leftovers.flatMap(\.items))
    }

    func end(_ sessions: [ActiveSession], automatic: Bool = false) {
        let targets = sessions.filter { !$0.managed }
        guard !targets.isEmpty else { return }
        let items = targets.flatMap { [$0.host] + $0.items }
        if automatic { lastAutoEnd = Date() }
        let prefix = automatic ? "已自动结束" : "已结束"
        kill(items, label: "\(prefix) \(targets.count) 个会话")
    }

    private func endIdleIfDue() {
        guard autoEndIdle else { return }
        let due = idleSessions.filter { !killing.contains($0.host.pid) }
        if !due.isEmpty { end(due, automatic: true) }
    }

    // MARK: 磁盘

    func loadDisk() {
        guard !diskLoading else { return }
        diskLoading = true
        cachesLoading = true
        let context = Disk.Context(scan: scan)
        Task.detached(priority: .utility) {
            let worktrees = Disk.worktrees(context)
            let scratch = Disk.scratch(context)
            // worktree 先列出来，大小随后补上。
            await MainActor.run {
                self.worktrees = worktrees
                self.scratch = scratch
                self.diskLoading = false
            }
            let sizes = Disk.measure(worktrees.flatMap(\.paths))
            await MainActor.run {
                self.worktrees = self.worktrees.map { item in
                    var item = item
                    item.bytes = item.paths.reduce(0) { $0 + (sizes[$1] ?? 0) }
                    return item
                }
            }
            let caches = Disk.caches()
            await MainActor.run {
                self.caches = caches
                self.cachesLoading = false
            }
        }
    }

    func remove(_ item: DiskItem) {
        guard !removing.contains(item.id) else { return }
        removing.insert(item.id)
        let context = Disk.Context(scan: scan)
        Task.detached(priority: .userInitiated) {
            let error = Disk.remove(item, context)
            await MainActor.run {
                self.removing.remove(item.id)
                if let error {
                    self.show("\(item.title)：\(error)", isError: true)
                    return
                }
                self.worktrees.removeAll { $0.id == item.id }
                self.scratch.removeAll { $0.id == item.id }
                self.caches.removeAll { $0.id == item.id }
                let how = item.state == .uncommitted ? "已移到废纸篓" : "已清理"
                self.show("\(how)「\(item.title)」" + (item.bytes.map { "，释放 \(Format.bytes($0))" } ?? ""))
            }
        }
    }

    // MARK: 其他

    func show(_ text: String, isError: Bool = false) {
        let toast = Toast(text: text, isError: isError)
        self.toast = toast
        Task {
            try? await Task.sleep(for: .seconds(4))
            if self.toast == toast { self.toast = nil }
        }
    }

    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch {
                show("无法修改登录项：\(error.localizedDescription)", isError: true)
            }
            objectWillChange.send()
        }
    }
}
