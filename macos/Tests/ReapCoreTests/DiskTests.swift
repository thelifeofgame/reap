import XCTest
@testable import ReapCore

/// 用临时 git 仓库验证 worktree 的判定和删除。
final class DiskTests: XCTestCase {
    private var repo: String!

    override func setUpWithError() throws {
        try XCTSkipIf(Git.path == nil, "没有 git")
        repo = NSTemporaryDirectory() + "reap-disk-\(UUID().uuidString)"
        try FileManager.default.createDirectory(atPath: repo, withIntermediateDirectories: true)
        repo = URL(fileURLWithPath: repo).resolvingSymlinksInPath().path
        git(["init", "-q", "-b", "main"])
        try "hi".write(toFile: repo + "/a.txt", atomically: true, encoding: .utf8)
        git(["add", "."])
        git(["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init"])
    }

    override func tearDownWithError() throws {
        if let repo { try? FileManager.default.removeItem(atPath: repo) }
    }

    @discardableResult
    private func git(_ args: [String]) -> String {
        Shell.run(Git.path!, ["-C", repo] + args)
    }

    private func find(_ name: String, _ context: Disk.Context) -> DiskItem? {
        Disk.worktrees(context).first { $0.paths.first == "\(repo!)/.claude/worktrees/\(name)" }
    }

    func testCleanWorktreeIsRemovedAndBranchKept() throws {
        git(["worktree", "add", "-q", ".claude/worktrees/wt1", "-b", "agent-wt1"])
        let context = Disk.Context(folders: [repo])
        let item = try XCTUnwrap(find("wt1", context))
        XCTAssertEqual(item.state, .removable)
        XCTAssertEqual(item.agent, .claude)

        XCTAssertNil(Disk.remove(item, context))
        XCTAssertFalse(FileManager.default.fileExists(atPath: repo + "/.claude/worktrees/wt1"))
        XCTAssertTrue(git(["branch", "--list", "agent-wt1"]).contains("agent-wt1"), "分支要保留")
    }

    func testWorktreeWithChangesIsMarkedUncommitted() throws {
        git(["worktree", "add", "-q", ".claude/worktrees/wt2", "-b", "agent-wt2"])
        try "edit".write(toFile: repo + "/.claude/worktrees/wt2/a.txt", atomically: true, encoding: .utf8)
        let item = try XCTUnwrap(find("wt2", Disk.Context(folders: [repo])))
        XCTAssertEqual(item.state, .uncommitted)
    }

    func testWorktreeInUseIsBusyAndRefused() throws {
        git(["worktree", "add", "-q", ".claude/worktrees/wt3", "-b", "agent-wt3"])
        let path = repo + "/.claude/worktrees/wt3"
        let context = Disk.Context(folders: [repo, path + "/src"])
        let item = try XCTUnwrap(find("wt3", context))
        guard case .busy = item.state else { return XCTFail("正在使用的 worktree 必须是 busy，实际 \(item.state)") }
        XCTAssertNotNil(Disk.remove(item, context))
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))
    }

    func testStateChangedSinceListingIsRefused() throws {
        git(["worktree", "add", "-q", ".claude/worktrees/wt4", "-b", "agent-wt4"])
        let path = repo + "/.claude/worktrees/wt4"
        let item = try XCTUnwrap(find("wt4", Disk.Context(folders: [repo])))
        XCTAssertEqual(item.state, .removable)
        // 列出之后有了改动：不能按“干净”删除。
        try "late edit".write(toFile: path + "/a.txt", atomically: true, encoding: .utf8)
        XCTAssertNotNil(Disk.remove(item, Disk.Context(folders: [repo])))
        XCTAssertTrue(FileManager.default.fileExists(atPath: path + "/a.txt"))
    }
}
