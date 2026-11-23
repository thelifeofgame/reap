import Foundation

/// 一个 AI 编码工具的识别规则。字段含义与 spec/agents.json 一一对应，测试会校验两边一致。
public struct AgentRule: Codable, Equatable, Sendable {
    public struct Marker: Codable, Equatable, Sendable {
        public let env: String
        public let value: String?

        public init(env: String, value: String? = nil) {
            self.env = env
            self.value = value
        }

        func matches(_ environment: [String: String]) -> Bool {
            guard let actual = environment[env] else { return false }
            return value.map { $0 == actual } ?? true
        }
    }

    public let id: String
    public let name: String
    public let experimental: Bool
    public let hostNames: [String]
    public let hostPathContains: [String]
    public let markers: [Marker]
    public let sessionEnv: String?
    public let hostPidEnv: String?
    public let ignorePathContains: [String]
    public let ignoreExePrefixes: [String: [String]]

    public var kind: AgentKind { AgentKind(rawValue: id) ?? .claude }
}

public enum AgentKind: String, Codable, CaseIterable, Sendable, Identifiable {
    case claude, codex, cursor

    public var id: String { rawValue }
    public var rule: AgentRule { AgentRule.all.first { $0.id == rawValue }! }
    public var displayName: String { rule.name }
}

public extension AgentRule {
    /// 判定时按此顺序匹配。Claude 在前：它的标记最精确。
    static let all: [AgentRule] = [
        AgentRule(
            id: "claude", name: "Claude Code", experimental: false,
            hostNames: ["claude"], hostPathContains: [],
            markers: [Marker(env: "CLAUDECODE", value: "1")],
            sessionEnv: "CLAUDE_CODE_SESSION_ID", hostPidEnv: "CLAUDE_PID",
            ignorePathContains: [], ignoreExePrefixes: [:]
        ),
        AgentRule(
            id: "codex", name: "Codex", experimental: false,
            hostNames: ["codex"], hostPathContains: ["/ChatGPT.app/Contents/MacOS/", "/Codex.app/Contents/MacOS/"],
            markers: [Marker(env: "CODEX_SHELL", value: "1"), Marker(env: "CODEX_SANDBOX"), Marker(env: "CODEX_THREAD_ID")],
            sessionEnv: "CODEX_THREAD_ID", hostPidEnv: nil,
            ignorePathContains: ["/ChatGPT.app/", "/Codex.app/", "/.codex/"],
            ignoreExePrefixes: ["darwin": ["/Applications/", "/System/"], "windows": ["C:/Program Files/Google/Chrome/", "C:/Program Files (x86)/Google/Chrome/", "C:/Program Files (x86)/Microsoft/Edge/",
                                "C:/Program Files/Mozilla Firefox/", "C:/Program Files/WindowsApps/"]]
        ),
        AgentRule(
            id: "cursor", name: "Cursor", experimental: true,
            hostNames: ["cursor-agent"], hostPathContains: ["/Cursor.app/Contents/MacOS/", "/Programs/cursor/Cursor.exe"],
            markers: [Marker(env: "CURSOR_AGENT", value: "1")],
            sessionEnv: nil, hostPidEnv: nil,
            ignorePathContains: ["/Cursor.app/", "/.cursor/", "/Programs/cursor/"],
            ignoreExePrefixes: ["darwin": ["/Applications/", "/System/"], "windows": ["C:/Program Files/Google/Chrome/", "C:/Program Files (x86)/Google/Chrome/", "C:/Program Files (x86)/Microsoft/Edge/",
                                "C:/Program Files/Mozilla Firefox/", "C:/Program Files/WindowsApps/"]]
        ),
    ]
}
