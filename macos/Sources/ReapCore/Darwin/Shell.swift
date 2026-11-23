import Foundation

enum Shell {
    /// 运行一个程序并返回标准输出。失败时返回空串，调用方把它当作“没有结果”。
    @discardableResult
    static func run(_ path: String, _ arguments: [String], environment: [String: String]? = nil) -> String {
        let (out, _) = execute(path, arguments, environment: environment)
        return out
    }

    /// 运行一个程序，失败时返回错误输出，成功时返回 nil。
    static func failure(_ path: String, _ arguments: [String]) -> String? {
        let (_, status) = execute(path, arguments, environment: nil, captureError: true)
        return status
    }

    private static func execute(_ path: String, _ arguments: [String], environment: [String: String]?,
                                captureError: Bool = false) -> (String, String?) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = arguments
        if let environment {
            process.environment = ProcessInfo.processInfo.environment.merging(environment) { $1 }
        }
        let out = Pipe()
        let err = Pipe()
        process.standardOutput = out
        process.standardError = err
        process.standardInput = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return ("", error.localizedDescription)
        }
        // 先读完再等退出，输出超过管道缓冲区时才不会卡住。
        let data = out.fileHandleForReading.readDataToEndOfFile()
        let errData = err.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let text = String(decoding: data, as: UTF8.self)
        guard process.terminationStatus != 0 else { return (text, nil) }
        let message = String(decoding: errData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return (text, captureError ? (message.isEmpty ? "退出码 \(process.terminationStatus)" : message) : nil)
    }
}
