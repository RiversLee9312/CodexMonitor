using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

public sealed partial class InjectedScrollUiTests
{
    private sealed class RetentionServer : IRpcTransport
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        internal TranscriptEntry[] Entries = [];
        internal static JsonObject Item(TranscriptEntry entry) => entry.Kind == EntryKind.Reasoning
            ? new() { ["id"] = entry.Id, ["type"] = "reasoning", ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = entry.Text }) }
            : new() { ["id"] = entry.Id, ["type"] = "agentMessage", ["text"] = entry.Text };
        public Task SendAsync(string value, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(value); var request = doc.RootElement;
            if (request.Field("id").ValueKind == JsonValueKind.Undefined) return Task.CompletedTask;
            if (request.Str("method") is "turn/start" or "turn/steer" or "thread/resume") throw new InvalidOperationException("A retention fixture must never initiate inference");
            var data = request.Str("method") == "thread/items/list"
                ? Entries.Reverse().Select(entry => (JsonNode?)new JsonObject { ["item"] = Item(entry), ["turnId"] = "history" }).ToArray() : [];
            _messages.Writer.TryWrite(new JsonObject { ["id"] = JsonNode.Parse(request.Field("id").GetRawText()), ["result"] = new JsonObject { ["data"] = new JsonArray(data) } }.ToJsonString());
            return Task.CompletedTask;
        }
        internal void Complete(TranscriptEntry entry, string threadId = "review-session") => _messages.Writer.TryWrite(new JsonObject { ["method"] = "item/completed", ["params"] = new JsonObject
            { ["threadId"] = threadId, ["turnId"] = "history", ["item"] = Item(entry) } }.ToJsonString());
        public async Task<string?> ReceiveAsync(CancellationToken ct) { try { return await _messages.Reader.ReadAsync(ct); } catch (ChannelClosedException) { return null; } }
        public ValueTask DisposeAsync() { _messages.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private static string RetentionRecord(TranscriptEntry entry) => new JsonObject { ["type"] = "event_msg", ["payload"] = new JsonObject
        { ["type"] = "item_completed", ["item"] = RetentionServer.Item(entry) } }.ToJsonString();
    private static Task WriteRetentionFile(string path, TranscriptEntry[] entries) => Task.Run(() => File.WriteAllLinesAsync(path, entries.Select(RetentionRecord)));

    private static TranscriptEntry[] RetentionEntries(bool work) => Enumerable.Range(0, 100).Select(i => work
        ? new TranscriptEntry("retained-" + i, EntryKind.Reasoning, "Summary " + i + ". A short reasoning summary.", DateTimeOffset.Now,
            Reasoning: new("Summary " + i + ". A short reasoning summary.", ""))
        : new TranscriptEntry("retained-" + i, EntryKind.Assistant, "Reply " + i + ". A readable paragraph.", DateTimeOffset.Now)).ToArray();

    private static void AssertAnchor(ScrollAnchor anchor, string context)
    {
        var element = anchor.List.ContainerFromItem(anchor.Item) as FrameworkElement;
        Assert.IsNotNull(element, context + ": the same logical reading item must remain realized");
        Assert.AreEqual(anchor.Y, element.TransformToVisual(anchor.List).TransformPoint(new(0, 0)).Y, 2, context);
        AssertCovered(anchor.List, 24);
    }

    [UITestMethod]
    public async Task LocalReloadKeepsExistingWorkNavigationAliveWhileIoIsPending()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "en-US");
        var entries = RetentionEntries(true); await window.ShowConversationPresentationReviewAsync(entries, false);
        var file = Path.GetTempFileName();
        try
        {
            window.ReviewTranscriptFile(file);
            var waiting = new TaskCompletionSource<TranscriptSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ReviewLocalReader((session, ct) => { started.TrySetResult(); return waiting.Task.WaitAsync(ct); });
            var row = window.ReviewMessageRows[0];
            var read = window.ReviewReloadTranscript(); await started.Task;
            row.HistoryChanged?.Invoke(true);
            var navigatedDuringIo = window.ReviewReadingWork;
            waiting.SetResult(new(new SessionInfo { Id = "review-session", FilePath = file }, entries)); await read;
            Assert.IsTrue(navigatedDuringIo, "A same-source refresh must not invalidate a visible work process's navigation callbacks before its asynchronous read completes");
        }
        finally { File.Delete(file); }
    }

    [UITestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task BurstUpdatesKeepTranscriptAndWorkReadingAnchors(bool server, bool work)
    {
        var window = TestApp.ReviewWindow;
        {
            await window.SetReviewAsync(1180, 800, "Dark", "en-US");
            var entries = RetentionEntries(work);
            await window.ShowConversationPresentationReviewAsync(entries, server);
            if (server) window.ReviewServerWorkAuthority();
            var list = work ? window.ReviewVisualElements<ActivityGroupView>().Single().List : window.ReviewTranscriptSurface;
            var input = new InjectedInput(window); await input.ActivateAsync();
            await input.WheelAsync(list, -120, 12, 15); await WaitForScrollIdle(list.Scroll);
            var anchor = ScrollAnchor.Capture(list); Assert.IsNotNull(anchor);
            var before = list.FirstVisibleIndex;
            Assert.IsGreaterThan(0, before, "The injected mouse wheel must leave the top before exercising updates");
            entries = entries.Select((entry, i) => i < before && i >= before - 2
                ? entry with { Text = entry.Text + "\n\nA paragraph added without inserting a new record.", Reasoning = work ? new(entry.Text + "\n\nA paragraph added without inserting a new record.", "") : null } : entry).ToArray();
            window.ReviewUpdateEntries(entries); await Task.Delay(350); await WaitForScrollIdle(list.Scroll);
            AssertAnchor(anchor, "A same-count height change above the reader");
            for (int batch = 0; batch < 12; batch++)
            {
                // Same-count edits and appended records can arrive before the preceding
                // update's asynchronous anchor restoration has run.
                entries = entries.Select((entry, i) => i < before && i >= before - 2
                    ? entry with { Text = entry.Text + "\n\nAn updated paragraph.", Reasoning = work ? new(entry.Text + "\n\nAn updated paragraph.", "") : null } : entry)
                    .Append(work ? new("appended-" + batch, EntryKind.Tool, "echo update", DateTimeOffset.Now, Tool: new("exec_command", "{\"cmd\":\"echo update\"}", "done", ToolActivityState.Completed))
                        : new("appended-" + batch, EntryKind.Assistant, "New reply", DateTimeOffset.Now)).ToArray();
                window.ReviewUpdateEntries(entries);
                await Task.Delay(4);
            }
            await Task.Delay(450); await WaitForScrollIdle(list.Scroll);
            AssertAnchor(anchor, $"{(server ? "server" : "local")} {(work ? "work" : "transcript")} burst updates");
        }
    }

    [UITestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalAndServerSidebarUpdatesKeepReadingAnchorAcrossBursts(bool remote)
    {
        var window = TestApp.ReviewWindow;
        {
            await window.SetReviewAsync(1180, 800, "Light", "en-US");
            var sessions = Sessions(80); await window.ReviewSessionCatalogAsync(sessions);
            var input = new InjectedInput(window); await input.ActivateAsync();
            await input.WheelAsync(window.ReviewSessionSurface, -120, 8, 12); await WaitForScrollIdle(window.ReviewSessionSurface.Scroll);
            var anchor = ScrollAnchor.Capture(window.ReviewSessionSurface); Assert.IsNotNull(anchor);
            for (int batch = 0; batch < 12; batch++)
            {
                sessions = sessions.Select((s, i) => i == 35 + batch ? s with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(batch + 1), Title = s.Title + " updated" } : s).ToArray();
                await window.ReviewUpdateCatalog(sessions, remote);
            }
            await Task.Delay(450); await WaitForScrollIdle(window.ReviewSessionSurface.Scroll);
            AssertAnchor(anchor, remote ? "Server sidebar burst" : "Local sidebar burst");
        }
    }

    [UITestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task FileReloadsAndServerNotificationsPreserveReadingPosition(bool server, bool work)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "en-US");
        var entries = RetentionEntries(work); var file = Path.GetTempFileName();
        var diagnostics = new List<string>(); ScrollAnchor.Diagnostics = diagnostics.Add;
        try
        {
            await WriteRetentionFile(file, entries); window.ReviewTranscriptFile(file); window.ReviewUsePaging();
            var transport = new RetentionServer { Entries = entries };
            if (server) window.ReviewAttachTranscriptServer(new(new JsonRpcClient(transport)));
            await window.ReviewReloadTranscript(true); await MainWindow.SettleAsync();
            var list = work ? window.ReviewVisualElements<ActivityGroupView>().Single().List : window.ReviewTranscriptSurface;
            var input = new InjectedInput(window); await input.ActivateAsync();
            await input.WheelAsync(list, 120, work ? 3 : 6, 12); await WaitForScrollIdle(list.Scroll);
            var anchor = ScrollAnchor.Capture(list); Assert.IsNotNull(anchor);
            var previous = list.FirstVisibleIndex - 1; Assert.IsGreaterThanOrEqualTo(0, previous);
            Assert.IsTrue(list.Scroll.ScrollableHeight - list.Scroll.VerticalOffset > 100, "The fixture must be reading history, not following the newest content");
            for (int batch = 0; batch < 8; batch++)
            {
                entries[previous] = entries[previous] with { Text = entries[previous].Text + "\n\nA streamed paragraph above the reader." };
                entries = entries.Append(new("new-" + batch, work ? EntryKind.Reasoning : EntryKind.Assistant, "New record " + batch, DateTimeOffset.Now)).ToArray();
                if (server) { transport.Complete(entries[previous]); transport.Complete(entries[^1]); await Task.Delay(10); }
                else { await File.AppendAllLinesAsync(file, new[] { RetentionRecord(entries[previous]), RetentionRecord(entries[^1]) }); await window.ReviewReloadTranscript(); }
            }
            await Task.Delay(500); await WaitForScrollIdle(list.Scroll);
            AssertAnchor(anchor, $"Actual {(server ? "app-server notifications" : "paged local file reload")} / {(work ? "work" : "transcript")}");
            TestContext.AddResultFile(await window.CaptureReviewAsync($"retention-{server}-{work}"));
            if (server)
            {
                await window.ReviewDisconnectTranscriptServer(); await MainWindow.SettleAsync();
                transport.Complete(new("stale", EntryKind.Assistant, "Stale disconnected notification", default));
                await Task.Delay(80); Assert.IsFalse(window.ReviewTranscriptText.Contains("Stale disconnected notification"));
                var localList = work ? window.ReviewVisualElements<ActivityGroupView>().Single().List : window.ReviewTranscriptSurface;
                await input.WheelAsync(localList, 120, 3, 12); await WaitForScrollIdle(localList.Scroll);
                var disconnectedAnchor = ScrollAnchor.Capture(localList); Assert.IsNotNull(disconnectedAnchor);
                await window.ReviewReloadTranscript(); await Task.Delay(300); AssertAnchor(disconnectedAnchor, "Local updates after disconnect");
            }
        }
        finally { ScrollAnchor.Diagnostics = null; TestContext.WriteLine(string.Join("\n", diagnostics.TakeLast(100))); File.Delete(file); }
    }

    [UITestMethod]
    public async Task AFocusedOffscreenToolUpdateDoesNotBringTheReaderBackToTheTool()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "en-US");
        var entries = Enumerable.Range(0, 80).Select(i => new TranscriptEntry("focus-" + i, EntryKind.Tool, "", default,
            Tool: new("exec_command", "{\"cmd\":\"echo " + i + "\"}", "done", ToolActivityState.Completed))).ToArray();
        await window.ShowConversationPresentationReviewAsync(entries, false);
        var input = new InjectedInput(window); await input.ActivateAsync();
        var disclosure = window.ReviewVisualElements<ActivityDisclosure>().Single(d => AutomationProperties.GetAutomationId(d) == "ActivityToggle-focus-0");
        await input.ClickAsync(disclosure); await MainWindow.SettleAsync();
        var list = window.ReviewVisualElements<ActivityGroupView>().Single().List;
        await input.WheelAsync(list, -120, 5, 12, x: .95); await WaitForScrollIdle(list.Scroll);
        var anchor = ScrollAnchor.Capture(list); Assert.IsNotNull(anchor);
        window.ReviewUpdateEntries(entries.Select((e, i) => i == 0 ? e with { Tool = e.Tool! with { Output = "New output" } } : e).ToArray());
        await Task.Delay(400); await WaitForScrollIdle(list.Scroll);
        AssertAnchor(anchor, "Restoring focus after a streamed output update must not request navigation");
    }

    [UITestMethod]
    public async Task ARecycledWorkProcessRetainsItsLogicalReadingItemAfterAnOffscreenUpdate()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "en-US");
        var entries = RetentionEntries(true).Concat(RetentionEntries(false)).Select((e, i) => e with { Id = "recycle-" + i }).ToArray();
        await window.ShowConversationPresentationReviewAsync(entries, false);
        var list = window.ReviewVisualElements<ActivityGroupView>().Single().List;
        var input = new InjectedInput(window); await input.ActivateAsync();
        await input.WheelAsync(list, -120, 3, 12); await WaitForScrollIdle(list.Scroll);
        var anchor = ScrollAnchor.Capture(list); Assert.IsNotNull(anchor);
        var id = ((EntryRow)anchor.Item).Entry.Id;
        await input.WheelAsync(window.ReviewTranscriptSurface, -480, 14, 12, x: .99); await WaitForScrollIdle(window.ReviewTranscriptSurface.Scroll);
        Assert.IsFalse(list.IsLoaded, "The fixture must recycle the work process before updating it");
        window.ReviewUpdateEntries(entries.Select((e, i) => i == 0 ? e with { Text = e.Text + string.Concat(Enumerable.Repeat("\n\nAn updated earlier summary.", 20)),
            Reasoning = new(e.Text + string.Concat(Enumerable.Repeat("\n\nAn updated earlier summary.", 20)), "") } : e).ToArray());
        await input.WheelAsync(window.ReviewTranscriptSurface, 480, 20, 12, x: .99); await WaitForScrollIdle(window.ReviewTranscriptSurface.Scroll);
        await Task.Delay(400);
        var restored = window.ReviewVisualElements<ActivityGroupView>().Single().List;
        var item = restored.Items.Cast<EntryRow>().Single(row => row.Entry.Id == id);
        AssertAnchor(new(restored, item, anchor.Y, restored.InputVersion), "Re-realized work process logical position");
    }

    [UITestMethod]
    public async Task LiveSidebarNotificationsReorderRowsWithoutNavigatingAwayFromTheReader()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "en-US");
        var server = new RetentionServer(); window.ReviewAttachTranscriptServer(new(new JsonRpcClient(server)));
        var sessions = Sessions(80);
        await window.ReviewSessionCatalogAsync([], (_, _, _) => Task.FromResult(new SessionPage(sessions, null)));
        var input = new InjectedInput(window); await input.ActivateAsync();
        await input.WheelAsync(window.ReviewSessionSurface, -120, 5, 12); await WaitForScrollIdle(window.ReviewSessionSurface.Scroll);
        var anchor = ScrollAnchor.Capture(window.ReviewSessionSurface); Assert.IsNotNull(anchor);
        for (int i = 35; i < 48; i++)
        { server.Complete(new("sidebar-record-" + i, EntryKind.Assistant, "New server content", default), sessions[i].Id); await Task.Delay(40); }
        await Task.Delay(550); await window.ReviewWaitSessionQueryAsync(); await WaitForScrollIdle(window.ReviewSessionSurface.Scroll);
        AssertAnchor(anchor, "Actual app-server notifications and sidebar reordering");
        Assert.AreEqual("injected-47", window.ReviewSessionRows[0].Session.Id, "The update notifications must actually reorder the sidebar");
    }

    [UITestMethod, DataRow(false), DataRow(true)]
    public async Task RemovingTheVisibleAnchorKeepsASurvivingVisibleItemInPlace(bool work)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "en-US");
        var entries = RetentionEntries(work); await window.ShowConversationPresentationReviewAsync(entries, false);
        var list = work ? window.ReviewVisualElements<ActivityGroupView>().Single().List : window.ReviewTranscriptSurface;
        var input = new InjectedInput(window); await input.ActivateAsync();
        await input.WheelAsync(list, -120, 3, 12); await WaitForScrollIdle(list.Scroll);
        var original = ScrollAnchor.Capture(list); Assert.IsNotNull(original);
        var next = list.RealizedElements.First(element => element.Index > list.Items.IndexOf(original.Item) && element.TransformToVisual(list).TransformPoint(new(0, 0)).Y < list.ActualHeight);
        var survivor = new ScrollAnchor(list, next.Content, next.TransformToVisual(list).TransformPoint(new(0, 0)).Y, list.InputVersion);
        var deletedId = ((EntryRow)original.Item).Entry.Id;
        window.ReviewUpdateEntries(entries.Where(entry => entry.Id != deletedId).ToArray());
        await Task.Delay(400); await WaitForScrollIdle(list.Scroll);
        AssertAnchor(survivor, "Deleting the leading visible record must preserve another visible record");
    }

    [UITestMethod]
    public async Task AContinuousBurstOfToolOutputKeepsTheNestedOutputReadingPosition()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 900, "Dark", "en-US");
        var output = string.Join("\n", Enumerable.Range(0, 300).Select(i => "Output line " + i));
        var entries = Enumerable.Range(0, 80).Select(i => new TranscriptEntry("output-" + i, EntryKind.Tool, "", default,
            Tool: new("exec_command", "{\"cmd\":\"echo output\"}", i == 0 ? output : "done", ToolActivityState.Running))).ToArray();
        await window.ShowConversationPresentationReviewAsync(entries, false);
        var input = new InjectedInput(window); await input.ActivateAsync();
        var toggle = window.ReviewVisualElements<ActivityDisclosure>().Single(d => AutomationProperties.GetAutomationId(d) == "ActivityToggle-output-0");
        await input.ClickAsync(toggle); await MainWindow.SettleAsync();
        ScrollViewer Output() => window.ReviewVisualElements<ScrollViewer>().First(s => AutomationProperties.GetName(s) == new LocalizationService("en-US")["ToolOutput"]);
        await input.WheelAsync(Output(), -120, 3, 12, y: .3); await WaitForScrollIdle(Output());
        var offset = Output().VerticalOffset; Assert.IsGreaterThan(0, offset);
        for (int batch = 0; batch < 15; batch++)
        {
            entries[0] = entries[0] with { Tool = entries[0].Tool! with { Output = output + "\nStreamed line " + batch, StandardInput = batch >= 7 ? "y\n" : null } };
            window.ReviewUpdateEntries(entries); await Task.Delay(3);
        }
        await Task.Delay(450); await WaitForScrollIdle(Output());
        Assert.AreEqual(offset, Output().VerticalOffset, 2, "A pending detail restoration must carry its original reading offset across the next streamed rebuild");
    }
}
