import ReapCore
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var store: Store

    var body: some View {
        Form {
            Section("检测来源") {
                ForEach(AgentKind.allCases) { agent in
                    Toggle(isOn: Binding(
                        get: { store.enabledAgents.contains(agent) },
                        set: { on in
                            if on { store.enabledAgents.insert(agent) } else { store.enabledAgents.remove(agent) }
                        }
                    )) {
                        HStack(spacing: 8) {
                            AgentBadge(agent: agent)
                            Text(description(agent)).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }

            Section {
                Toggle("自动结束空闲会话", isOn: $store.autoEndIdle)
                Stepper(value: $store.idleLimitMinutes, in: Store.minimumIdleLimit...(24 * 60), step: 30) {
                    HStack {
                        Text("空闲多久算闲置")
                        Spacer()
                        Text(Format.duration(store.idleLimitMinutes * 60)).monospacedDigit().foregroundStyle(.secondary)
                    }
                }
                if let last = store.lastAutoEnd {
                    Text("上次自动结束：\(last.formatted(date: .omitted, time: .shortened))")
                        .font(.caption).foregroundStyle(.secondary)
                }
            } header: {
                Text("空闲会话")
            } footer: {
                Text("只对能读到状态的 Claude Code 会话生效。对话记录不会丢，之后可以 claude --resume 接着聊。")
            }

            Section("通用") {
                Toggle("登录时启动", isOn: Binding(get: { store.launchAtLogin }, set: { store.launchAtLogin = $0 }))
                Toggle("菜单栏显示残留进程占用的内存", isOn: $store.showMemoryInMenuBar)
            }

            Section("关于") {
                LabeledContent("版本", value: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "开发版")
                LabeledContent("数据") { Text("只在本机读取进程和文件，不联网，不上传任何东西。") }
                LabeledContent("致谢") {
                    Link("检测思路参考 seshka（MIT）", destination: URL(string: "https://github.com/jigjoy-ai/seshka")!)
                }
            }
        }
        .formStyle(.grouped)
    }

    private func description(_ agent: AgentKind) -> String {
        switch agent {
        case .claude: "按 CLAUDECODE 环境变量、会话记录和进程组识别"
        case .codex: "按 CODEX_* 环境变量识别，App 自身组件和用户的浏览器不计"
        case .cursor: "按 CURSOR_AGENT=1 识别，Cursor 未公开文档，可能漏报"
        }
    }
}
