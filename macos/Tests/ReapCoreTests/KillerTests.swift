import XCTest
@testable import ReapCore

final class KillerTests: XCTestCase {
    private func spawn(_ args: [String]) throws -> (Process, ProcessItem) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c"] + args
        try p.run()
        let start = try XCTUnwrap(Killer.startTime(of: p.processIdentifier))
        let item = ProcessItem(pid: p.processIdentifier, ppid: getpid(), pgid: p.processIdentifier, startTime: start,
                               exe: "/bin/sh", cmd: "sh", owner: Owner(agent: .claude, session: nil, alive: false), marked: true)
        return (p, item)
    }

    func testPoliteTermination() throws {
        let (p, item) = try spawn(["sleep 30"])
        let outcome = Killer.terminate([item])
        p.waitUntilExit()
        XCTAssertEqual(outcome.killed, 1)
        XCTAssertTrue(outcome.forced.isEmpty)
    }

    func testForceKillsWhatIgnoresTerm() throws {
        let (p, item) = try spawn(["trap '' TERM; while :; do sleep 0.1; done"])
        Thread.sleep(forTimeInterval: 0.2)
        let outcome = Killer.terminate([item], grace: 0.5)
        p.waitUntilExit()
        XCTAssertEqual(outcome.forced, [item.pid])
        XCTAssertFalse(Killer.isRunning(item.pid))
    }

    func testReusedPidIsNotTouched() throws {
        let (p, item) = try spawn(["sleep 30"])
        defer { p.terminate() }
        // 模拟“扫描后 PID 被别的进程复用”：启动时间对不上。
        let stale = ProcessItem(pid: item.pid, ppid: item.ppid, pgid: item.pgid, startTime: item.startTime - 100,
                            exe: item.exe, cmd: item.cmd, owner: item.owner, marked: true)
        let outcome = Killer.terminate([stale])
        XCTAssertEqual(outcome.skipped, [item.pid])
        XCTAssertTrue(Killer.isRunning(item.pid))
    }
}
