import AppKit
import ReapCore
import SwiftUI

struct MainView: View {
    @EnvironmentObject private var store: Store

    var body: some View {
        NavigationSplitView {
            List(selection: $store.section) {
                ForEach(Pane.allCases) { section in
                    Label(section.title, systemImage: section.symbol)
                        .badge(badge(section))
                        .tag(section)
                }
            }
            .navigationSplitViewColumnWidth(min: 180, ideal: 200, max: 240)
            .safeAreaInset(edge: .bottom) { footer }
        } detail: {
            Group {
                switch store.section {
                case .leftovers: LeftoversView()
                case .sessions: SessionsView()
                case .disk: DiskView()
                case .settings: SettingsView()
                }
            }
            .navigationTitle(store.section.title)
            .toolbar {
                ToolbarItem(placement: .navigation) {
                    Button {
                        store.refresh()
                    } label: {
                        Label("刷新", systemImage: "arrow.clockwise")
                    }
                    .disabled(store.scanning)
                    .keyboardShortcut("r")
                    .help("立即重新扫描（⌘R）")
                }
            }
        }
        .overlay(alignment: .bottom) {
            if let toast = store.toast {
                ToastView(toast: toast)
                    .padding(.bottom, 20)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.snappy(duration: 0.25), value: store.toast)
        .frame(minWidth: 820, minHeight: 520)
        .onAppear {
            NSApp.setActivationPolicy(.regular)
            NSApp.activate(ignoringOtherApps: true)
            store.refresh()
        }
        .onDisappear { NSApp.setActivationPolicy(.accessory) }
    }

    private func badge(_ section: Pane) -> Int {
        switch section {
        case .leftovers: store.leftoverCount
        case .sessions: store.sessions.count
        default: 0
        }
    }

    private var footer: some View {
        HStack(spacing: 6) {
            Circle()
                .fill(store.scanning ? Theme.idle : Theme.busy)
                .frame(width: 6, height: 6)
            Text(store.scanning ? "正在扫描…" : "更新于 \(store.scan.scannedAt.formatted(date: .omitted, time: .standard))")
                .font(.caption2)
                .foregroundStyle(.secondary)
            Spacer()
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }
}
