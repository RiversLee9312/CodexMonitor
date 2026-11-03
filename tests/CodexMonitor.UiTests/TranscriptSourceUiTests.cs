using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class TranscriptSourceUiTests
{
    private readonly List<string> _fixtureFiles = [];
    [TestCleanup]
    public void CleanFixtureFiles() { foreach (var path in _fixtureFiles) File.Delete(path); }
    private sealed class SourceServer : IRpcTransport
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        public TaskCompletionSource<JsonObject> Snapshot { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SendAsync(string value, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(value); var request = document.RootElement;
            if (request.Field("id").ValueKind == JsonValueKind.Undefined) return;
            var response = request.Str("method") == "thread/turns/list" ? await Snapshot.Task.WaitAsync(ct) : new JsonObject { ["data"] = new JsonArray() };
            _messages.Writer.TryWrite(new JsonObject { ["id"] = JsonNode.Parse(request.Field("id").GetRawText()), ["result"] = response.DeepClone() }.ToJsonString());
        }
        public void Notify(string delta) => _messages.Writer.TryWrite(new JsonObject { ["method"] = "item/agentMessage/delta", ["params"] = new JsonObject { ["threadId"] = "review-session", ["itemId"] = "server-item", ["delta"] = delta } }.ToJsonString());
        public void Drop() => _messages.Writer.TryComplete();
        public async Task<string?> ReceiveAsync(CancellationToken ct) { try { return await _messages.Reader.ReadAsync(ct); } catch (ChannelClosedException) { return null; } }
        public ValueTask DisposeAsync() { Drop(); return ValueTask.CompletedTask; }
        public void Complete(string text) => Snapshot.TrySetResult(new JsonObject { ["data"] = new JsonArray(new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = "server-item", ["type"] = "agentMessage", ["text"] = text }) }) });
    }
    private async Task<string> LocalFile(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexMonitor-source-" + Guid.NewGuid() + ".jsonl");
        _fixtureFiles.Add(path); await File.WriteAllTextAsync(path, LocalRecord(text)); return path;
    }
    private static string LocalRecord(string text) => new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) } }.ToJsonString();
    [UITestMethod]
    public async Task ConnectingInvalidatesALateLocalReadAndOnlyReloadsFromTheServer()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var file = await LocalFile("Local"); window.ReviewTranscriptFile(file);
        var late = new TaskCompletionSource<TranscriptSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        window.ReviewLocalReader((_, _) => { reads++; return late.Task; });
        var localRead = window.ReviewReloadTranscript(); await MainWindow.SettleAsync();
        var server = new SourceServer(); window.ReviewAttachTranscriptServer(new(new JsonRpcClient(server)));
        var serverRead = window.ReviewReloadTranscript(); server.Complete("Server snapshot"); await serverRead;
        server.Notify(" + live"); await MainWindow.SettleAsync();
        late.SetResult(new(new() { Id = "review-session" }, new[] { new TranscriptEntry("local", EntryKind.Assistant, "Stale local", default) }));
        await localRead; Assert.AreEqual(1, reads); StringAssert.Contains(window.ReviewTranscriptText, "Server snapshot + live"); Assert.IsFalse(window.ReviewTranscriptText.Contains("Stale local"));
        await window.ReviewReloadTranscript(); Assert.AreEqual(1, reads, "A connected transcript must never read the local file");
    }
    [UITestMethod]
    public async Task ServerSnapshotDoesNotDuplicateNotificationsAlreadyIncludedInItsPage()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var server = new SourceServer(); window.ReviewAttachTranscriptServer(new(new JsonRpcClient(server)));
        var loading = window.ReviewReloadTranscript(); server.Notify(" before"); await MainWindow.SettleAsync();
        server.Complete("Server before"); await loading; server.Notify(" after"); await MainWindow.SettleAsync();
        Assert.AreEqual("Server before after", window.ReviewTranscriptText);
    }
    [UITestMethod]
    public async Task FailedInitialServerHistoryNeverMergesFutureDeltasIntoTheLocalSnapshot()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var file = await LocalFile("Local must not leak into server mode"); window.ReviewTranscriptFile(file); await window.ReviewReloadTranscript();
        var server = new SourceServer(); window.ReviewAttachTranscriptServer(new(new JsonRpcClient(server)));
        server.Snapshot.SetException(new IOException("Synthetic history failure")); await window.ReviewReloadTranscript();
        server.Notify("Server after failure"); await MainWindow.SettleAsync();
        Assert.AreEqual("Server after failure", window.ReviewTranscriptText); Assert.IsTrue(window.ReviewSessionAttached);
    }
    [UITestMethod]
    public async Task OtherServerNotificationsCannotChangeAnUnattachedLocalTranscript()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var file = await LocalFile("Local source"); window.ReviewTranscriptFile(file); await window.ReviewReloadTranscript();
        var server = new SourceServer(); window.SetSessionServerReview(new(new JsonRpcClient(server)));
        server.Notify("Wrong source"); await MainWindow.SettleAsync();
        Assert.AreEqual("Local source", window.ReviewTranscriptText); Assert.IsFalse(window.ReviewSessionAttached);
    }
    [UITestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisconnectSwitchesBackToLocalAndRejectsLateServerUpdates(bool unexpected)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var file = await LocalFile("Local before"); window.ReviewTranscriptFile(file);
        var server = new SourceServer(); window.ReviewAttachTranscriptServer(new(new JsonRpcClient(server)));
        var loading = window.ReviewReloadTranscript(); server.Complete("Server only"); await loading;
        Assert.AreEqual("Server only", window.ReviewTranscriptText);
        await File.WriteAllTextAsync(file, LocalRecord("Local after disconnect"));
        server.Snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously); var pending = window.ReviewReloadTranscript();
        if (unexpected) { server.Drop(); await MainWindow.SettleAsync(); }
        else await window.ReviewDisconnectTranscriptServer();
        await pending; await MainWindow.SettleAsync();
        using var delta = JsonDocument.Parse("""{"threadId":"review-session","itemId":"server-item","delta":" late server data"}""");
        window.NotifyTranscriptReview(new("item/agentMessage/delta", delta.RootElement.Clone()));
        Assert.AreEqual("Local after disconnect", window.ReviewTranscriptText);
        Assert.IsFalse(window.ReviewSessionAttached); Assert.IsFalse(window.ReviewComposerVisible);
        var replacement = new SourceServer(); window.ReviewAttachTranscriptServer(new(new JsonRpcClient(replacement)));
        var reconnecting = window.ReviewReloadTranscript(); replacement.Complete("Replacement server"); await reconnecting;
        server.Notify(" stale after reconnect"); await MainWindow.SettleAsync();
        Assert.AreEqual("Replacement server", window.ReviewTranscriptText);
    }
}
