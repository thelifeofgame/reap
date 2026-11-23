using Microsoft.UI.Dispatching;
using Reap.Core;
using Reap.Windows;

namespace Reap.App;

/// <summary>全应用共享的状态。后台线程扫描、结束进程，完成后回到界面线程发 Changed。</summary>
public sealed class AppState
{
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _timer;

    public AppState(DispatcherQueue ui)
    {
        _ui = ui;
        Settings = Settings.Load();
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public Settings Settings { get; }
    public ScanResult Scan { get; private set; } = new();
    public bool Scanning { get; private set; }
    public bool HasScanned { get; private set; }
    public HashSet<int> Killing { get; } = [];
    public ulong FreedTotal { get; private set; }
    public DateTimeOffset? LastAutoEnd { get; private set; }

    public List<DiskItem> Worktrees { get; private set; } = [];
    public List<DiskItem> Caches { get; private set; } = [];
    public bool DiskLoading { get; private set; }
    public bool CachesLoading { get; private set; }
    public HashSet<string> Removing { get; } = [];

    /// <summary>任何状态变化。界面收到后重绘。</summary>
    public event Action? Changed;
    /// <summary>给用户的一句提示，第二个参数表示是否出错。</summary>
    public event Action<string, bool>? Toast;

    private void Notify() => Changed?.Invoke();
    private void Show(string text, bool error = false) => Toast?.Invoke(text, error);

    // MARK: 派生数据

    public List<LeftoverGroup> Leftovers => Scan.Leftovers.Where(g => Settings.EnabledAgents.Contains(g.Agent)).ToList();
    public List<ActiveSession> Sessions => Scan.Sessions.Where(s => Settings.EnabledAgents.Contains(s.Agent)).ToList();
    public int LeftoverCount => Leftovers.Sum(g => g.ProcessCount);
    public ulong LeftoverMemory => Leftovers.Aggregate(0UL, (sum, g) => sum + g.Memory);

    public List<ActiveSession> IdleSessions =>
        Sessions.Where(s => !s.Managed && (s.IdleSeconds() ?? -1) >= Settings.IdleLimitMinutes * 60).ToList();

    public ulong DiskReclaimable =>
        Worktrees.Concat(Caches).Where(i => i.State != DiskState.Busy).Aggregate(0UL, (sum, i) => sum + (i.Bytes ?? 0));

    // MARK: 扫描

    public void Refresh()
    {
        if (Scanning) return;
        Scanning = true;
        Notify();
        Task.Run(() =>
        {
            ScanResult result;
            try { result = Scanner.Scan(); }
            catch (Exception e)
            {
                _ui.TryEnqueue(() =>
                {
                    Scanning = false;
                    Show("扫描失败：" + e.Message, true);
                    Notify();
                });
                return;
            }
            _ui.TryEnqueue(() =>
            {
                Scan = result;
                Scanning = false;
                HasScanned = true;
                Notify();
                EndIdleIfDue();
            });
        });
    }

    // MARK: 结束进程

    public void Kill(IReadOnlyList<ProcessItem> items, string? label = null)
    {
        var targets = items.Where(i => !Killing.Contains(i.Pid)).ToList();
        if (targets.Count == 0) return;
        foreach (var t in targets) Killing.Add(t.Pid);
        Notify();
        Task.Run(() =>
        {
            var outcome = Killer.Terminate(targets);
            _ui.TryEnqueue(() =>
            {
                foreach (var t in targets) Killing.Remove(t.Pid);
                FreedTotal += outcome.Freed;
                var text = label ?? $"已结束 {outcome.Killed} 个进程";
                if (outcome.Freed > 0) text += $"，释放 {Format.Bytes(outcome.Freed)}";
                if (outcome.Skipped.Count > 0) text += $"（{outcome.Skipped.Count} 个已自行退出）";
                Show(text);
                Refresh();
            });
        });
    }

    public void KillAll() => Kill(Leftovers.SelectMany(g => g.Items).ToList());

    public void End(IReadOnlyList<ActiveSession> sessions, bool automatic = false)
    {
        var targets = sessions.Where(s => !s.Managed).ToList();
        if (targets.Count == 0) return;
        if (automatic) LastAutoEnd = DateTimeOffset.Now;
        Kill(targets.SelectMany(s => s.Items.Prepend(s.Host)).ToList(),
             $"{(automatic ? "已自动结束" : "已结束")} {targets.Count} 个会话");
    }

    private void EndIdleIfDue()
    {
        if (!Settings.AutoEndIdle) return;
        var due = IdleSessions.Where(s => !Killing.Contains(s.Host.Pid)).ToList();
        if (due.Count > 0) End(due, automatic: true);
    }

    // MARK: 磁盘

    public void LoadDisk()
    {
        if (DiskLoading || CachesLoading) return;
        DiskLoading = CachesLoading = true;
        Notify();
        var context = Disk.Context.From(Scan);
        Task.Run(() =>
        {
            var worktrees = Disk.Worktrees(context);
            _ui.TryEnqueue(() =>
            {
                Worktrees = worktrees;
                DiskLoading = false;
                Notify();
            });
            // worktree 先列出来，大小随后补上。
            var measured = worktrees.Select(w => w with { Bytes = w.Paths.Aggregate(0UL, (s, p) => s + Disk.Measure(p)) }).ToList();
            var caches = Disk.Caches();
            _ui.TryEnqueue(() =>
            {
                Worktrees = measured;
                Caches = caches;
                CachesLoading = false;
                Notify();
            });
        });
    }

    public void Remove(DiskItem item)
    {
        if (!Removing.Add(item.Id)) return;
        Notify();
        var context = Disk.Context.From(Scan);
        Task.Run(() =>
        {
            var error = Disk.Remove(item, context);
            _ui.TryEnqueue(() =>
            {
                Removing.Remove(item.Id);
                if (error is not null)
                {
                    Show($"{item.Title}：{error}", true);
                }
                else
                {
                    Worktrees.RemoveAll(i => i.Id == item.Id);
                    Caches.RemoveAll(i => i.Id == item.Id);
                    var how = item.State == DiskState.Uncommitted ? "已移到回收站" : "已清理";
                    Show($"{how}「{item.Title}」" + (item.Bytes is { } b ? $"，释放 {Format.Bytes(b)}" : ""));
                }
                Notify();
            });
        });
    }

    public void SaveSettings()
    {
        Settings.Save();
        Notify();
    }
}
