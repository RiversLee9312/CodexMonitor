using System.Text.Json;
using System.Threading.Channels;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class SessionServerTests
{
    private sealed class Discovery(params DiscoveredServer[] servers) : IServerDiscovery
    {
        public Task<IReadOnlyList<DiscoveredServer>> FindAsync(string home, string executable, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscoveredServer>>(servers);
    }
    private sealed class RuntimeTransport(bool owns, bool unloads = false) : IRpcTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        public List<string> Methods { get; } = [];
        public Task SendAsync(string message, CancellationToken ct)
        {
            using var d = JsonDocument.Parse(message); var request = d.RootElement; var method = request.Str("method"); Methods.Add(method);
            object result = method switch
            {
                "thread/loaded/list" => new { data = owns ? new[] { "target" } : [], nextCursor = (string?)null },
                "thread/read" => new { thread = new { id = "target", status = new { type = unloads ? "notLoaded" : "active" } } },
                "thread/turns/list" => new { data = new[] { new { id = "live-turn", status = "inProgress" } } },
                _ => throw new InvalidOperationException("Lookup unexpectedly called " + method)
            };
            _incoming.Writer.TryWrite(JsonSerializer.Serialize(new { id = request.Field("id"), result })); return Task.CompletedTask;
        }
        public async Task<string?> ReceiveAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [TestMethod]
    public async Task LookupRejectsServersThatOnlyHaveStoredHistoryAndMatchesActualRuntimeOwner()
    {
        var wrong = new RuntimeTransport(false); var owner = new RuntimeTransport(true);
        var locator = new SessionServerLocator(new Discovery(new(new("ws://127.0.0.1:4500")), new(new("ws://127.0.0.1:4501"))),
            (server, _, _, _) => Task.FromResult(new CodexClient(new JsonRpcClient(server.Endpoint.EndsWith("4501") ? owner : wrong))));
        var matches = await locator.FindAsync("target", Path.GetTempPath(), "codex");
        Assert.HasCount(1, matches); Assert.AreEqual("ws://127.0.0.1:4501", matches[0].Server.Endpoint);
        Assert.AreEqual("live-turn", matches[0].Session.ActiveTurnId);
        CollectionAssert.AreEqual(new[] { "thread/loaded/list" }, wrong.Methods);
        Assert.IsTrue(owner.Methods.All(m => m is "thread/loaded/list" or "thread/read" or "thread/turns/list"));
    }
    [TestMethod]
    public async Task ThreadUnloadedDuringDiscoveryIsNotResumed()
    {
        var transport = new RuntimeTransport(true, true);
        var locator = new SessionServerLocator(new Discovery(new DiscoveredServer(new("ws://127.0.0.1:4500"))),
            (_, _, _, _) => Task.FromResult(new CodexClient(new JsonRpcClient(transport))));
        Assert.HasCount(0, await locator.FindAsync("target", Path.GetTempPath(), "codex"));
        Assert.IsFalse(transport.Methods.Contains("thread/resume"));
    }
    [TestMethod]
    public async Task LoadedThreadInventoryPaginatesAndDoesNotReadHistory()
    {
        var transport = new FakeTransport(); await using var client = new CodexClient(new JsonRpcClient(transport));
        var pending = client.LoadedThreadIdsAsync(); var first = await transport.SentAsync();
        Assert.AreEqual("thread/loaded/list", first.Str("method")); transport.Reply(first, new { data = new[] { "a" }, nextCursor = "next" });
        var second = await transport.SentAsync(); Assert.AreEqual("next", second.Field("params").Str("cursor"));
        transport.Reply(second, new { data = new[] { "b" } }); CollectionAssert.AreEquivalent(new[] { "a", "b" }, (await pending).ToArray());
    }
    [TestMethod]
    public async Task ResumeOmitsHistoryAndKeepsRuntimeAndPolicyUnchanged()
    {
        var transport = new FakeTransport(); await using var client = new CodexClient(new JsonRpcClient(transport));
        var pending = client.ResumeAsync("target"); var request = await transport.SentAsync();
        Assert.AreEqual("thread/resume", request.Str("method")); Assert.IsTrue(request.Field("params").Field("excludeTurns").GetBoolean());
        Assert.AreEqual(JsonValueKind.Undefined, request.Field("params").Field("includeTurns").ValueKind);
        Assert.AreEqual(JsonValueKind.Undefined, request.Field("params").Field("model").ValueKind);
        transport.Reply(request, new { thread = new { id = "target", status = new { type = "active" } } });
        Assert.AreEqual(SessionState.Active, (await pending).State);
    }
    [TestMethod]
    public void ToolSummariesExtractCommandsAndPatchPathsWithoutRunningThem()
    {
        Assert.AreEqual("rg --files", new ToolActivity("exec", "{\"code\":\"text(await tools.exec_command({cmd:\\\"rg --files\\\"}));\"}").Summary);
        var patch = new ToolActivity("exec", "*** Begin Patch\n*** Update File: D:/repo/src/app.cs\n@@\n");
        Assert.AreEqual("app.cs", patch.Summary); Assert.AreEqual("ToolFileChanges", patch.CategoryKey);
        Assert.AreEqual(ToolActivityState.Failed, ToolActivity.CompletedState("Process exited with code 7"));
    }
    [TestMethod]
    public void MultiFileCodeModePatchesProduceSeparateAccurateInlineRows()
    {
        var patch = "*** Begin Patch\n*** Update File: D:/repo/a.cs\n@@\n-before\n+after\n+extra\n*** Add File: D:/repo/b.cs\n+new\n*** End Patch";
        var code = "text(await tools.apply_patch(" + JsonSerializer.Serialize(patch) + "));";
        var parts = new ToolActivity("exec", JsonSerializer.Serialize(new { code }), State: ToolActivityState.Completed).DisplayParts();
        Assert.HasCount(2, parts); Assert.AreEqual("a.cs", parts[0].Summary); Assert.AreEqual(2, parts[0].AddedLines); Assert.AreEqual(1, parts[0].RemovedLines);
        Assert.AreEqual("b.cs", parts[1].Summary); Assert.AreEqual(1, parts[1].AddedLines); Assert.AreEqual(0, parts[1].RemovedLines);
    }
    [TestMethod]
    public void ProtocolToolMetadataKeepsFailureDurationAndFileCounts()
    {
        using var command = JsonDocument.Parse("""{"id":"cmd","type":"commandExecution","command":"dotnet test","aggregatedOutput":"failed","status":"completed","exitCode":1,"durationMs":2500}""");
        var item = CodexClient.ParseItem(command.RootElement); Assert.AreEqual(ToolActivityState.Failed, item.Tool?.State); Assert.AreEqual(2500L, item.Tool?.DurationMs);
        using var edits = JsonDocument.Parse("""{"id":"edit","type":"fileChange","status":"completed","changes":[{"path":"D:/repo/a.cs","diff":"--- a\n+++ b\n-old\n+new"}]}""");
        var part = CodexClient.ParseItem(edits.RootElement).Tool!.DisplayParts().Single();
        Assert.AreEqual(1, part.AddedLines); Assert.AreEqual(1, part.RemovedLines);
        var deleted = new ToolActivity("apply_patch", "*** Begin Patch\n*** Delete File: D:/repo/old.cs\n*** End Patch").DisplayParts().Single();
        Assert.IsNull(deleted.AddedLines); Assert.IsNull(deleted.RemovedLines);
    }
}
