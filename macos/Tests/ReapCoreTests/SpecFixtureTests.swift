import XCTest
@testable import ReapCore

/// 跑 spec/fixtures 下的全部共享用例。Windows 和 Linux 版跑的是同一批文件。
final class SpecFixtureTests: XCTestCase {
    private static let specDir = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().appendingPathComponent("spec")

    private struct Expectation: Decodable, Equatable, CustomStringConvertible {
        let pid: Int32
        let agent: String
        let session: String?
        let alive: Bool
        var description: String { "\(pid) \(agent) \(session ?? "nil") \(alive ? "alive" : "dead")" }
    }

    private struct Fixture: Decodable {
        let description: String
        let snapshot: Snapshot
        let expect: [Expectation]
    }

    private struct AgentsFile: Decodable {
        let agents: [AgentRule]
    }

    func testAgentsMatchSpec() throws {
        let data = try Data(contentsOf: Self.specDir.appendingPathComponent("agents.json"))
        let spec = try JSONDecoder().decode(AgentsFile.self, from: data)
        XCTAssertEqual(spec.agents, AgentRule.all, "Agent.swift 与 spec/agents.json 不一致")
    }

    func testAllFixtures() throws {
        let dir = Self.specDir.appendingPathComponent("fixtures")
        let files = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        XCTAssertFalse(files.isEmpty)
        for file in files {
            let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: file))
            let got = Classifier.classify(fixture.snapshot)
                .map { Expectation(pid: $0.key, agent: $0.value.agent.rawValue, session: $0.value.session, alive: $0.value.alive) }
                .sorted { $0.pid < $1.pid }
            let want = fixture.expect.sorted { $0.pid < $1.pid }
            XCTAssertEqual(got, want, "\(file.lastPathComponent)：\(fixture.description)")
        }
    }
}
