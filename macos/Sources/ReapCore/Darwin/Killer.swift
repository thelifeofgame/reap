import Darwin
import Foundation

public enum Killer {
    public struct Outcome: Sendable {
        /// 确实结束了的进程数（含辅助进程）。
        public var killed = 0
        /// 结束前占用的内存。
        public var freed: UInt64 = 0
        /// 温和请求无效、被强制结束的。
        public var forced: [Int32] = []
        /// 扫描后 PID 已换成别的进程，没有动它。
        public var skipped: [Int32] = []
    }

    static func isRunning(_ pid: Int32) -> Bool {
        kill(pid, 0) == 0 || errno == EPERM
    }

    static func startTime(of pid: Int32) -> Double? {
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size else { return nil }
        return Double(info.pbi_start_tvsec) + Double(info.pbi_start_tvusec) / 1_000_000
    }

    /// PID 会复用：启动时间对得上才是用户看到的那个进程。
    static func stillSame(_ pid: Int32, _ start: Double) -> Bool {
        guard let now = startTime(of: pid) else { return false }
        return abs(now - start) < 0.01
    }

    /// 先请求退出，宽限期过后强制结束仍未退出的；主进程退出后残留的辅助进程也一并清掉。
    public static func terminate(_ items: [ProcessItem], grace: TimeInterval = 2.0) -> Outcome {
        var outcome = Outcome()
        let targets = items.filter { stillSame($0.pid, $0.startTime) }
        outcome.skipped = items.filter { item in !targets.contains { $0.pid == item.pid } }.map(\.pid)
        let memory = targets.reduce(0) { $0 + $1.memory }

        let pids = targets.map(\.pid)
        for pid in pids { kill(pid, SIGTERM) }
        wait(until: Date().addingTimeInterval(grace)) { !pids.contains(where: isRunning) }

        let stubborn = pids.filter(isRunning)
        for pid in stubborn { kill(pid, SIGKILL) }
        wait(until: Date().addingTimeInterval(1)) { !stubborn.contains(where: isRunning) }
        outcome.forced = stubborn

        let helpers = targets.filter { !isRunning($0.pid) }.flatMap(\.helpers)
        wait(until: Date().addingTimeInterval(1)) { !helpers.contains { isRunning($0.pid) } }
        for helper in helpers where isRunning(helper.pid) && stillSame(helper.pid, helper.startTime) {
            kill(helper.pid, SIGKILL)
        }

        let gone = targets.filter { !isRunning($0.pid) }
        outcome.killed = gone.reduce(0) { $0 + 1 + $1.helpers.filter { !isRunning($0.pid) }.count }
        outcome.freed = gone.count == targets.count ? memory : gone.reduce(0) { $0 + $1.memory }
        return outcome
    }

    private static func wait(until deadline: Date, _ done: () -> Bool) {
        while Date() < deadline, !done() { Thread.sleep(forTimeInterval: 0.05) }
    }
}
