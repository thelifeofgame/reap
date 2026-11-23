import Darwin
import Foundation

/// 直接用内核接口读进程表，不解析 ps 的输出（命令行和路径里的空格会让 ps 的列错位）。
enum DarwinProcesses {
    struct Entry {
        let pid: Int32
        let ppid: Int32
        let pgid: Int32
        let uid: uid_t
        let startTime: Double
        let comm: String
    }

    struct Arguments {
        let exe: String
        let argv: [String]
        /// nil：环境被系统隐藏（系统自带程序、受保护进程）。
        let env: [String: String]?
    }

    static func list() -> [Entry] {
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_ALL, 0]
        var size = 0
        guard sysctl(&mib, 4, nil, &size, nil, 0) == 0 else { return [] }
        // 两次调用之间可能多出进程，留点余量。
        size += size / 8
        let stride = MemoryLayout<kinfo_proc>.stride
        var buffer = [kinfo_proc](repeating: kinfo_proc(), count: size / stride)
        guard sysctl(&mib, 4, &buffer, &size, nil, 0) == 0 else { return [] }
        return buffer.prefix(size / stride).map { kp in
            var comm = kp.kp_proc.p_comm
            let name = withUnsafeBytes(of: &comm) { raw in
                String(decoding: raw.prefix { $0 != 0 }, as: UTF8.self)
            }
            let start = kp.kp_proc.p_un.__p_starttime
            return Entry(
                pid: kp.kp_proc.p_pid,
                ppid: kp.kp_eproc.e_ppid,
                pgid: kp.kp_eproc.e_pgid,
                uid: kp.kp_eproc.e_ucred.cr_uid,
                startTime: Double(start.tv_sec) + Double(start.tv_usec) / 1_000_000,
                comm: name
            )
        }
    }

    private static let argMax: Int = {
        var mib: [Int32] = [CTL_KERN, KERN_ARGMAX]
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        return sysctl(&mib, 2, &value, &size, nil, 0) == 0 ? Int(value) : 1 << 20
    }()

    /// 只对当前用户自己的进程有效。
    static func arguments(of pid: Int32, buffer: inout [UInt8]) -> Arguments? {
        if buffer.count < argMax { buffer = [UInt8](repeating: 0, count: argMax) }
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = buffer.count
        guard sysctl(&mib, 3, &buffer, &size, nil, 0) == 0, size > MemoryLayout<Int32>.size else { return nil }

        return buffer.withUnsafeBufferPointer { raw -> Arguments? in
            let bytes = UnsafeBufferPointer(rebasing: raw[0..<size])
            let argc = Int(bytes.baseAddress!.withMemoryRebound(to: Int32.self, capacity: 1) { $0.pointee })
            var at = MemoryLayout<Int32>.size

            func nextString() -> String? {
                guard at < bytes.count else { return nil }
                let start = at
                while at < bytes.count, bytes[at] != 0 { at += 1 }
                let text = String(decoding: UnsafeBufferPointer(rebasing: bytes[start..<at]), as: UTF8.self)
                at += 1
                return text
            }

            let exe = nextString() ?? ""
            while at < bytes.count, bytes[at] == 0 { at += 1 }
            var argv: [String] = []
            for _ in 0..<argc {
                guard let arg = nextString() else { break }
                argv.append(arg)
            }
            var env: [String: String] = [:]
            while let entry = nextString(), !entry.isEmpty {
                guard let eq = entry.firstIndex(of: "=") else { continue }
                env[String(entry[..<eq])] = String(entry[entry.index(after: eq)...])
            }
            return Arguments(exe: exe, argv: argv, env: env.isEmpty ? nil : env)
        }
    }

    static func path(of pid: Int32) -> String {
        var buffer = [CChar](repeating: 0, count: Int(MAXPATHLEN) * 4)
        let length = proc_pidpath(pid, &buffer, UInt32(buffer.count))
        return length > 0 ? String(cString: buffer) : ""
    }

    /// 实际占用内存，与活动监视器的“内存”一栏一致。比 RSS 多算了压缩内存，模拟器、虚拟机主要占的就是这部分。
    static func footprint(of pid: Int32) -> UInt64 {
        var info = rusage_info_v4()
        let status = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) {
                proc_pid_rusage(pid, RUSAGE_INFO_V4, $0)
            }
        }
        return status == 0 ? info.ri_phys_footprint : 0
    }

    static func cwd(of pid: Int32) -> String {
        var info = proc_vnodepathinfo()
        let size = Int32(MemoryLayout<proc_vnodepathinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, &info, size) == size else { return "" }
        return withUnsafeBytes(of: &info.pvi_cdir.vip_path) { raw in
            String(cString: raw.bindMemory(to: CChar.self).baseAddress!)
        }
    }

    /// 标准输出或标准错误指向的文件路径。
    static func outputPaths(of pid: Int32) -> [String] {
        [Int32(1), 2].compactMap { fd in
            var info = vnode_fdinfowithpath()
            let size = Int32(MemoryLayout<vnode_fdinfowithpath>.size)
            guard proc_pidfdinfo(pid, fd, PROC_PIDFDVNODEPATHINFO, &info, size) == size else { return nil }
            return withUnsafeBytes(of: &info.pvip.vip_path) { raw in
                String(cString: raw.bindMemory(to: CChar.self).baseAddress!)
            }
        }
    }

    static func children(of pid: Int32) -> [Int32] {
        var pids = [pid_t](repeating: 0, count: 2048)
        let count = proc_listchildpids(pid, &pids, Int32(MemoryLayout<pid_t>.size * pids.count))
        return count > 0 ? Array(pids.prefix(Int(count))).filter { $0 > 0 } : []
    }

    /// 每个进程正在监听的 TCP 端口。lsof 比逐个 fd 查 socket 简单可靠，只对少量 PID 调用。
    static func listeningPorts(of pids: [Int32]) -> [Int32: [Int]] {
        guard !pids.isEmpty else { return [:] }
        let list = pids.map(String.init).joined(separator: ",")
        let output = Shell.run("/usr/sbin/lsof", ["-nP", "-iTCP", "-sTCP:LISTEN", "-a", "-p", list, "-Fpn"])
        var result: [Int32: [Int]] = [:]
        var current: Int32?
        for line in output.split(separator: "\n") {
            if line.hasPrefix("p") {
                current = Int32(line.dropFirst())
            } else if line.hasPrefix("n"), let pid = current,
                      let port = line.split(separator: ":").last.flatMap({ Int($0) }),
                      !(result[pid] ?? []).contains(port) {
                result[pid, default: []].append(port)
            }
        }
        return result.mapValues { $0.sorted() }
    }
}
