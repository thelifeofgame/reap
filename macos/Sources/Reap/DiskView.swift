import AppKit
import ReapCore
import SwiftUI

struct DiskView: View {
    @EnvironmentObject private var store: Store
    @State private var pending: DiskItem?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                HStack(spacing: 12) {
                    StatCard(title: "可释放空间", value: Format.bytes(store.diskReclaimable), symbol: "internaldrive", tint: Theme.accent)
                    StatCard(title: "Agent worktree", value: "\(store.worktrees.count)", symbol: "arrow.triangle.branch")
                    StatCard(title: "开发缓存", value: store.cachesLoading ? "测量中" : "\(store.caches.count) 项",
                             symbol: "shippingbox")
                }
                Text("只有点击后才会删除。正在使用的不会列为可删；有未提交改动的 worktree 移到废纸篓；删除 worktree 不会删分支。")
                    .font(.callout).foregroundStyle(.secondary)

                section("Agent worktree", symbol: "arrow.triangle.branch", items: store.worktrees,
                        loading: store.diskLoading, empty: "没有 Agent 创建的 worktree。")
                section("Claude 临时文件", symbol: "doc.on.doc", items: store.scratch,
                        loading: store.diskLoading, empty: "没有已关闭会话留下的临时文件。")
                section("可自动重建的缓存", symbol: "shippingbox", items: store.caches,
                        loading: store.cachesLoading, empty: "没有超过 \(Format.bytes(Disk.smallestCache)) 的缓存。")
            }
            .padding(20)
        }
        .toolbar {
            ToolbarItem {
                Button {
                    store.loadDisk()
                } label: {
                    Label("重新检查", systemImage: "arrow.clockwise")
                }
                .disabled(store.diskLoading || store.cachesLoading)
            }
        }
        .task {
            if store.worktrees.isEmpty && store.scratch.isEmpty && store.caches.isEmpty { store.loadDisk() }
        }
        .confirmationDialog(pending.map(confirmTitle) ?? "", isPresented: Binding(get: { pending != nil }, set: { if !$0 { pending = nil } }),
                            presenting: pending) { item in
            Button(item.state == .uncommitted ? "移到废纸篓" : "删除", role: .destructive) { store.remove(item) }
        } message: { item in
            Text(confirmMessage(item))
        }
    }

    @ViewBuilder
    private func section(_ title: String, symbol: String, items: [DiskItem], loading: Bool, empty: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label(title, systemImage: symbol).font(.headline)
                if loading { ProgressView().controlSize(.small) }
                Spacer()
                let total = items.reduce(0) { $0 + ($1.bytes ?? 0) }
                if total > 0 { Text(Format.bytes(total)).foregroundStyle(.secondary).monospacedDigit() }
            }
            if items.isEmpty {
                Text(loading ? "正在检查…" : empty)
                    .font(.callout).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .card(padding: 12)
            } else {
                VStack(spacing: 0) {
                    ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                        if index > 0 { Divider() }
                        DiskRow(item: item, busy: store.removing.contains(item.id)) { pending = item }
                    }
                }
                .card(padding: 4)
            }
        }
    }

    private func confirmTitle(_ item: DiskItem) -> String {
        let size = item.bytes.map { "（\(Format.bytes($0))）" } ?? ""
        return item.state == .uncommitted ? "把「\(item.title)」移到废纸篓？\(size)" : "删除「\(item.title)」？\(size)"
    }

    private func confirmMessage(_ item: DiskItem) -> String {
        switch item.kind {
        case .worktree:
            return item.state == .uncommitted
                ? "里面有未提交的改动，所以移到废纸篓而不是直接删除。分支保留。"
                : "用 git worktree remove 删除，分支保留。"
        case .scratch:
            return "这些是已关闭会话的临时文件和命令输出，删除后无法恢复。"
        case .cache:
            return (item.note ?? "") + " 删除后无法恢复。"
        }
    }
}

struct DiskRow: View {
    let item: DiskItem
    let busy: Bool
    let onRemove: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            if let agent = item.agent { AgentBadge(agent: agent, compact: true) }
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title).font(.system(size: 13, weight: .medium)).lineLimit(1)
                Text(item.note ?? item.detail).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                    .help(item.paths.map(Disk.abbreviate).joined(separator: "\n"))
            }
            Spacer()
            switch item.state {
            case .busy(let reason):
                Chip(text: reason, symbol: "lock.fill")
            case .uncommitted:
                Chip(text: "有未提交改动", symbol: "exclamationmark.triangle.fill", tint: Theme.idle)
            case .removable:
                EmptyView()
            }
            Text(item.bytes.map(Format.bytes) ?? "—")
                .font(.system(size: 12, weight: .medium)).monospacedDigit().foregroundStyle(.secondary)
                .frame(width: 72, alignment: .trailing)
            Button {
                NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: item.paths.first ?? "")
            } label: {
                Image(systemName: "magnifyingglass")
            }
            .buttonStyle(.borderless)
            .help("在访达中显示")
            Group {
                if busy {
                    ProgressView().controlSize(.small).frame(width: 64)
                } else {
                    Button(item.state == .uncommitted ? "移走" : "清理", action: onRemove)
                        .buttonStyle(.bordered)
                        .frame(width: 64)
                }
            }
            .disabled({ if case .busy = item.state { true } else { false } }())
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
    }
}
