import AppKit
import ReapCore
import SwiftUI

extension Notification.Name {
    static let reapOpenMain = Notification.Name("reapOpenMain")
}

/// 菜单栏 App 没有 Dock 图标：首次启动、或用户在访达里再次打开 Reap 时，直接弹出主窗口。
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        let key = "launchedBefore"
        guard !UserDefaults.standard.bool(forKey: key) else { return }
        UserDefaults.standard.set(true, forKey: key)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            NotificationCenter.default.post(name: .reapOpenMain, object: nil)
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        NotificationCenter.default.post(name: .reapOpenMain, object: nil)
        return false
    }
}

@main
struct ReapApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var store = Store()

    var body: some Scene {
        MenuBarExtra {
            PopoverView().environmentObject(store)
        } label: {
            MenuBarLabel().environmentObject(store)
        }
        .menuBarExtraStyle(.window)

        Window("Reap", id: "main") {
            MainView()
                .environmentObject(store)
                .tint(Theme.accent)
        }
        .defaultSize(width: 980, height: 660)
        .windowToolbarStyle(.unified)
    }
}

struct MenuBarLabel: View {
    @EnvironmentObject private var store: Store
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        label.onReceive(NotificationCenter.default.publisher(for: .reapOpenMain)) { _ in
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    @ViewBuilder
    private var label: some View {
        let count = store.leftoverCount
        HStack(spacing: 3) {
            Image(systemName: count > 0 ? "scissors.circle.fill" : "scissors")
            if count > 0 {
                Text(store.showMemoryInMenuBar ? Format.bytes(store.leftoverMemory) : "\(count)")
                    .monospacedDigit()
            }
        }
    }
}
