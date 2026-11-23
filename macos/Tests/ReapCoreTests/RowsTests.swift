import XCTest
@testable import ReapCore

final class RowsTests: XCTestCase {
    private let dead = Owner(agent: .claude, session: "s1", alive: false)

    private func item(_ pid: Int32, ppid: Int32 = 1, pgid: Int32? = nil, exe: String, cmd: String,
                      marked: Bool = true, start: Double = 0) -> ProcessItem {
        ProcessItem(pid: pid, ppid: ppid, pgid: pgid ?? pid, startTime: start, exe: exe, cmd: cmd, owner: dead, marked: marked)
    }

    func testAppHelpersFoldIntoApp() {
        let app = "/Caches/ms-playwright/chrome-mac/Chromium.app"
        let rows = Rows.fold([
            item(10, exe: "/usr/local/bin/node", cmd: "node test.js"),
            item(11, ppid: 10, exe: app + "/Contents/MacOS/Chromium", cmd: "Chromium", marked: false),
            item(12, ppid: 11, exe: app + "/Contents/Frameworks/H.app/Contents/MacOS/Helper", cmd: "Helper", marked: false),
            item(13, ppid: 11, exe: app + "/Contents/Frameworks/H.app/Contents/MacOS/Helper", cmd: "Helper", marked: false),
        ])
        XCTAssertEqual(rows.map(\.pid), [10, 11])
        XCTAssertEqual(rows[1].helpers.map(\.pid), [12, 13])
    }

    func testWrapperShellFoldsIntoCommandOfSameGroup() {
        let wrap = "/bin/zsh -c source snap.sh && eval 'npm run dev' < /dev/null && pwd -P >| /tmp/x"
        let rows = Rows.fold([
            item(20, pgid: 20, exe: "/bin/zsh", cmd: wrap, marked: false),
            item(21, ppid: 20, pgid: 20, exe: "/usr/local/bin/node", cmd: "node vite"),
        ])
        XCTAssertEqual(rows.map(\.pid), [21])
        XCTAssertEqual(rows[0].helpers.map(\.pid), [20])
    }

    func testLoneWrapperShowsInnerCommand() {
        let wrap = "/bin/zsh -c source snap.sh && eval 'while true; do echo '\"'\"'hi'\"'\"'; sleep 1; done' < /dev/null && pwd -P >| /tmp/x"
        let rows = Rows.fold([item(30, pgid: 30, exe: "/bin/zsh", cmd: wrap, marked: false)])
        XCTAssertEqual(rows.first?.displayCommand, "while true; do echo 'hi'; sleep 1; done")
    }

    func testGroupingBySessionAndWorktree() {
        var a = item(40, exe: "/usr/local/bin/node", cmd: "node a", start: 200)
        var b = item(41, exe: "/usr/local/bin/node", cmd: "node b", start: 100)
        let c = ProcessItem(pid: 42, ppid: 1, pgid: 42, startTime: 300, exe: "/usr/local/bin/node", cmd: "node c",
                            owner: Owner(agent: .codex, session: nil, alive: false), marked: true)
        a.cwd = "/repo"
        b.cwd = "/repo"
        let groups = Rows.group([a, b, c]) { _, _ in (project: "repo", title: "修复登录") }
        XCTAssertEqual(groups.count, 2)
        XCTAssertEqual(groups[0].items.map(\.pid), [41, 40], "组内按启动时间排序")
        XCTAssertEqual(groups[0].title, "修复登录")
        XCTAssertEqual(groups[1].agent, .codex)
        XCTAssertEqual(groups[1].project, "未知项目")
    }

    func testFormat() {
        XCTAssertEqual(Format.bytes(512), "512 B")
        XCTAssertEqual(Format.bytes(5 * 1_073_741_824 + 600 * 1_048_576), "5.6 GB")
        XCTAssertEqual(Format.duration(59), "59 秒")
        XCTAssertEqual(Format.duration(3 * 3600 + 5 * 60), "3 小时 5 分")
        XCTAssertEqual(Format.duration(2 * 86400), "2 天")
    }
}
