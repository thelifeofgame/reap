using System.Runtime.InteropServices;
using System.Text;
using Reap.Core;

namespace Reap.Windows;

/// <summary>
/// 读进程表。命令行、工作目录和环境变量来自目标进程的 PEB（进程环境块），
/// 只对当前用户、未提权的进程能读到；读不到时环境为 null。
/// </summary>
public static class WindowsProcesses
{
    public sealed class Details
    {
        public required ProcessRecord Record { get; init; }
        public string Cwd { get; init; } = "";
    }

    private static readonly string? MySid = CurrentSid();

    public static List<Details> List()
    {
        var entries = new List<(int Pid, int Ppid, string Name)>();
        var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snapshot == Native.InvalidHandle) return [];
        try
        {
            var entry = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            if (Native.Process32FirstW(snapshot, ref entry))
            {
                do entries.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
                while (Native.Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }

        var details = new List<Details>(entries.Count);
        foreach (var (pid, ppid, name) in entries)
        {
            if (pid == 0) continue;
            details.Add(Read(pid, ppid, name));
        }

        // Windows 不会改写孤儿进程的父 PID，父进程退出后这个号可能已经给了别人。
        // 父进程比子进程晚启动，说明它不是真正的父进程。
        var start = details.ToDictionary(d => d.Record.Pid, d => d.Record.StartTime);
        foreach (var d in details)
        {
            var r = d.Record;
            if (r.Ppid > 0 && (!start.TryGetValue(r.Ppid, out var parentStart) || (parentStart > 0 && r.StartTime > 0 && parentStart > r.StartTime)))
                r.Ppid = 0;
        }
        return details;
    }

    private static Details Read(int pid, int ppid, string name)
    {
        var record = new ProcessRecord { Pid = pid, Ppid = ppid, Pgid = pid, Cmd = name, Own = false };
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_VM_READ, false, (uint)pid);
        var canRead = handle != IntPtr.Zero;
        if (!canRead) handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return new Details { Record = record };
        try
        {
            record.Exe = ImagePath(handle);
            record.StartTime = StartTime(handle);
            record.Own = MySid is not null && TokenSid(handle) == MySid;
            var cwd = "";
            if (record.Own && canRead && ReadParameters(handle) is { } p)
            {
                if (p.CommandLine.Length > 0) record.Cmd = p.CommandLine;
                record.Env = p.Environment;
                cwd = p.CurrentDirectory;
            }
            return new Details { Record = record, Cwd = cwd };
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    public static double StartTime(IntPtr handle) =>
        Native.GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
            ? DateTimeOffset.FromFileTime(creation).ToUnixTimeMilliseconds() / 1000.0
            : 0;

    /// <summary>已打开进程的启动时间；打不开返回 null。</summary>
    public static double? StartTime(int pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try { return StartTime(handle); }
        finally { Native.CloseHandle(handle); }
    }

    private static string ImagePath(IntPtr handle)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return Native.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : "";
    }

    /// <summary>私有内存，和任务管理器“内存”一栏口径接近。</summary>
    public static ulong Memory(int pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            return Native.GetProcessMemoryInfo(handle, out var c, (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX>())
                ? c.PrivateUsage.ToUInt64()
                : 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    // MARK: 所属用户

    private static string? CurrentSid()
    {
        var self = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)Environment.ProcessId);
        if (self == IntPtr.Zero) return null;
        try { return TokenSid(self); }
        finally { Native.CloseHandle(self); }
    }

    private static string? TokenSid(IntPtr process)
    {
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token)) return null;
        try
        {
            Native.GetTokenInformation(token, Native.TokenUser, IntPtr.Zero, 0, out var length);
            if (length <= 0) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!Native.GetTokenInformation(token, Native.TokenUser, buffer, length, out _)) return null;
                // TOKEN_USER 的第一个字段是 SID_AND_ATTRIBUTES，SID 指针在最前面。
                var sid = Marshal.ReadIntPtr(buffer);
                if (!Native.ConvertSidToStringSidW(sid, out var text)) return null;
                try { return Marshal.PtrToStringUni(text); }
                finally { Native.LocalFree(text); }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    // MARK: PEB

    private sealed record Parameters(string CommandLine, string CurrentDirectory, Dictionary<string, string>? Environment);

