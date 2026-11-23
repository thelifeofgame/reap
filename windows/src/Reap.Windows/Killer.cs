using System.ComponentModel;
using System.Diagnostics;
using Reap.Core;

namespace Reap.Windows;

public static class Killer
{
    public sealed class Outcome
    {
        /// <summary>确实结束了的进程数（含子进程）。</summary>
        public int Killed { get; set; }
        /// <summary>结束前占用的内存。</summary>
        public ulong Freed { get; set; }
        /// <summary>关窗口请求无效、被强制结束的。</summary>
        public List<int> Forced { get; } = [];
        /// <summary>扫描后 PID 已换成别的进程（或已自行退出），没有动它。</summary>
        public List<int> Skipped { get; } = [];
    }

    /// <summary>PID 会复用：启动时间对得上才是用户看到的那个进程。</summary>
    public static bool StillSame(int pid, double startTime) =>
        WindowsProcesses.StartTime(pid) is { } now && Math.Abs(now - startTime) < 0.01;

    private static bool IsRunning(Process p)
    {
        try { return !p.HasExited; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return false; }
    }

    /// <summary>
    /// 有窗口的先请求关窗口，宽限期过后强制结束仍未退出的；主进程退出后残留的子进程也一并清掉。
    /// 控制台程序不发 Ctrl+C：它可能和用户自己的终端共用一个控制台，会误伤其他进程。
    /// </summary>
    public static Outcome Terminate(IReadOnlyList<ProcessItem> items, TimeSpan? grace = null)
    {
        var outcome = new Outcome();
        var targets = new List<(ProcessItem Item, Process Process)>();
        foreach (var item in items)
        {
            if (!StillSame(item.Pid, item.StartTime))
            {
                outcome.Skipped.Add(item.Pid);
                continue;
            }
            try { targets.Add((item, Process.GetProcessById(item.Pid))); }
            catch (ArgumentException) { outcome.Skipped.Add(item.Pid); }
        }

        var askedToClose = false;
        foreach (var (_, process) in targets)
        {
            try { askedToClose |= process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
        }
        if (askedToClose)
        {
            var deadline = DateTime.UtcNow + (grace ?? TimeSpan.FromSeconds(2));
            while (DateTime.UtcNow < deadline && targets.Any(t => IsRunning(t.Process))) Thread.Sleep(50);
        }

        foreach (var (item, process) in targets)
        {
            if (!IsRunning(process)) continue;
            try
            {
                process.Kill();
                process.WaitForExit(2000);
                outcome.Forced.Add(item.Pid);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        }

        foreach (var (item, process) in targets)
        {
            if (IsRunning(process)) continue;
            outcome.Killed++;
            outcome.Freed += item.Memory;
            foreach (var helper in item.Helpers)
            {
                if (!StillSame(helper.Pid, helper.StartTime))
                {
                    outcome.Killed++;
                    continue;
                }
                try
                {
                    using var h = Process.GetProcessById(helper.Pid);
                    h.Kill();
                    if (h.WaitForExit(1000)) outcome.Killed++;
                }
                catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException) { }
            }
        }
        foreach (var (_, process) in targets) process.Dispose();
        return outcome;
    }
}
