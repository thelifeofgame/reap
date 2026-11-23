using Reap.Core;

namespace Reap.Core.Tests;

public class RowsTests
{
    private static readonly Owner Dead = new(AgentKind.Claude, "s1", false);

    private static ProcessItem Item(int pid, int ppid = 0, string exe = "", string cmd = "", bool marked = true,
                                    double start = 0, Owner? owner = null) =>
        new() { Pid = pid, Ppid = ppid, Pgid = pid, Exe = exe, Cmd = cmd, Marked = marked, StartTime = start, Owner = owner ?? Dead };

    [Fact]
    public void SameExeChildrenAndConhostFoldIntoParent()
    {
        const string chrome = @"C:\Users\u\AppData\Local\ms-playwright\chromium-1140\chrome-win\chrome.exe";
        var rows = Rows.Fold([
            Item(10, exe: @"C:\Program Files\nodejs\node.exe", cmd: "node test.js"),
            Item(11, 10, chrome, "chrome.exe --remote-debugging-pipe", marked: false),
            Item(12, 11, chrome.ToUpperInvariant(), "chrome.exe --type=renderer", marked: false),
            Item(13, 11, chrome, "chrome.exe --type=gpu-process", marked: false),
            Item(14, 10, @"C:\Windows\System32\conhost.exe", "conhost.exe 0x4", marked: false),
        ]);
        Assert.Equal([10, 11], rows.Select(r => r.Pid));
        Assert.Equal([14], rows[0].Helpers.Select(h => h.Pid));
        Assert.Equal([12, 13], rows[1].Helpers.Select(h => h.Pid));
    }

    [Fact]
    public void WrapperShellFoldsIntoItsChild()
    {
        const string wrap = "bash -c -l source snap.sh && eval 'npm run dev' < /dev/null && pwd -P >| /tmp/x";
        var rows = Rows.Fold([
            Item(20, exe: @"C:\Program Files\Git\usr\bin\bash.exe", cmd: wrap, marked: false),
            Item(21, 20, @"C:\Program Files\nodejs\node.exe", "node vite"),
        ]);
        Assert.Equal([21], rows.Select(r => r.Pid));
        Assert.Equal([20], rows[0].Helpers.Select(h => h.Pid));
    }

    [Fact]
    public void LoneWrapperShowsInnerCommand()
    {
        const string wrap = "bash -c source s.sh && eval 'while true; do echo '\"'\"'hi'\"'\"'; sleep 1; done' < /dev/null && pwd -P >| /tmp/x";
        var rows = Rows.Fold([Item(30, exe: "/usr/bin/bash", cmd: wrap, marked: false)]);
        Assert.Equal("while true; do echo 'hi'; sleep 1; done", rows[0].DisplayCommand);
    }

    [Fact]
    public void DisplayCommandDropsLongPath()
    {
        var quoted = Item(1, exe: @"C:\Program Files\nodejs\node.exe", cmd: "\"C:\\Program Files\\nodejs\\node.exe\" server.js --port 3000");
        Assert.Equal("node.exe server.js --port 3000", quoted.DisplayCommand);
        var plain = Item(2, exe: @"C:\tools\python.exe", cmd: @"C:\tools\python.exe -m http.server");
        Assert.Equal("python.exe -m http.server", plain.DisplayCommand);
    }

    [Fact]
    public void GroupsBySessionThenByFolder()
    {
        var a = Item(40, cmd: "node a", start: 200);
        var b = Item(41, cmd: "node b", start: 100);
        var c = Item(42, cmd: "node c", start: 300, owner: new Owner(AgentKind.Codex, null, false));
        a.Cwd = @"C:\work\repo";
        b.Cwd = @"C:\work\repo";
        c.Cwd = @"C:\work\api";
        var groups = Rows.Group([a, b, c], (_, _) => ("repo", "修复登录"));
        Assert.Equal(2, groups.Count);
        Assert.Equal([41, 40], groups[0].Items.Select(i => i.Pid));
        Assert.Equal("修复登录", groups[0].Title);
        Assert.Equal(AgentKind.Codex, groups[1].Agent);
        Assert.Equal("api", groups[1].Project);
    }

    [Fact]
    public void FormatsInChinese()
    {
        Assert.Equal("512 B", Format.Bytes(512));
        Assert.Equal("5.6 GB", Format.Bytes(5UL * 1_073_741_824 + 600UL * 1_048_576));
        Assert.Equal("59 秒", Format.Duration(59));
        Assert.Equal("3 小时 5 分", Format.Duration(3 * 3600 + 5 * 60));
        Assert.Equal("2 天", Format.Duration(2 * 86400));
    }
}
