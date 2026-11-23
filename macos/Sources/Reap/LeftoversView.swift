import ReapCore
import SwiftUI

struct AgentFilter: View {
    @Binding var selection: AgentKind?

    var body: some View {
        Picker("来源", selection: $selection) {
            Text("全部").tag(AgentKind?.none)
            ForEach(AgentKind.allCases) { agent in
                Text(agent.shortName).tag(AgentKind?.some(agent))
            }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .frame(width: 240)
    }
}

struct LeftoversView: View {
    @EnvironmentObject private var store: Store
    @State private var search = ""
    @State private var agent: AgentKind?
    @State private var confirmAll = false

    private var groups: [LeftoverGroup] {
        store.leftovers.compactMap { group in
            guard agent == nil || group.agent == agent else { return nil }
            guard !search.isEmpty else { return group }
            if group.project.localizedCaseInsensitiveContains(search)
                || (group.title?.localizedCaseInsensitiveContains(search) ?? false) {
                return group
            }
            var g = group
            g.items = group.items.filter {
                $0.cmd.localizedCaseInsensitiveContains(search) || String($0.pid) == search
                    || $0.ports.contains { String($0) == search }
            }
            return g.items.isEmpty ? nil : g
        }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(spacing: 12) {
                    StatCard(title: "残留进程", value: "\(store.leftoverCount)", symbol: "bolt.horizontal.circle",
                             tint: store.leftoverCount > 0 ? Theme.danger : Theme.busy)
                    StatCard(title: "占用内存", value: Format.bytes(store.leftoverMemory), symbol: "memorychip")
                    StatCard(title: "本次已释放", value: Format.bytes(store.freedTotal), symbol: "leaf", tint: Theme.busy)
                }

                if !store.hasScanned {
                    ProgressView("正在扫描…").frame(maxWidth: .infinity).padding(40)
                } else if groups.isEmpty {
                    EmptyState(
                        symbol: "checkmark.seal.fill",
                        title: store.leftovers.isEmpty ? "很干净" : "没有匹配的进程",
                        message: store.leftovers.isEmpty
                            ? "没有 AI 编码会话留下的进程。关掉的会话如果留下开发服务器、监听器或浏览器，会出现在这里。"
                            : "换个关键词或来源试试。"
                    )
                } else {
                    ForEach(groups) { group in
                        LeftoverCard(group: group)
                    }
                }
            }
            .padding(20)
        }
        .searchable(text: $search, placement: .toolbar, prompt: "搜索命令、端口、PID、项目")
        .toolbar {
            ToolbarItemGroup {
                AgentFilter(selection: $agent)
                Button {
                    confirmAll = true
                } label: {
                    Label("全部结束", systemImage: "xmark.circle")
                }
                .disabled(store.leftovers.isEmpty)
                .help("结束所有残留进程")
            }
        }
        .confirmationDialog("结束全部 \(store.leftoverCount) 个残留进程？", isPresented: $confirmAll) {
            Button("全部结束（释放约 \(Format.bytes(store.leftoverMemory))）", role: .destructive) { store.killAll() }
        } message: {
            Text("这些进程所属的会话都已关闭。先请求退出，2 秒内没退出的会被强制结束。")
        }
    }
}

struct LeftoverCard: View {
    @EnvironmentObject private var store: Store
    let group: LeftoverGroup

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .center, spacing: 10) {
                AgentBadge(agent: group.agent)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(group.project).font(.headline)
                        if let worktree = group.worktree {
                            Chip(text: worktree.label, symbol: "arrow.triangle.branch", tint: Theme.accent)
                        }
                    }
                    HStack(spacing: 6) {
                        if let title = group.title {
                            Text(title).lineLimit(1)
                        }
                        if let short = group.shortSession {
                            Text(short).font(.system(.caption, design: .monospaced))
                        }
                        Text("已关闭 · 最早 \(Format.duration(Int(Date().timeIntervalSince1970 - group.oldestStart))) 前启动")
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 1) {
                    Text(Format.bytes(group.memory)).font(.system(.body, design: .rounded).weight(.semibold)).monospacedDigit()
                    Text("\(group.processCount) 个进程").font(.caption).foregroundStyle(.secondary)
                }
                Button("结束本组") { store.kill(group.items) }
                    .buttonStyle(.bordered)
                    .tint(Theme.danger)
            }
            Divider().padding(.horizontal, -14)
            VStack(spacing: 0) {
                ForEach(group.items) { item in
                    ProcessRow(item: item, busy: store.killing.contains(item.pid)) { store.kill([item]) }
                }
            }
            .padding(.horizontal, -10)
        }
        .card()
    }
}
