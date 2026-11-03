using System.Text.Json;
using System.Threading.Channels;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

internal sealed class FakeTransport : IRpcTransport
{
    public Channel<string> Incoming { get; } = Channel.CreateUnbounded<string>();
    public Channel<string> Outgoing { get; } = Channel.CreateUnbounded<string>();
    public Task SendAsync(string message, CancellationToken ct) => Outgoing.Writer.WriteAsync(message, ct).AsTask();
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try { return await Incoming.Reader.ReadAsync(ct); } catch (ChannelClosedException) { return null; }
    }
    public ValueTask DisposeAsync() { Incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    public async Task<JsonElement> SentAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(await Outgoing.Reader.ReadAsync(timeout.Token)); return doc.RootElement.Clone();
    }
    public void Reply(JsonElement request, object result) => Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { id = request.Field("id"), result }));
}

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public void UnloadedServerSummaryDoesNotClaimRuntimeLiveness()
    {
        using var document = JsonDocument.Parse("""{"id":"external","status":{"type":"notLoaded"}}""");
        var session = CodexClient.ParseSession(document.RootElement);
        Assert.IsFalse(session.IsLive); Assert.AreEqual(SessionState.Unknown, session.State);
    }

    [TestMethod]
    public async Task ConcurrentResponsesAreMatchedByIdRatherThanArrivalOrder()
    {
        var transport = new FakeTransport(); await using var rpc = new JsonRpcClient(transport);
        var first = rpc.RequestAsync("a", new { }); var second = rpc.RequestAsync("b", new { });
        var requestA = await transport.SentAsync(); var requestB = await transport.SentAsync();
        transport.Reply(requestB, new { text = "B" }); transport.Reply(requestA, new { text = "A" });
        Assert.AreEqual("A", (await first).Str("text")); Assert.AreEqual("B", (await second).Str("text"));
    }
    [TestMethod]
    public async Task DisconnectFailsPendingRequestsPromptly()
    {
        var transport = new FakeTransport(); await using var rpc = new JsonRpcClient(transport);
        var pending = rpc.RequestAsync("a", new { }); await transport.SentAsync(); transport.Incoming.Writer.TryComplete();
        await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.IsFalse(rpc.IsConnected);
    }
    [TestMethod]
    public async Task CancellationDoesNotBreakLaterRequests()
    {
        var transport = new FakeTransport(); await using var rpc = new JsonRpcClient(transport);
        using var cancelled = new CancellationTokenSource();
        var pending = rpc.RequestAsync("a", new { }, cancelled.Token); var stale = await transport.SentAsync(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        transport.Reply(stale, new { });
        var next = rpc.RequestAsync("b", new { }); var current = await transport.SentAsync(); transport.Reply(current, new { ok = true });
        Assert.IsTrue((await next).Field("ok").GetBoolean());
    }
    [TestMethod]
    public async Task RpcErrorsPreserveCodeAndMessage()
    {
        var transport = new FakeTransport(); await using var rpc = new JsonRpcClient(transport);
        var pending = rpc.RequestAsync("a", new { }); var request = await transport.SentAsync();
        transport.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { id = request.Field("id"), error = new { code = -32602, message = "Turn mismatch" } }));
        var error = await Assert.ThrowsAsync<RpcException>(() => pending); Assert.AreEqual(-32602, error.Code); Assert.AreEqual("Turn mismatch", error.Message);
    }
    [TestMethod]
    public async Task SteerAlwaysIncludesExpectedTurnIdAndDoesNotCreateANewTurn()
    {
        var transport = new FakeTransport(); await using var client = new CodexClient(new JsonRpcClient(transport));
        var sending = client.SendAsync("s1", "只修改测试", "turn1"); var request = await transport.SentAsync();
        Assert.AreEqual("turn/steer", request.Str("method")); Assert.AreEqual("turn1", request.Field("params").Str("expectedTurnId"));
        Assert.AreEqual("只修改测试", request.Field("params").Field("input")[0].Str("text"));
        transport.Reply(request, new { turnId = "turn1" }); Assert.AreEqual("turn1", await sending);
    }
    [TestMethod]
    public async Task IdleSendStartsATurnAndDoesNotOverrideSandboxOrModel()
    {
        var transport = new FakeTransport(); await using var client = new CodexClient(new JsonRpcClient(transport));
        var sending = client.SendAsync("s1", "Hello", null); var request = await transport.SentAsync();
        Assert.AreEqual("turn/start", request.Str("method")); Assert.AreEqual(JsonValueKind.Undefined, request.Field("params").Field("sandboxPolicy").ValueKind);
        transport.Reply(request, new { turn = new { id = "new" } }); Assert.AreEqual("new", await sending);
    }
    [TestMethod]
    public async Task ListingPaginatesBothArchiveStatesAndIncludesEveryAgentSource()
    {
        var transport = new FakeTransport(); await using var client = new CodexClient(new JsonRpcClient(transport));
        var listing = client.ListAsync(); var first = await transport.SentAsync();
        Assert.IsTrue(first.Field("params").Field("sourceKinds").Items().Any(x => x.GetString() == "subAgentThreadSpawn"));
        Assert.HasCount(0, first.Field("params").Field("modelProviders").Items());
        transport.Reply(first, new { data = new[] { new { id = "one" } }, nextCursor = "page2" });
        var second = await transport.SentAsync(); Assert.AreEqual("page2", second.Field("params").Str("cursor"));
        transport.Reply(second, new { data = new[] { new { id = "two" } }, nextCursor = (string?)null });
        var archive = await transport.SentAsync(); Assert.IsTrue(archive.Field("params").Field("archived").GetBoolean());
        transport.Reply(archive, new { data = new[] { new { id = "old" } }, nextCursor = (string?)null });
        var result = await listing; Assert.HasCount(3, result); Assert.IsTrue(result.Single(s => s.Id == "old").IsArchived);
    }
    [TestMethod]
    [DataRow("ws://127.0.0.1:4500", true)]
    [DataRow("ws://localhost:4500", true)]
    [DataRow("ws://[::1]:4500", true)]
    [DataRow("ws://example.com:4500", false)]
    [DataRow("https://localhost", false)]
    [DataRow("ws://user:password@localhost:4500", false)]
    public void WebSocketEndpointsAreLimitedToLoopbackWithoutEmbeddedCredentials(string endpoint, bool expected) => Assert.AreEqual(expected, WebSocketTransport.IsAllowedEndpoint(endpoint));
}
