using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reap.Core;

namespace Reap.Core.Tests;

/// <summary>跑 spec/fixtures 下的全部共享用例。macOS 和 Linux 版跑的是同一批文件。</summary>
public class SpecFixtureTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static string SpecDir([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", "spec"));

    private sealed record Expectation(int Pid, string Agent, string? Session, bool Alive);

    private sealed class Fixture
    {
        public string Description { get; set; } = "";
        public Snapshot Snapshot { get; set; } = new();
        public List<Expectation> Expect { get; set; } = [];
    }

    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(Path.Combine(SpecDir(), "fixtures"), "*.json").Order().Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Files))]
    public void FixtureMatches(string file)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(SpecDir(), "fixtures", file)), Json)!;
        var got = Classifier.Classify(fixture.Snapshot)
            .Select(e => new Expectation(e.Key, e.Value.Agent.Id(), e.Value.Session, e.Value.Alive))
            .OrderBy(e => e.Pid).ToList();
        var want = fixture.Expect.OrderBy(e => e.Pid).ToList();
        Assert.True(want.SequenceEqual(got),
            $"{file}：{fixture.Description}\n期望：{string.Join("; ", want)}\n实际：{string.Join("; ", got)}");
    }

    private sealed class SpecMarker
    {
        public string Env { get; set; } = "";
        public string? Value { get; set; }
    }

    private sealed class SpecAgent
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Experimental { get; set; }
        public List<string> HostNames { get; set; } = [];
        public List<string> HostPathContains { get; set; } = [];
        public List<SpecMarker> Markers { get; set; } = [];
        public string? SessionEnv { get; set; }
        public string? HostPidEnv { get; set; }
        public List<string> IgnorePathContains { get; set; } = [];
        public Dictionary<string, List<string>> IgnoreExePrefixes { get; set; } = [];
    }

    private sealed class SpecAgents
    {
        public List<SpecAgent> Agents { get; set; } = [];
    }

    [Fact]
    public void AgentsMatchSpec()
    {
        var spec = JsonSerializer.Deserialize<SpecAgents>(File.ReadAllText(Path.Combine(SpecDir(), "agents.json")), Json)!;
        Assert.Equal(spec.Agents.Count, AgentRule.All.Count);
        for (var i = 0; i < spec.Agents.Count; i++)
        {
            var s = spec.Agents[i];
            var c = AgentRule.All[i];
            Assert.Equal(s.Id, c.Id);
            Assert.Equal(s.Name, c.Name);
            Assert.Equal(s.Experimental, c.Experimental);
            Assert.Equal(s.HostNames, c.HostNames);
            Assert.Equal(s.HostPathContains, c.HostPathContains);
            Assert.Equal(s.Markers.Select(m => (m.Env, m.Value)), c.Markers.Select(m => (m.Env, m.Value)));
            Assert.Equal(s.SessionEnv, c.SessionEnv);
            Assert.Equal(s.HostPidEnv, c.HostPidEnv);
            Assert.Equal(s.IgnorePathContains, c.IgnorePathContains);
            Assert.Equal(s.IgnoreExePrefixes.Keys.Order(), c.IgnoreExePrefixes.Keys.Order());
            foreach (var key in s.IgnoreExePrefixes.Keys)
                Assert.Equal(s.IgnoreExePrefixes[key], c.IgnoreExePrefixes[key]);
        }
    }
}
