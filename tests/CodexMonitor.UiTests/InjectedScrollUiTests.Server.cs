using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

public sealed partial class InjectedScrollUiTests
{
    private sealed class PagedServer : IRpcTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly List<JsonObject> _items = [];
        public ConcurrentQueue<string> Cursors { get; } = new();
        public PagedServer()
        {
            for (int i = 0; i < 2400; i++)
            {
                if (i % 400 == 0) Add(new() { ["id"] = "reply-" + i, ["type"] = "agentMessage", ["text"] = "Historical reply " + i + ". " + new string('A', 240) });
                Add(new() { ["id"] = "command-" + i, ["type"] = "commandExecution", ["command"] = "echo " + i, ["status"] = "completed", ["aggregatedOutput"] = "done" });
            }
            Add(new() { ["id"] = "current-user", ["type"] = "userMessage", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Continue the current task" }) }, "turn");
        }
        private void Add(JsonObject item, string turn = "history") => _items.Add(new() { ["item"] = item, ["turnId"] = turn, ["startedAtMs"] = 1000, ["completedAtMs"] = 2000 });
        public async Task SendAsync(string text, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(text); var request = document.RootElement;
            if (request.Field("id").ValueKind == JsonValueKind.Undefined) return;
            if (request.Str("method") is "turn/start" or "turn/steer" or "thread/resume") throw new InvalidOperationException("The fixture must never initiate inference");
            JsonObject response = new() { ["data"] = new JsonArray() };
            if (request.Str("method") == "thread/items/list")
            {
                var cursor = request.Field("params").Str("cursor"); var offset = cursor.Length == 0 ? 0 : int.Parse(cursor);
                Cursors.Enqueue(offset.ToString()); await Task.Delay(110, ct);
                var page = _items.AsEnumerable().Reverse().Skip(offset).Take(100).Select(row => (JsonNode?)row.DeepClone()).ToArray();
                response = new() { ["data"] = new JsonArray(page), ["nextCursor"] = offset + page.Length < _items.Count ? (offset + page.Length).ToString() : null };
            }
            _incoming.Writer.TryWrite(new JsonObject { ["id"] = JsonNode.Parse(request.Field("id").GetRawText()), ["result"] = response }.ToJsonString());
        }
        public void Notify(string method, JsonObject values)
        {
            values["threadId"] = "review-session";
            _incoming.Writer.TryWrite(new JsonObject { ["method"] = method, ["params"] = values }.ToJsonString());
        }
        public async Task<string?> ReceiveAsync(CancellationToken ct)
        { try { return await _incoming.Reader.ReadAsync(ct); } catch (ChannelClosedException) { return null; } }
        public ValueTask DisposeAsync() { _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    [UITestMethod]
    public async Task ASingleWheelAtACollapsedGroupContinuesPastHiddenPagesWithoutDrainingHistory()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Dark", "zh-CN");
        var server = new PagedServer(); var client = new CodexClient(new JsonRpcClient(server));
        window.ReviewAttachTranscriptServer(client); await window.ReviewReloadTranscript(true); window.ReviewAutoCollapse(true);
        await MainWindow.SettleAsync();
        var input = new InjectedInput(window); await input.ActivateAsync();
        Assert.AreEqual(0, window.ReviewTranscriptSurface.Scroll.VerticalOffset, 1);
        await input.WheelAsync(window.ReviewTranscriptSurface, 120, 1);
        await WaitFor(() => window.ReviewTranscriptText.Contains("Historical reply 2000", StringComparison.Ordinal), "Hidden tools inside a collapsed group must not consume the only upward wheel without exposing earlier content");
        await Task.Delay(600);
        Assert.IsLessThanOrEqualTo(6, server.Cursors.Count, "Bounded hidden-page prefetch must not drain the entire conversation");
        Assert.AreEqual(server.Cursors.Count, server.Cursors.Distinct().Count(), "A cursor must not be fetched twice");
        Assert.IsTrue(window.ReviewHasEarlier);
        TestContext.WriteLine("Actual injected wheel loaded bounded server cursors: " + string.Join(",", server.Cursors));
        TestContext.AddResultFile(await window.CaptureReviewAsync("injected-collapsed-server-history"));
    }

    [UITestMethod]
    public async Task LiveUpdatesAndRapidWheelReversalKeepTheReadersAnchorAndCancelStaleSourceWork()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var server = new PagedServer(); var client = new CodexClient(new JsonRpcClient(server));
        window.ReviewAttachTranscriptServer(client); await window.ReviewReloadTranscript(true); window.ReviewAutoCollapse(true);
        server.Notify("turn/started", new() { ["turn"] = new JsonObject { ["id"] = "turn", ["status"] = "inProgress" } });
        var input = new InjectedInput(window); await input.ActivateAsync();
        async Task Updates()
        {
            for (int i = 0; i < 30; i++)
            {
                server.Notify("item/completed", new() { ["turnId"] = "turn", ["item"] = new JsonObject { ["id"] = "live-" + i, ["type"] = "commandExecution", ["command"] = "echo live " + i, ["aggregatedOutput"] = "done", ["status"] = "completed" } });
                await Task.Delay(25);
            }
        }
        await Task.WhenAll(input.WheelAsync(window.ReviewTranscriptSurface, 960, 90, 8), Updates());
        await input.WheelAsync(window.ReviewTranscriptSurface, -480, 12);
        await WaitFor(() => !window.ReviewHistoryIoPending, "Pending server IO must settle after direction reversal"); await MainWindow.SettleAsync();
        var anchor = ScrollAnchor.Capture(window.ReviewTranscriptSurface); Assert.IsNotNull(anchor);
        for (int i = 30; i < 40; i++) server.Notify("item/completed", new() { ["turnId"] = "turn", ["item"] = new JsonObject { ["id"] = "live-" + i, ["type"] = "commandExecution", ["command"] = "echo live " + i, ["status"] = "completed" } });
        await Task.Delay(500); await MainWindow.SettleAsync();
        Assert.AreEqual(anchor.Y, window.ReviewItemY(anchor.Item), 2, "Live records below the reader must not move the visible anchor");
        Assert.AreEqual(server.Cursors.Count, server.Cursors.Distinct().Count());
        TestContext.AddResultFile(await window.CaptureReviewAsync("injected-server-reversal-anchor"));
        await window.ReviewDisconnectTranscriptServer(); await MainWindow.SettleAsync();
        Assert.IsFalse(window.ReviewMessageRows.SelectMany(r => r.Activities).Any(WorkProcessState.IsPending), "A server-only pending view must disappear after disconnect");
    }
}
