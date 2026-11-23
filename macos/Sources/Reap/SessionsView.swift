import AppKit
import ReapCore
import SwiftUI

struct SessionsView: View {
    @EnvironmentObject private var store: Store
    @State private var agent: AgentKind?
    @State private var confirmIdle = false
    @State private var pending: ActiveSession?

    private var sessions: [ActiveSession] {
        store.sessions.filter { agent == nil || $0.agent == agent }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                let idle = store.idleSessions
                HStack(spacing: 12) {
                    StatCard(title: "活跃会话", value: "\(store.sessions.count)", symbol: "bubble.left.and.text.bubble.right")
                    StatCard(title: "工作中", value: "\(store.sessions.filter { $0.status == .busy }.count)",
                             symbol: "waveform", tint: Theme.busy)
                    StatCard(title: "空闲超过 \(Format.duration(store.idleLimitMinutes * 60))", value: "\(idle.count)",
                             symbol: "moon.zzz", tint: Theme.idle)
                }

                if !idle.isEmpty {
                    HStack(spacing: 10) {
                        Image(systemName: "moon.zzz.fill").foregroundStyle(Theme.idle)
                        Text("\(idle.count) 个会话已空闲超过 \(Format.duration(store.idleLimitMinutes * 60))，还占着 \(Format.bytes(idle.reduce(0) { $0 + $1.memory }))。")
                        Spacer()
                        Button("全部结束") { confirmIdle = true }.buttonStyle(.borderedProminent).tint(Theme.idle)
                    }
                    .font(.callout)
                    .card(padding: 12)
                }

                if !store.hasScanned {
                    ProgressView("正在扫描…").frame(maxWidth: .infinity).padding(40)
                } else if sessions.isEmpty {
                    EmptyState(symbol: "moon.stars", title: "没有打开的会话",
                               message: "Claude Code、Codex 或 Cursor Agent 运行时，会在这里显示状态和它开着的进程。",
                               tint: .secondary)
                } else {
                    ForEach(sessions) { session in
                        SessionCard(session: session) { pending = session }
                    }
                }
            }
            .padding(20)
        }
        .toolbar {
            ToolbarItem { AgentFilter(selection: $agent) }
        }
        .confirmationDialog("结束 \(store.idleSessions.count) 个空闲会话？", isPresented: $confirmIdle) {
            Button("结束", role: .destructive) { store.end(store.idleSessions) }
        } message: {
            Text("对话记录不会丢，之后可以用 claude --resume 接着聊。")
        }
        .confirmationDialog("结束这个会话？", isPresented: Binding(get: { pending != nil }, set: { if !$0 { pending = nil } }),
                            presenting: pending) { session in
            Button("结束会话和它的 \(session.items.count) 个进程", role: .destructive) { store.end([session]) }
        } message: { session in
            Text(session.agent == .claude ? "对话记录不会丢，之后可以用 claude --resume 接着聊。" : "会话里正在进行的工作会中断。")
        }
    }
}

struct StatusPill: View {
    let session: ActiveSession

    var body: some View {
        let (text, color): (String, Color) = {
            switch session.status {
            case .busy: return ("工作中", Theme.busy)
            case .idle: return ("空闲 " + Format.duration(session.idleSeconds() ?? 0), Theme.idle)
            case .unknown: return (session.managed ? "由 App 管理" : "状态未知", .secondary)
            case .other(let s): return (s, .secondary)
            }
        }()
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 7, height: 7)
            Text(text).font(.system(size: 11, weight: .semibold))
        }
        .foregroundStyle(color)
        .padding(.horizontal, 8)
        .padding(.vertical, 3)
        .background(color.opacity(0.13), in: Capsule())
        .help(session.managed ? "App 内置的后台进程，结束它会关掉整个 App，请在 App 里操作。" : "")
    }
}

struct SessionCard: View {
    @EnvironmentObject private var store: Store
    let session: ActiveSession
    let onEnd: () -> Void
    @State private var expanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center, spacing: 10) {
                AgentBadge(agent: session.agent)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 8) {
                        Text(session.project).font(.headline)
                        StatusPill(session: session)
                    }
                    HStack(spacing: 6) {
                        if let title = session.title { Text(title).lineLimit(1) }
                        Text("PID \(String(session.host.pid)) · 已运行 \(Format.duration(session.host.age()))")
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 1) {
                    Text(Format.bytes(session.memory)).font(.system(.body, design: .rounded).weight(.semibold)).monospacedDigit()
                    Text("\(session.items.count) 个后台进程").font(.caption).foregroundStyle(.secondary)
                }
                if let id = session.session, session.agent == .claude {
                    Button {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString("claude --resume \(id)", forType: .string)
                        store.show("已复制 claude --resume \(id.prefix(8))…")
                    } label: {
                        Image(systemName: "doc.on.doc")
                    }
                    .buttonStyle(.borderless)
                    .help("复制恢复命令 claude --resume")
                }
                if !session.managed {
                    Button("结束会话", action: onEnd)
                        .buttonStyle(.bordered)
                        .tint(Theme.danger)
                        .disabled(store.killing.contains(session.host.pid))
                }
            }
            if !session.host.cwd.isEmpty, session.host.cwd != "/" {
                Label(Disk.abbreviate(session.host.cwd), systemImage: "folder")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            if !session.items.isEmpty {
                Button {
                    withAnimation(.snappy(duration: 0.2)) { expanded.toggle() }
                } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "chevron.right").font(.system(size: 9, weight: .bold))
                            .rotationEffect(.degrees(expanded ? 90 : 0))
                        Text(expanded ? "收起后台进程" : "查看 \(session.items.count) 个后台进程")
                    }
                    .font(.caption.weight(.medium))
                }
                .buttonStyle(.plain)
                .foregroundStyle(Theme.accent)
                if expanded {
                    VStack(spacing: 0) {
                        ForEach(session.items) { item in
                            ProcessRow(item: item, busy: store.killing.contains(item.pid)) { store.kill([item]) }
                        }
                    }
                    .padding(.horizontal, -10)
                }
            }
        }
        .card()
    }
}
