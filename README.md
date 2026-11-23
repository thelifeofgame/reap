# Reap

清理 AI 编码工具留下的残留进程和磁盘垃圾。支持 Claude Code、Codex、Cursor，macOS 和 Windows 都能用。

Claude Code、Codex 这类 Agent 会顺手启动开发服务器、文件监听器、浏览器。会话关掉以后，这些进程经常还在后台跑，占着内存和端口，worktree 和临时文件也越积越多。Reap 常驻菜单栏（Windows 上是托盘），帮你找到并清理它们。

![Reap 残留进程页](docs/screenshot-leftovers.png)

## 下载

到 [Releases](https://github.com/thelifeofgame/reap/releases/latest) 下载最新版本：

| 系统 | 文件 | 要求 |
| --- | --- | --- |
| macOS | `Reap-<版本>-macos.dmg`（Apple 芯片和 Intel 通用） | macOS 14 及以上 |
| Windows x64 | `Reap-Setup-<版本>-x64.exe` 安装包，或 `Reap-<版本>-x64.zip` 免安装版 | Windows 10 1809 及以上 |
| Windows ARM | `Reap-Setup-<版本>-arm64.exe`，或 `Reap-<版本>-arm64.zip` | Windows 10 1809 及以上 |

**首次打开提示“无法验证开发者”（macOS）**：目前还没有做 Apple 公证。打开 DMG 把 Reap 拖进“应用程序”后，在访达里右键 Reap，选“打开”即可。如果提示“已损坏”，在终端执行：

```bash
xattr -dr com.apple.quarantine /Applications/Reap.app
```

**Windows 提示“Windows 已保护你的电脑”**：安装包还没有代码签名，点“更多信息”，再点“仍要运行”。安装不需要管理员权限。

## 功能

- **残留进程**：找出会话已关闭、但还在后台运行的进程，按会话分组显示内存占用和监听端口，可以一键结束一组或全部。先请求进程退出，2 秒后仍未退出的才强制结束。
- **活跃会话**：列出正在运行的 Agent 会话和它们启动的后台进程。可以设置自动结束空闲太久的 Claude Code 会话，对话记录不会丢，之后用 `claude --resume` 接着聊。
- **磁盘清理**：
  - Agent 创建的 git worktree。有未提交改动的移到废纸篓或回收站，不直接删；删 worktree 不会删分支。
  - 已关闭会话留下的 Claude 临时文件和命令输出。
  - npm、pnpm、Yarn、Gradle、Cargo、pip、uv、Go 等会自动重新下载的缓存。
- **菜单栏/托盘常驻**：显示残留进程占用的内存，可以设置登录时启动。

## 怎么判断哪些是残留

| Agent | 识别方式 |
| --- | --- |
| Claude Code | `CLAUDECODE` 环境变量、会话记录、进程组 |
| Codex | `CODEX_*` 环境变量。Codex/ChatGPT App 自身组件和你自己的浏览器不会被算进去 |
| Cursor（实验） | `CURSOR_AGENT=1`。Cursor 没有公开文档，可能漏报 |

进程归属于某个 Agent，并且启动它的 Agent 已经退出，才算残留。正在使用的进程和文件不会出现在可清理列表里，所有删除和结束操作都要你点了才执行。完整规则见 [spec/DETECTION.md](spec/DETECTION.md)，macOS 和 Windows 共用这一份规则和测试用例。

## 隐私

只在本机读取进程和文件，不联网，不上传任何数据。

## 从源码构建

macOS（需要 Xcode 15+ 或 Swift 5.10+）：

```bash
cd macos
swift test
scripts/build.sh      # 生成 build/Reap.app
scripts/make-dmg.sh   # 生成 build/Reap.dmg
```

Windows（需要 .NET 8 SDK，生成安装包还需要 Inno Setup 6）：

```powershell
cd windows
pwsh scripts/publish.ps1 -Version 0.1.0 -Arch x64
```

## 发布新版本

推送 `v` 开头的标签，GitHub Actions 会自动构建 macOS 和 Windows 安装包并发布到 Releases：

```bash
git tag v0.1.0
git push origin v0.1.0
```

## 反馈

遇到误判（该清的没列出来，或者列出了不该清的），或者想支持新的 Agent，欢迎提 [Issue](https://github.com/thelifeofgame/reap/issues)。新增 Agent 一般只需要改 [spec/agents.json](spec/agents.json)，再补一个 fixture。

## 致谢

进程检测思路和磁盘清理规则参考了 [seshka](https://github.com/jigjoy-ai/seshka)（MIT）。

## 许可证

[MIT](LICENSE)
