using System.Globalization;

namespace Reap.Core;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(ulong value)
    {
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        if (unit == 0) return $"{value} B";
        var digits = size >= 100 || unit <= 1 ? "0" : "0.0";
        return size.ToString(digits, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    /// <summary>时长：45 秒、12 分钟、3 小时 5 分、2 天 4 小时。</summary>
    public static string Duration(int seconds)
    {
        var s = Math.Max(0, seconds);
        if (s < 60) return $"{s} 秒";
        if (s < 3600) return $"{s / 60} 分钟";
        if (s < 86400)
        {
            var m = s % 3600 / 60;
            return m == 0 ? $"{s / 3600} 小时" : $"{s / 3600} 小时 {m} 分";
        }
        var h = s % 86400 / 3600;
        return h == 0 ? $"{s / 86400} 天" : $"{s / 86400} 天 {h} 小时";
    }
}
