using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ProtocolCoverageTests
{
    [TestMethod]
    public void InternalFileMapsAndConnectorContextNormalizeWithoutLosingDiffsErrorsOrTiming()
    {
        using var patch = JsonDocument.Parse("""{"id":"p","type":"FileChange","status":"Completed","changes":{"D:/New.cs":{"type":"Add","content":"first\nsecond\n"},"D:/Old.cs":{"type":"Update","unified_diff":"@@\n-old\n+new","move_path":"D:/Renamed.cs"}},"stdout":"Applied","stderr":""}""");
        var activity = TranscriptItemAdapter.Parse(patch.RootElement).Tool!;
        var parts = activity.DisplayParts(); Assert.HasCount(2, parts); Assert.AreEqual("New.cs", parts[0].Summary); Assert.AreEqual(2, parts[0].AddedLines);
        Assert.AreEqual(1, parts[1].AddedLines); Assert.AreEqual(1, parts[1].RemovedLines); Assert.AreEqual("Applied", activity.Output);
        using var mcp = JsonDocument.Parse("""{"id":"m","type":"McpToolCall","server":"github","tool":"search","appName":"GitHub","actionName":"Search code","duration":{"secs":2,"nanos":0},"status":"Completed","result":{"isError":true,"content":[{"type":"text","text":"Unavailable"}]}}""");
        var call = TranscriptItemAdapter.Parse(mcp.RootElement).Tool!; Assert.AreEqual("GitHub · Search code", call.Summary); Assert.AreEqual(2000L, call.DurationMs); Assert.AreEqual(ToolActivityState.Failed, call.State);
    }
    [TestMethod]
    public void RealtimeSegmentsKeepEarlierUtterancesAndUserDirectionAcrossSeveralTurns()
    {
        IReadOnlyList<TranscriptEntry> entries = Array.Empty<TranscriptEntry>();
        void Apply(string method, string json) { using var doc = JsonDocument.Parse(json); entries = TranscriptUpdates.Apply(entries, method, doc.RootElement); }
        Apply("thread/realtime/transcript/delta", """{"role":"assistant","delta":"First"}""");
        Apply("thread/realtime/transcript/done", """{"role":"assistant","text":"First reply"}""");
        Apply("thread/realtime/transcript/delta", """{"role":"assistant","delta":"Second"}""");
        Apply("thread/realtime/transcript/done", """{"role":"assistant","text":"Second reply"}""");
        Apply("thread/realtime/item/started", """{"item":{"type":"transcriptSegment","id":"user1","role":"user","text":""}}""");
        Apply("thread/realtime/item/transcript/delta", """{"itemId":"user1","delta":"Question"}""");
        Apply("thread/realtime/item/completed", """{"item":{"type":"transcriptSegment","id":"user1","role":"user","text":"Question?"}}""");
        Apply("thread/realtime/itemAdded", """{"item":{"type":"message","id":"reply","role":"assistant","content":[{"type":"output_text","text":"Answer"}]}}""");
        Assert.HasCount(4, entries); CollectionAssert.AreEqual(new[] { "First reply", "Second reply", "Question?", "Answer" }, entries.Select(e => e.Text).ToArray());
        Assert.AreEqual(EntryKind.User, entries[2].Kind); Assert.IsTrue(entries.All(e => e.RawJson is null));
    }
    [TestMethod]
    public void EveryNativeThreadItemHasAnExplicitProjectionAndUnknownItemsRetainOnlySafeData()
    {
        foreach (var type in TranscriptItemAdapter.SupportedTypes)
        {
            using var doc = JsonDocument.Parse(new System.Text.Json.Nodes.JsonObject { ["type"] = type, ["id"] = "item", ["durationMs"] = 30000, ["status"] = "completed" }.ToJsonString());
            var entry = TranscriptItemAdapter.Parse(doc.RootElement);
            Assert.IsNull(entry.RawJson, type); if (entry.Kind is not (EntryKind.User or EntryKind.Assistant or EntryKind.Reasoning)) Assert.IsNotNull(entry.Tool, type);
        }
        using var unknown = JsonDocument.Parse("""{"id":"u","type":"futureItem","value":"Retained","encrypted_content":"SECRET"}""");
        var fallback = TranscriptItemAdapter.Parse(unknown.RootElement);
        StringAssert.Contains(fallback.Text, "Retained"); Assert.IsFalse(fallback.Text.Contains("SECRET")); Assert.IsNotNull(fallback.RawJson);
    }
    [TestMethod]
    [DataRow("""{"type":"sleep","id":"s","durationMs":30000}""", "ToolWaiting")]
    [DataRow("""{"type":"Extension","kind":"sleep","id":"s","durationMs":30000}""", "ToolWaiting")]
    [DataRow("""{"type":"dynamicToolCall","id":"s","namespace":"clock","tool":"sleep","arguments":{"duration_ms":30000}}""", "ToolWaiting")]
    [DataRow("""{"type":"mcpToolCall","id":"s","server":"clock","tool":"sleep","arguments":{"duration_ms":30000}}""", "ToolWaiting")]
    [DataRow("""{"type":"dynamicToolCall","id":"c","namespace":"functions","tool":"exec_command","arguments":{"cmd":"pwd"}}""", "ToolCommand")]
    [DataRow("""{"type":"collabAgentToolCall","id":"c","tool":"spawnAgent","receiverThreadIds":["child"],"agentsStates":{"child":{"status":"running"}},"prompt":"Review"}""", "ToolCollaboration")]
    [DataRow("""{"type":"subAgentActivity","id":"c","kind":"started","agentPath":"/root/review","agentThreadId":"child"}""", "AgentStarted")]
    [DataRow("""{"type":"SubAgentActivity","id":"c","kind":"completed","agent_path":"/root/review","agent_thread_id":"child"}""", "AgentCompleted")]
    [DataRow("""{"type":"hookPrompt","id":"h","fragments":[{"hookRunId":"h1","text":"Check the result"}]}""", "EventHook")]
    [DataRow("""{"type":"CommandExecution","id":"c","command":"cat readme","parsed_cmd":[{"type":"Read","path":"readme"}],"aggregated_output":"readable","exit_code":0,"duration":{"secs":1,"nanos":500000000},"status":"Completed"}""", "ToolReadFile")]
    [DataRow("""{"type":"imageGeneration","id":"i","revisedPrompt":"An icon","savedPath":"D:/icon.png","result":"LARGE_BINARY","status":"completed"}""", "ToolImageGeneration")]
    [DataRow("""{"type":"todo_list","id":"p","items":[{"text":"Check layouts","completed":true}]}""", "ToolPlan")]
    public void LocalAndServerAdaptersShareSemantics(string json, string category)
    {
        using var doc = JsonDocument.Parse(json);
        var local = TranscriptParser.ParseResponseItem(doc.RootElement, "local", default)!;
        var server = CodexClient.ParseItem(doc.RootElement);
        Assert.IsNotNull(local.Tool); Assert.IsNotNull(server.Tool);
        Assert.AreEqual(category, local.Tool.CategoryKey); Assert.AreEqual(local.Tool, server.Tool);
        if (category == "ToolWaiting") Assert.AreEqual(30000L, server.Tool.WaitDurationMs);
        if (category == "ToolImageGeneration") Assert.IsFalse(server.Tool.Input.Contains("LARGE_BINARY"));
        if (category == "ToolReadFile") Assert.AreEqual(1500L, server.Tool.DurationMs);
    }
    [TestMethod]
    [DataRow("""{"type":"agent_message","id":"m","author":"/root/reviewer","recipient":"/root","content":[{"type":"input_text","text":"Review complete"},{"type":"encrypted_content","encrypted_content":"SECRET"}]}""", EntryKind.Event)]
    [DataRow("""{"type":"agent_message","id":"m","text":"Review complete"}""", EntryKind.Assistant)]
    public void InterAgentMessagesAreDistinctFromSdkAssistantRepliesAndNeverExposeEncryptedBlocks(string json, EntryKind kind)
    {
        using var doc = JsonDocument.Parse(json); var local = TranscriptParser.ParseResponseItem(doc.RootElement, "m", default)!; var server = CodexClient.ParseItem(doc.RootElement);
        Assert.AreEqual(kind, local.Kind); Assert.AreEqual(kind, server.Kind); Assert.AreEqual("Review complete", local.Text); Assert.AreEqual(local.Text, server.Text);
        Assert.IsFalse((local.Tool?.Input + local.RawJson + local.Text).Contains("SECRET"));
        if (kind == EntryKind.Event) { Assert.AreEqual("AgentMessage", local.Tool?.CategoryKey); StringAssert.Contains(local.Tool!.Summary, "/root/reviewer → /root"); }
    }
    [TestMethod]
    public void ProgressPatchesTerminalInputAndFinalItemsKeepOneIdentity()
    {
        IReadOnlyList<TranscriptEntry> entries = Array.Empty<TranscriptEntry>();
        void Apply(string method, string json) { using var doc = JsonDocument.Parse(json); entries = TranscriptUpdates.Apply(entries, method, doc.RootElement); }
        Apply("item/started", """{"item":{"id":"m","type":"mcpToolCall","server":"github","tool":"search","arguments":{},"status":"inProgress"}}""");
        Apply("item/mcpToolCall/progress", """{"itemId":"m","message":"Searching"}"""); Assert.AreEqual("Searching", entries[0].Tool?.Progress);
        Apply("item/completed", """{"item":{"id":"m","type":"mcpToolCall","server":"github","tool":"search","arguments":{},"result":{"content":[{"type":"text","text":"Found"}]},"status":"completed"}}""");
        Assert.HasCount(1, entries); Assert.IsNull(entries[0].Tool?.Progress);
        Apply("item/fileChange/patchUpdated", """{"itemId":"f","changes":[{"path":"A.cs","diff":"+new"}]}"""); Assert.AreEqual("A.cs", entries[1].Tool!.DisplayParts()[0].Summary);
        Apply("item/commandExecution/terminalInteraction", """{"itemId":"c","stdin":"yes\n"}""");
        Apply("item/completed", """{"item":{"id":"c","type":"commandExecution","command":"command","status":"completed","aggregatedOutput":"Done"}}""");
        Assert.AreEqual("yes\n", entries[2].Tool?.StandardInput); Assert.HasCount(3, entries);
    }
    [TestMethod]
    public void InterruptedWaitIsNotLeftRunningAndRealtimeTextIsNotRawJson()
    {
        using var start = JsonDocument.Parse("""{"item":{"id":"s","type":"sleep","durationMs":1000}}""");
        var entries = TranscriptUpdates.Apply(Array.Empty<TranscriptEntry>(), "item/started", start.RootElement);
        using var stop = JsonDocument.Parse("""{"turn":{"status":"interrupted"}}"""); entries = TranscriptUpdates.Apply(entries, "turn/completed", stop.RootElement);
        Assert.AreEqual(ToolActivityState.Interrupted, entries[0].Tool?.State);
        using var delta = JsonDocument.Parse("""{"role":"assistant","delta":"Hello"}"""); entries = TranscriptUpdates.Apply(entries, "thread/realtime/transcript/delta", delta.RootElement);
        using var done = JsonDocument.Parse("""{"role":"assistant","text":"Hello there"}"""); entries = TranscriptUpdates.Apply(entries, "thread/realtime/transcript/done", done.RootElement);
        Assert.AreEqual("Hello there", entries.Last().Text); Assert.AreEqual(EntryKind.Assistant, entries.Last().Kind);
    }
    [TestMethod]
    public void EveryCataloguedNonStreamingEventHasAStructuredProjectionAndSensitiveTransportDataIsExcluded()
    {
        using var sample = JsonDocument.Parse("""{"threadId":"s","turnId":"t","message":"Update","status":"completed","reviewId":"r","run":{"id":"h","name":"Check"},"showBufferingUi":true,"tokenUsage":{"total":{"totalTokens":10}},"secret":"SECRET","nested":{"refreshToken":"SECRET","encrypted_content":"SECRET","deltaBase64":"SECRET"}}""");
        foreach (var (method, definition) in ProtocolEventCatalog.Notifications.Where(x => x.Value.Route != ProtocolEventRoute.Stream))
        {
            var entry = TranscriptEvents.Parse(method, sample.RootElement); Assert.IsNotNull(entry, method); Assert.IsNotNull(entry.Tool, method);
            Assert.IsFalse(entry.Tool.Input.Contains("SECRET"), method); Assert.IsNull(entry.RawJson, method);
        }
        Assert.HasCount(85, ProtocolEventCatalog.Notifications);
    }
    [TestMethod]
    public void LegacyNotificationEnvelopesNormalizeBeforeApplyingDeltas()
    {
        using var delta = JsonDocument.Parse("""{"conversationId":"s","msg":{"type":"agent_message_delta","item_id":"a","delta":"Partial"}}""");
        var notification = RecordedEvents.Normalize(new("codex/event/agent_message_delta", delta.RootElement));
        Assert.AreEqual("s", notification.Parameters.Str("threadId"));
        var entries = TranscriptUpdates.Apply(Array.Empty<TranscriptEntry>(), notification.Method, notification.Parameters);
        using var done = JsonDocument.Parse("""{"conversationId":"s","msg":{"type":"agent_message","item_id":"a","message":"Finished"}}""");
        notification = RecordedEvents.Normalize(new("codex/event/agent_message", done.RootElement)); entries = TranscriptUpdates.Apply(entries, notification.Method, notification.Parameters);
        Assert.HasCount(1, entries); Assert.AreEqual("Finished", entries[0].Text);
    }
}
