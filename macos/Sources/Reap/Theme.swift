import ReapCore
import SwiftUI

enum Theme {
    static let accent = Color(red: 0.38, green: 0.40, blue: 0.96)
    static let danger = Color(red: 0.92, green: 0.30, blue: 0.30)
    static let busy = Color(red: 0.20, green: 0.72, blue: 0.45)
    static let idle = Color(red: 0.96, green: 0.62, blue: 0.18)
    static let card = Color(nsColor: .controlBackgroundColor)
    static let stroke = Color(nsColor: .separatorColor).opacity(0.6)
    static let radius: CGFloat = 12
}

extension AgentKind {
    var color: Color {
        switch self {
        case .claude: Color(red: 0.85, green: 0.47, blue: 0.34)
        case .codex: Color(red: 0.07, green: 0.62, blue: 0.50)
        case .cursor: Color(red: 0.50, green: 0.42, blue: 0.95)
        }
    }

    var symbol: String {
        switch self {
        case .claude: "asterisk"
        case .codex: "chevron.left.forwardslash.chevron.right"
        case .cursor: "cursorarrow.rays"
        }
    }

    var shortName: String {
        switch self {
        case .claude: "Claude"
        case .codex: "Codex"
        case .cursor: "Cursor"
        }
    }
}

struct CardModifier: ViewModifier {
    var padding: CGFloat = 14

    func body(content: Content) -> some View {
        content
            .padding(padding)
            .background(Theme.card, in: RoundedRectangle(cornerRadius: Theme.radius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Theme.radius, style: .continuous).strokeBorder(Theme.stroke))
    }
}

extension View {
    func card(padding: CGFloat = 14) -> some View { modifier(CardModifier(padding: padding)) }
}
