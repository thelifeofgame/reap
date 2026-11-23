# 残留进程判定规则（三端共用）

macOS / Windows / Linux 三个实现必须对同一份进程快照给出相同的判定结果。
`spec/agents.json` 是 Agent 定义的唯一来源，`spec/fixtures/*.json` 是共享测试用例，每个平台的测试都要跑全部用例。

## 术语

- **Agent**：Claude Code、Codex、Cursor 等 AI 编码工具。定义见 `agents.json`。
- **宿主进程（host）**：Agent 自身的进程。满足其一即可：
  - 可执行文件名在 `hostNames` 中。Windows 上去掉 `.exe` 并忽略大小写；macOS/Linux 区分大小写（Claude 桌面版的可执行文件叫 `Claude`，不是宿主）；
  - 可执行文件路径（`\` 统一换成 `/`）包含 `hostPathContains` 中任一片段；
  - 在 `hostSessions` 里有记录（算 Claude 的命令行宿主）。npm 安装的 Claude Code 进程名是 `node`，只能靠这条认出来。
    采集端必须核对记录里的启动时间与进程一致，防止 PID 复用。
- **归属（owner）**：`{ agent, session, alive }`。`session` 可以为空，`alive` 为真表示启动它的宿主还活着。
- **残留进程**：有归属且 `alive == false`。
- **Windows 约定**：路径比较（`hostPathContains`、`ignorePathContains`、`ignoreExePrefixes`）一律不区分大小写；
  采集端负责把环境变量名转成大写（Windows 环境变量名不区分大小写），父进程已退出或 PID 已被复用（父进程比子进程晚启动）时把 `ppid` 置 0；
  Windows 没有进程组，`pgid` 填进程自己的 PID。
- **不可读环境**：快照里 `env == null`。macOS 会对系统程序（`/bin/zsh`、`/usr/bin/ssh`）隐藏环境变量，Windows 上受保护进程也读不到。

## 快照格式

```jsonc
{
  "platform": "darwin",            // darwin | windows | linux
  "selfPid": 999,                  // 本工具自身 PID
  "processes": [{
    "pid": 101, "ppid": 100, "pgid": 101,
    "exe": "/usr/local/bin/node",  // 完整路径，读不到时为空串
    "cmd": "node server.js",
    "env": { "CLAUDECODE": "1" },  // null = 读不到
    "own": true                    // 是否当前用户的进程，缺省 true
  }],
  "hostSessions":  { "100": "sess-a" },   // 宿主 PID → 会话 ID（来自 Agent 自己的记录文件）
  "outputSessions": { "205": "sess-b" },  // 正在往 Claude 命令输出目录写的进程 → 会话 ID
  "watchedGroups": { "300": { "agent": "claude", "session": "sess-c", "hostPid": 100 } }
}
```

## 判定步骤

候选进程 = `own == true`，且不是本工具、不是本工具的子孙、不是任何宿主进程。
一个进程若命中某 Agent 的**忽略规则**，就不能归属于该 Agent：

- 可执行文件路径包含 `ignorePathContains` 任一片段；
- 或以 `ignoreExePrefixes[platform]` 任一前缀开头。

之所以要忽略规则：Codex 的 `CODEX_SHELL=1` 会被 ChatGPT/Codex App 自身以及它拉起的浏览器继承，不排除会把用户自己的 Chrome 当成残留杀掉。

宿主的会话：取 `hostSessions[pid]`。没有记录时，沿父进程向上找同一 Agent 的宿主并继承其会话（子 Agent 归属父会话）。

**1. 环境标记。** 对每个可读环境的候选，按 `agents.json` 顺序找第一个 marker 命中且未被忽略的 Agent：
- marker 有 `value` 时要求 `env[marker.env] == value`；没有 `value` 时只要求变量存在。
- `session = env[sessionEnv]`（未定义或缺失则为空）。
- `alive =`（`env[hostPidEnv]` 指向一个仍在运行的该 Agent 宿主）或（祖先链上存在该 Agent 的宿主）。

**2. 输出文件。** 尚无归属、出现在 `outputSessions` 里的进程：归属 Claude，`alive =` 祖先链上有 Claude 宿主，或该会话仍属于某个活着的宿主。

**3. 进程组。** 尚无归属、环境不可读、`pgid` 在 `watchedGroups` 里的进程：归属该组记录的 Agent 和会话，`alive =` 记录里的 `hostPid` 仍是该 Agent 的宿主，或祖先链上有该 Agent 的宿主。

**4. 传播**（最多 5 轮，直到没有变化）：
- 向上：从每个已归属进程沿父链向上，父进程满足「环境不可读、未归属、是候选、PID > 1」就继承同一归属，遇到不满足的停止。
  这样能把「反复重启服务器的那个隐藏环境的 shell 脚本」和服务器一起列出。
- 向下：每个未归属候选沿父链向上找，遇到本工具就停；
  - 先遇到某 Agent 的宿主：只有当宿主是按 `hostNames` 识别的命令行宿主、该进程**环境不可读**、**pgid 与宿主不同**且未被该 Agent 忽略时，才归属该宿主的 Agent 和会话，`alive = true`。
    环境可读却没有标记的，是故意去掉标记启动的，不收。按路径识别的 App 宿主（Cursor、ChatGPT）不适用这一条，否则用户自己在 IDE 里开的终端也会被算进去。
  - 先遇到已归属进程：继承它的归属（前提是未被该 Agent 忽略）。

只共享进程组本身**不**算证据：编辑器或编排器自己启动 Agent 时，会和 Agent 同组。

**5. 重新绑定。** 对每个已归属进程，找同一 Agent 的活宿主：先找祖先链，找不到再看第 1 步里 `hostPidEnv` 指向的宿主（进程被挂到 init/launchd 下时只剩这条线索）。
找到就把归属改为 `{ 该 Agent, 宿主的会话（宿主没有会话时保留原值）, alive: true }`。
Claude Code 在 `/clear` 或续接对话后会换会话 ID，但不会重启已经在跑的进程。

## 输出

```jsonc
"expect": [ { "pid": 201, "agent": "claude", "session": "sess-b", "alive": false } ]
```

`expect` 列出所有有归属的进程（顺序无关），没列出的进程必须没有归属。

## 结束进程

- 结束前再次确认 PID 的启动时间没变（PID 会被复用）。
- 先发温和信号（Unix 上是 `SIGTERM`，Windows 上是 `WM_CLOSE` 或 `taskkill` 不带 `/F`），2 秒后仍在的再强制结束。
- 应用的辅助进程（浏览器每个标签页一个）合并为一行，主进程结束后仍未退出的辅助进程会被强制结束。

## 磁盘清理

- 只有用户点击后才删除；正在使用的东西不会出现在可删列表里。
- Worktree 位置：`<repo>/.claude/worktrees/*`（Claude）和 `~/.codex/worktrees/*/*`（Codex）。
  有进程在里面运行、或被活着的 Agent 锁定的不可删；有未提交改动的移到废纸篓或回收站，不直接删；删除 worktree 不删分支。
- Claude 临时目录：已关闭、且 1 小时内没有写入的会话目录才可删。
- 缓存：只列工具会自动重新下载的缓存（npm、pnpm、Yarn、Gradle、Cargo、pip、uv、Go 等）。
