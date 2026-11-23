using System.Runtime.InteropServices;

namespace Reap.Windows;

/// <summary>每个进程正在监听的 TCP 端口（IPv4 + IPv6）。</summary>
public static class Ports
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    public static Dictionary<int, List<int>> Listening()
    {
        var result = new Dictionary<int, List<int>>();
        // MIB_TCPROW_OWNER_PID：state, localAddr, localPort, remoteAddr, remotePort, owningPid，共 24 字节。
        Collect(AF_INET, rowSize: 24, portOffset: 8, pidOffset: 20, result);
        // MIB_TCP6ROW_OWNER_PID：localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, owningPid，共 56 字节。
        Collect(AF_INET6, rowSize: 56, portOffset: 20, pidOffset: 52, result);
        foreach (var list in result.Values) list.Sort();
        return result;
    }

    private static void Collect(int family, int rowSize, int portOffset, int pidOffset, Dictionary<int, List<int>> result)
    {
        var size = 0;
        var status = Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (status != ERROR_INSUFFICIENT_BUFFER || size <= 0) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return;
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                var raw = Marshal.ReadInt32(row + portOffset);
                // 端口按网络字节序存在低 16 位。
                var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                var pid = Marshal.ReadInt32(row + pidOffset);
                if (!result.TryGetValue(pid, out var list)) result[pid] = list = [];
                if (!list.Contains(port)) list.Add(port);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
