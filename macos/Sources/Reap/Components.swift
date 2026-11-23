import AppKit
import ReapCore
import SwiftUI

struct AgentBadge: View {
    let agent: AgentKind
    var compact = false

    var body: some View {
        HStack(spacing: 4) {
            Image(systemName: agent.symbol).font(.system(size: 9, weight: .bold))
            if !compact {
                Text(agent.shortName).font(.system(size: 11, weight: .semibold))
            }
            if agent.rule.experimental && !compact {
                Text("实验").font(.system(size: 9, weight: .medium)).opacity(0.75)
            }
        }
        .foregroundStyle(agent.color)
        .padding(.horizontal, compact ? 5 : 7)
        .padding(.vertical, 3)
        .background(agent.color.opacity(0.14), in: Capsule())
        .help(agent.displayName)
    }
}

struct Chip: View {
    let text: String
    var symbol: String?
    var tint: Color = .secondary

    var body: some View {
        HStack(spacing: 3) {
            if let symbol { Image(systemName: symbol).font(.system(size: 9, weight: .semibold)) }
            Text(text).font(.system(size: 11, weight: .medium)).lineLimit(1)
        }
        .foregroundStyle(tint)
        .padding(.horizontal, 6)
        .padding(.vertical, 2)
        .background(tint.opacity(0.12), in: Capsule())
    }
}

struct PortChip: View {
    let port: Int

    var body: some View {
        Button {
            if let url = URL(string: "http://localhost:\(port)") { NSWorkspace.shared.open(url) }
        } label: {
            Chip(text: ":\(port)", symbol: "network", tint: Theme.accent)
        }
        .buttonStyle(.plain)
        .help("在浏览器打开 http://localhost:\(port)")
    }
}

struct StatCard: View {
    let title: String
    let value: String
    let symbol: String
    var tint: Color = Theme.accent

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(tint)
                .frame(width: 34, height: 34)
                .background(tint.opacity(0.14), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            VStack(alignment: .leading, spacing: 1) {
                Text(value).font(.system(size: 20, weight: .semibold, design: .rounded)).monospacedDigit()
                Text(title).font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .card(padding: 12)
    }
}

struct EmptyState: View {
    let symbol: String
    let title: String
    let message: String
    var tint: Color = Theme.busy

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 40, weight: .light))
                .foregroundStyle(tint)
                .symbolRenderingMode(.hierarchical)
            Text(title).font(.title3.weight(.semibold))
            Text(message).font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
        .frame(maxWidth: 360)
        .padding(40)
        .frame(maxWidth: .infinity)
    }
}

/// 鼠标移上去才显红的结束按钮，列表里不会一片红。
struct KillButton: View {
    let busy: Bool
    var help = "结束进程"
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Group {
                if busy {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 16))
                        .foregroundStyle(hover ? Theme.danger : Color.secondary.opacity(0.5))
                }
            }
            .frame(width: 22, height: 22)
        }
        .buttonStyle(.plain)
        .disabled(busy)
        .onHover { hover = $0 }
        .help(help)
    }
}

struct ProcessRow: View {
    let item: ProcessItem
    let busy: Bool
    let onKill: () -> Void
    @State private var expanded = false
    @State private var hover = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .center, spacing: 10) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(item.displayCommand)
                        .font(.system(size: 12.5, design: .monospaced))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .help(item.cmd)
                    HStack(spacing: 6) {
                        Text("PID \(String(item.pid))")
                        Text("·")
                        Text("运行 \(Format.duration(item.age()))")
                        if let plugin = item.plugin {
                            Chip(text: "插件 \(plugin)", symbol: "puzzlepiece.extension")
                        }
                        if !item.helpers.isEmpty {
                            Button {
                                withAnimation(.snappy(duration: 0.2)) { expanded.toggle() }
                            } label: {
                                HStack(spacing: 2) {
                                    Text("+\(item.helpers.count) 个子进程")
                                    Image(systemName: "chevron.right").font(.system(size: 8, weight: .bold))
                                        .rotationEffect(.degrees(expanded ? 90 : 0))
                                }
                            }
                            .buttonStyle(.plain)
                            .foregroundStyle(Theme.accent)
                        }
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
                Spacer(minLength: 8)
                ForEach(item.ports.prefix(3), id: \.self) { PortChip(port: $0) }
                Text(Format.bytes(item.memory))
                    .font(.system(size: 12, weight: .medium)).monospacedDigit()
                    .foregroundStyle(.secondary)
                    .frame(width: 70, alignment: .trailing)
                KillButton(busy: busy, action: onKill)
            }
            if expanded {
                VStack(alignment: .leading, spacing: 3) {
                    ForEach(item.helpers, id: \.pid) { helper in
                        HStack(spacing: 6) {
                            Image(systemName: "arrow.turn.down.right").font(.system(size: 9)).foregroundStyle(.tertiary)
                            Text(String(helper.pid)).monospacedDigit().foregroundStyle(.secondary)
                            Text(helper.cmd).lineLimit(1).truncationMode(.middle)
                        }
                        .font(.system(size: 11, design: .monospaced))
                    }
                }
                .padding(.leading, 4)
            }
        }
        .padding(.vertical, 7)
        .padding(.horizontal, 10)
        .background(hover ? Color.primary.opacity(0.04) : .clear, in: RoundedRectangle(cornerRadius: 8))
        .onHover { hover = $0 }
        .contextMenu {
            Button("复制 PID") { copy(String(item.pid)) }
            Button("复制完整命令") { copy(item.cmd) }
            if !item.cwd.isEmpty {
                Button("在访达中显示工作目录") {
                    NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: item.cwd)
                }
            }
            Divider()
            Button("结束进程", role: .destructive, action: onKill)
        }
    }

    private func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}

struct ToastView: View {
    let toast: Toast

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: toast.isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                .foregroundStyle(toast.isError ? Theme.danger : Theme.busy)
            Text(toast.text).font(.callout.weight(.medium))
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .background(.regularMaterial, in: Capsule())
        .overlay(Capsule().strokeBorder(Theme.stroke))
        .shadow(color: .black.opacity(0.12), radius: 10, y: 4)
    }
}