    /// <summary>
    /// 读 RTL_USER_PROCESS_PARAMETERS。64 位与 32 位（WOW64）进程的结构偏移不同：
    /// 64 位：PEB+0x20 → 参数；参数+0x38 当前目录，+0x70 命令行，+0x80 环境指针，+0x3F0 环境大小。
    /// 32 位：PEB32+0x10 → 参数；参数+0x24 当前目录，+0x40 命令行，+0x48 环境指针，+0x290 环境大小。
    /// </summary>
    private static Parameters? ReadParameters(IntPtr handle)
    {
        if (!Environment.Is64BitProcess) return null;
        Native.IsWow64Process(handle, out var wow64);
        try
        {
            return wow64 ? ReadParameters32(handle) : ReadParameters64(handle);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static Parameters? ReadParameters64(IntPtr handle)
    {
        var pbi = new Native.PROCESS_BASIC_INFORMATION();
        if (Native.NtQueryInformationProcess(handle, Native.ProcessBasicInformation, ref pbi, Marshal.SizeOf(pbi), out _) != 0) return null;
        if (ReadPointer64(handle, pbi.PebBaseAddress + 0x20) is not { } parameters || parameters == 0) return null;
        var p = new IntPtr(parameters);
        var cwd = ReadUnicodeString64(handle, p + 0x38);
        var cmd = ReadUnicodeString64(handle, p + 0x70);
        var envAddress = ReadPointer64(handle, p + 0x80) ?? 0;
        var envSize = ReadPointer64(handle, p + 0x3F0) ?? 0;
        return new Parameters(cmd, cwd, ReadEnvironment(handle, new IntPtr(envAddress), envSize));
    }

    private static Parameters? ReadParameters32(IntPtr handle)
    {
        if (Native.NtQueryInformationProcess(handle, Native.ProcessWow64Information, out IntPtr peb32, IntPtr.Size, out _) != 0
            || peb32 == IntPtr.Zero) return null;
        if (ReadUInt32(handle, peb32 + 0x10) is not { } parameters || parameters == 0) return null;
        var p = new IntPtr(parameters);
        var cwd = ReadUnicodeString32(handle, p + 0x24);
        var cmd = ReadUnicodeString32(handle, p + 0x40);
        var envAddress = ReadUInt32(handle, p + 0x48) ?? 0;
        var envSize = ReadUInt32(handle, p + 0x290) ?? 0;
        return new Parameters(cmd, cwd, ReadEnvironment(handle, new IntPtr(envAddress), envSize));
    }

    private static byte[]? ReadBytes(IntPtr handle, IntPtr address, int count)
    {
        if (address == IntPtr.Zero || count <= 0) return null;
        var buffer = new byte[count];
        return Native.ReadProcessMemory(handle, address, buffer, count, out var read) && read.ToInt64() == count ? buffer : null;
    }

    private static long? ReadPointer64(IntPtr handle, IntPtr address) =>
        ReadBytes(handle, address, 8) is { } b ? BitConverter.ToInt64(b) : null;

    private static uint? ReadUInt32(IntPtr handle, IntPtr address) =>
        ReadBytes(handle, address, 4) is { } b ? BitConverter.ToUInt32(b) : null;

    /// <summary>UNICODE_STRING（64 位）：Length(2) MaximumLength(2) 填充(4) Buffer(8)。</summary>
    private static string ReadUnicodeString64(IntPtr handle, IntPtr address)
    {
        if (ReadBytes(handle, address, 16) is not { } s) return "";
        var length = BitConverter.ToUInt16(s, 0);
        var buffer = BitConverter.ToInt64(s, 8);
        return ReadBytes(handle, new IntPtr(buffer), length) is { } text ? Encoding.Unicode.GetString(text) : "";
    }

    /// <summary>UNICODE_STRING（32 位）：Length(2) MaximumLength(2) Buffer(4)。</summary>
    private static string ReadUnicodeString32(IntPtr handle, IntPtr address)
    {
        if (ReadBytes(handle, address, 8) is not { } s) return "";
        var length = BitConverter.ToUInt16(s, 0);
        var buffer = BitConverter.ToUInt32(s, 4);
        return ReadBytes(handle, new IntPtr(buffer), length) is { } text ? Encoding.Unicode.GetString(text) : "";
    }

    /// <summary>环境块是 UTF-16 的 "名=值\0名=值\0\0"。变量名统一转大写（Windows 变量名不区分大小写）。</summary>
    private static Dictionary<string, string>? ReadEnvironment(IntPtr handle, IntPtr address, long size)
    {
        if (address == IntPtr.Zero) return null;
        var length = (int)Math.Clamp(size, 0, 1 << 20);
        if (length == 0) length = 64 * 1024;
        var bytes = ReadBytes(handle, address, length);
        // 大小字段不可靠时，退回一次只读一页再试。
        bytes ??= ReadBytes(handle, address, 4096);
        if (bytes is null) return null;
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf("\0\0", StringComparison.Ordinal);
        if (end >= 0) text = text[..end];
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in text.Split('\0'))
        {
            // "=C:=C:\..." 这类以等号开头的是盘符当前目录，不是变量。
            if (entry.Length == 0 || entry[0] == '=') continue;
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            env[entry[..eq].ToUpperInvariant()] = entry[(eq + 1)..];
        }
        return env.Count == 0 ? null : env;
    }
}
