import Foundation

public enum Format {
    public static func bytes(_ value: UInt64) -> String {
        let units = ["B", "KB", "MB", "GB", "TB"]
        var size = Double(value)
        var unit = 0
        while size >= 1024, unit < units.count - 1 {
            size /= 1024
            unit += 1
        }
        if unit == 0 { return "\(value) B" }
        return size >= 100 || unit <= 1 ? String(format: "%.0f %@", size, units[unit]) : String(format: "%.1f %@", size, units[unit])
    }

    /// 时长：45 秒、12 分钟、3 小时 5 分、2 天 4 小时。
    public static func duration(_ seconds: Int) -> String {
        let s = max(0, seconds)
        if s < 60 { return "\(s) 秒" }
        if s < 3600 { return "\(s / 60) 分钟" }
        if s < 86400 {
            let m = (s % 3600) / 60
            return m == 0 ? "\(s / 3600) 小时" : "\(s / 3600) 小时 \(m) 分"
        }
        let h = (s % 86400) / 3600
        return h == 0 ? "\(s / 86400) 天" : "\(s / 86400) 天 \(h) 小时"
    }

    /// 相对时间的前半句，例如“3 天”，调用方接“前”“未改动”。
    public static func ago(_ interval: TimeInterval) -> String {
        duration(Int(interval))
    }
}
