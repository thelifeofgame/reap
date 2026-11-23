import AppKit
import ReapCore
import SwiftUI

/// 菜单栏弹窗：只放最常用的——看一眼有多少残留、一键结束、跳到主窗口。
struct PopoverView: View {
    @EnvironmentObject private var store: Store
    @Environment(\.openWindow) private var openWindow
    @State private var confirmAll = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            summary
            if !store.leftovers.isEmpty { groupList }
            idleHint
            actions
        }
        .padding(14)
        .frame(width: 360)
        .onAppear { store.refresh() }
    }

    private var header: some View {
        HStack(spacing: 8) {
            Image(systemName: "scissors")
                .font(.system(size: 13, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 24, height: 24)
                .background(Theme.accent.gradient, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
            Text("Reap").font(.headline)
            Spacer()
            if store.scanning {
                ProgressView().controlSize(.small)
            } else {
                Button { store.refresh() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.borderless)
                    .help("重新扫描")
            }
            Menu {
                Button("打开主窗口") { open(.leftovers) }
                Button("设置…") { open(.settings) }
                Divider()
                Button("退出 Reap") { NSApp.terminate(nil) }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
        }
    }

    private var summary: some View {
        let clean = store.leftovers.isEmpty
        return HStack(spacing: 12) {
            Image(systemName: clean ? "checkmark.seal.fill" : "bolt.horizontal.circle.fill")
                .font(.system(size: 28))
                .foregroundStyle(clean ? Theme.busy : Theme.danger)
                .symbolRenderingMode(.hierarchical)
            VStack(alignment: .leading, spacing: 2) {
                if clean {
                    Text("很干净").font(.title3.weight(.semibold))
                    Text("没有已关闭会话留下的进程").font(.caption).foregroundStyle(.secondary)
                } else {
                    Text("\(store.leftoverCount) 个残留进程").font(.title3.weight(.semibold))
                    Text("来自 \(store.leftovers.count) 个已关闭会话，占用 \(Format.bytes(store.leftoverMemory))")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer()
        }
        .card(padding: 12)
    }

    private var groupList: some View {
        VStack(spacing: 2) {
            ForEach(store.leftovers.prefix(5)) { group in
                HStack(spacing: 8) {
                    AgentBadge(agent: group.agent, compact: true)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(group.title ?? group.project).font(.system(size: 12.5, weight: .medium)).lineLimit(1)
                        Text(summaryLine(group)).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Text(Format.bytes(group.memory)).font(.caption).monospacedDigit().foregroundStyle(.secondary)
                    KillButton(busy: group.items.contains { store.killing.contains($0.pid) }, help: "结束这一组") {
                        store.kill(group.items)
                    }
                }
                .padding(.vertical, 4)
                .padding(.horizontal, 6)
            }
            if store.leftovers.count > 5 {
                Button("还有 \(store.leftovers.count - 5) 组，在主窗口查看") { open(.leftovers) }
                    .buttonStyle(.plain).font(.caption).foregroundStyle(Theme.accent)
                    .padding(.top, 4)
            }
        }
    }

    @ViewBuilder
    private var idleHint: some View {
        let idle = store.idleSessions
        if !idle.isEmpty {
            HStack(spacing: 8) {
                Image(systemName: "moon.zzz.fill").foregroundStyle(Theme.idle)
                Text("\(idle.count) 个会话空闲超过 \(Format.duration(store.idleLimitMinutes * 60))")
                    .font(.caption)
                Spacer()
                Button("结束") { store.end(idle) }.controlSize(.small)
            }
            .padding(10)
            .background(Theme.idle.opacity(0.1), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
        }
    }

    private var actions: some View {
        HStack(spacing: 8) {
            Button { open(.leftovers) } label: {
                Text("打开主窗口").frame(maxWidth: .infinity)
            }
            .controlSize(.large)
            Button { confirmAll = true } label: {
                Text(store.leftovers.isEmpty ? "无需清理" : "全部结束").frame(maxWidth: .infinity)
            }
            .controlSize(.large)
            .buttonStyle(.borderedProminent)
            .tint(Theme.danger)
            .disabled(store.leftovers.isEmpty)
        }
        .confirmationDialog("结束全部 \(store.leftoverCount) 个残留进程？", isPresented: $confirmAll) {
            Button("全部结束", role: .destructive) { store.killAll() }
        }
    }

    private func summaryLine(_ group: LeftoverGroup) -> String {
        var parts = [group.project]
        let ports = group.items.flatMap(\.ports)
        if !ports.isEmpty { parts.append(ports.prefix(3).map { ":\($0)" }.joined(separator: " ")) }
        parts.append("\(group.processCount) 个进程")
        return parts.joined(separator: " · ")
    }

    private func open(_ section: Pane) {
        store.section = section
        openWindow(id: "main")
        NSApp.activate(ignoringOtherApps: true)
    }
}
