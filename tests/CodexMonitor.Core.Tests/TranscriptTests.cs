using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class TranscriptTests
{
    private string _folder = null!;
    [TestInitialize] public void Setup() { _folder = Path.Combine(Path.GetTempPath(), "CodexMonitor.Tests", Guid.NewGuid().ToString()); Directory.CreateDirectory(_folder); }
    [TestCleanup] public void Cleanup() => Directory.Delete(_folder, true);
    private async Task<TranscriptSnapshot> Parse(params string[] lines)
    {
        var path = Path.Combine(_folder, "rollout.jsonl"); await File.WriteAllLinesAsync(path, lines);
        return await new TranscriptParser().ReadAsync(new SessionInfo { Id = "s", FilePath = path });
    }
    [TestMethod]
    public async Task ProgramBatchConsumesEachNativeMirrorOnceAndRetainsTheProgramOutput()
    {
        const string code = "await Promise.all(['git status','git status'].map(async cmd=>text(await tools.exec_command({cmd}))));";
        var result = await Parse(
            JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "custom_tool_call", call_id = "batch", name = "exec", input = code } }),
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","id":"one","command":["git","status"],"status":"Completed","aggregated_output":"First"}}}""",
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","id":"two","command":["git","status"],"status":"Completed","aggregated_output":"Second"}}}""",
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","id":"three","command":["git","status"],"status":"Completed","aggregated_output":"Separate real execution"}}}""",
            """{"type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"batch","output":"Program aggregate"}}""");
        Assert.HasCount(2, result.Entries); var parent = result.Entries[0].Tool!;
        Assert.AreEqual(code, parent.Input); Assert.AreEqual("Program aggregate", parent.Output); Assert.HasCount(2, parent.DisplayCalls!);
        CollectionAssert.AreEqual(new[] { "First", "Second" }, parent.DisplayCalls!.Select(call => call.Output).ToArray());
        Assert.AreEqual("Separate real execution", result.Entries[1].Tool!.Output);
    }
    [TestMethod]
    public async Task WrappedCommandsAndInternalArgvMirrorsAppearOnceAndKeepFinalTiming()
    {
        var result = await Parse(
            """{"type":"response_item","payload":{"type":"custom_tool_call","call_id":"wrapper","name":"exec","input":"text(await tools.exec_command({cmd:\"git status\"}));"}}""",
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","id":"different-native-id","command":["C:/Program Files/PowerShell/7/pwsh.exe","-Command","git status"],"status":"Completed","aggregated_output":"Clean","exit_code":0,"duration":{"secs":2,"nanos":0}}}}""",
            """{"type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"wrapper","output":"Original orchestration envelope"}}""");
        Assert.HasCount(1, result.Entries); Assert.AreEqual("git status", result.Entries[0].Tool?.Summary); Assert.AreEqual(2000L, result.Entries[0].Tool?.DurationMs);
        Assert.AreEqual("Original orchestration envelope", result.Entries[0].Tool?.Output); Assert.AreEqual("Clean", result.Entries[0].Tool?.DisplayOutput);
    }
    [TestMethod]
    public async Task WireMatchedItemsDoNotConsumeAnotherIdenticalRealExecutionOrDuplicatePatchMirrors()
    {
        var result = await Parse(
            """{"type":"response_item","payload":{"type":"function_call","call_id":"first","name":"shell","arguments":"git status"}}""",
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","id":"first","command":["git","status"],"status":"Completed"}}}""",
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","id":"second","command":["git","status"],"status":"Completed"}}}""",
            """{"type":"response_item","payload":{"type":"custom_tool_call","call_id":"patch","name":"exec","input":"tools.apply_patch(\"*** Begin Patch\\n*** Update File: D:/A.cs\\n@@\\n-old\\n+new\\n*** End Patch\")"}}""",
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"FileChange","id":"native-patch","status":"Completed","changes":{"D:/A.cs":{"type":"Update","unified_diff":"--- a/A.cs\n+++ b/A.cs\n@@ -1 +1 @@\n-old\n+new"}}}}}""");
        Assert.HasCount(3, result.Entries); Assert.HasCount(2, result.Entries.Where(entry => entry.Tool?.Summary == "git status").ToArray()); Assert.HasCount(1, result.Entries.Where(entry => entry.Tool?.CategoryKey == "ToolFileChanges").ToArray());
    }
    [TestMethod]
    public async Task LegacyCommandBeginAndEndCoalesceAndRetainTheCommandOnFailure()
    {
        var result = await Parse(
            """{"type":"event_msg","payload":{"type":"exec_command_begin","call_id":"legacy","command":["pwsh","-NoProfile","-Command","exit 2"]}}""",
            """{"type":"event_msg","payload":{"type":"exec_command_end","call_id":"legacy","aggregated_output":"Failed","exit_code":2}}""");
        Assert.HasCount(1, result.Entries); Assert.AreEqual("exit 2", result.Entries[0].Tool?.Summary); Assert.AreEqual(ToolActivityState.Failed, result.Entries[0].Tool?.State); Assert.AreEqual("Failed", result.Entries[0].Tool?.Output);
    }
    [TestMethod]
    public async Task LegacyPartialTextRemainsReadableAndACompletedMessageReplacesItsDraft()
    {
        var result = await Parse(
            """{"type":"event_msg","payload":{"type":"agent_message_delta","delta":"Partial "}}""",
            """{"type":"event_msg","payload":{"type":"agent_message_delta","delta":"reply"}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","message":"Completed reply"}}""",
            """{"type":"event_msg","payload":{"type":"agent_reasoning_raw_content_delta","delta":"Unfinished thinking"}}""",
            """{"type":"event_msg","payload":{"type":"future_event","message":"Retained","encrypted_content":"SECRET"}}""");
        Assert.HasCount(3, result.Entries); Assert.AreEqual("Completed reply", result.Entries[0].Text);
        Assert.AreEqual("Unfinished thinking", result.Entries.Single(e => e.Kind == EntryKind.Reasoning).Reasoning?.FullText);
        var fallback = result.Entries.Single(e => e.RawJson is not null); StringAssert.Contains(fallback.Text, "Retained"); Assert.IsFalse(fallback.Text.Contains("SECRET"));
    }
    [TestMethod]
    [DataRow("sleep")]
    [DataRow("clock.sleep")]
    public async Task NativeCompletedMirrorsReplaceCallsWithoutDuplicatingMessagesOrWaits(string kind)
    {
        var result = await Parse(
            """{"type":"response_item","payload":{"type":"function_call","call_id":"sleep-call","name":"clock.sleep","arguments":"{\"duration_ms\":30000}"}}""",
            """{"type":"response_item","payload":{"type":"function_call_output","call_id":"sleep-call","output":"Sleep completed."}}""",
            JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "item_completed", item = new { type = "Extension", kind, id = "sleep-call", durationMs = 30000 } } }),
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"a","content":[{"type":"Text","text":"A reply"}]}}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","message":"A reply"}}""");
        Assert.HasCount(2, result.Entries); Assert.AreEqual(30000L, result.Entries[0].Tool?.WaitDurationMs); Assert.AreEqual(ToolActivityState.Completed, result.Entries[0].Tool?.State);
        Assert.AreEqual("A reply", result.Entries[1].Text);
    }
    [TestMethod]
    public async Task UnmirroredEventRepliesSurviveMixedModernAndLegacyRecordsAndRepeatedMessagesRemainDistinct()
    {
        var result = await Parse(
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"text":"Question"}]}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","message":"Reply"}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","message":"Reply"}}""");
        Assert.HasCount(3, result.Entries); Assert.HasCount(2, result.Entries.Where(e => e.Kind == EntryKind.Assistant).ToArray());
    }
    [TestMethod]
    public async Task SdkJsonItemUpdatesAndCompletedMessagesUseTheSameProjection()
    {
        var result = await Parse(
            """{"type":"thread.started","thread_id":"sdk"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.started","item":{"id":"c","type":"command_execution","command":"pwd","status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"c","type":"command_execution","command":"pwd","status":"completed","aggregated_output":"D:/repo","exit_code":0}}""",
            """{"type":"item.completed","item":{"id":"a","type":"agent_message","text":"Finished"}}""",
            """{"type":"turn.completed"}""");
        Assert.HasCount(2, result.Entries); Assert.AreEqual(SessionState.Completed, result.Session.State);
        Assert.AreEqual("ToolCommand", result.Entries[0].Tool?.CategoryKey); Assert.AreEqual("D:/repo", result.Entries[0].Tool?.Output); Assert.AreEqual("Finished", result.Entries[1].Text);
    }

    [TestMethod]
    public async Task PartialTrailingRecordCanBeRetriedAfterConcurrentWriterCompletes()
    {
        var path = Path.Combine(_folder, "rollout.jsonl");
        var prefix = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"text\":\"你好 🌏\"}]}}\n";
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(prefix + "{\"type\":\"event_msg\"")); await writer.FlushAsync();
        var session = new SessionInfo { Id = "s", FilePath = path };
        var first = await new TranscriptParser().ReadAsync(session);
        Assert.HasCount(1, first.Entries); Assert.AreEqual(1, first.InvalidRecords); Assert.AreEqual("你好 🌏", first.Entries[0].Text);
        await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(",\"payload\":{\"type\":\"task_complete\"}}\n")); await writer.FlushAsync();
        var second = await new TranscriptParser().ReadAsync(session);
        Assert.AreEqual(0, second.InvalidRecords); Assert.AreEqual(SessionState.Completed, second.Session.State);
    }

    [TestMethod]
    public async Task EncryptedReasoningAndSystemInstructionsAreNotDisplayed()
    {
        var result = await Parse(
            """{"type":"response_item","payload":{"type":"reasoning","encrypted_content":"SECRET","summary":[{"text":"Public summary"}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"developer","content":[{"text":"PRIVATE INSTRUCTIONS"}]}}""");
        Assert.HasCount(1, result.Entries); Assert.AreEqual("Public summary", result.Entries[0].Text);
        Assert.IsFalse(TranscriptExport.Markdown(result, new("en-US")).Contains("SECRET"));
    }

    [TestMethod]
    public async Task EventAndResponseMessageCopiesDoNotDuplicateConversation()
    {
        var result = await Parse(
            """{"type":"event_msg","payload":{"type":"user_message","message":"Hi"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"text":"Hi"}]}}""");
        Assert.HasCount(1, result.Entries);
    }

    [TestMethod]
    public async Task LegacyMessageEventsAreReadable()
    {
        var result = await Parse("""{"type":"event_msg","payload":{"type":"agent_message","message":"Legacy"}}""");
        Assert.HasCount(1, result.Entries); Assert.AreEqual(EntryKind.Assistant, result.Entries[0].Kind);
    }

    [TestMethod]
    public async Task ActiveTranscriptDoesNotClaimVerifiedProcessLiveness()
    {
        var result = await Parse("""{"type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}""");
        Assert.AreEqual(SessionState.RecordedActive, result.Session.State); Assert.IsFalse(result.Session.IsLive); Assert.AreEqual("t1", result.Session.ActiveTurnId);
    }

    [TestMethod]
    public async Task StopMarkerClearsActiveTurn()
    {
        var result = await Parse("""{"type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}""", """{"type":"event_msg","payload":{"type":"turn_aborted"}}""");
        Assert.AreEqual(SessionState.Interrupted, result.Session.State); Assert.IsNull(result.Session.ActiveTurnId);
    }

    [TestMethod]
    public async Task UnknownRecordsAndMalformedMiddleLinesDoNotHideLaterMessages()
    {
        var result = await Parse("""{"type":"future_record","payload":{"n":1}}""", "not json", """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"text":"After"}]}}""");
        Assert.AreEqual(1, result.UnknownRecords); Assert.AreEqual(1, result.InvalidRecords); Assert.HasCount(2, result.Entries);
        Assert.AreEqual("After", result.Entries[1].Text); Assert.IsNotNull(result.Entries[0].RawJson);
    }

    [TestMethod]
    public async Task ParentRelationshipAndMetadataSurviveParsing()
    {
        var result = await Parse("""{"type":"session_meta","payload":{"id":"child","cwd":"D:\\repo","source":{"subagent":{"thread_spawn":{"parent_thread_id":"parent"}}}}}""");
        Assert.AreEqual("parent", result.Session.ParentId); Assert.AreEqual("subAgent", result.Session.Source); Assert.AreEqual("child", result.Session.Id);
    }

    [TestMethod]
    public async Task ToolCallAndResultAreOneExpandableActivity()
    {
        var result = await Parse("""{"type":"response_item","payload":{"type":"function_call","call_id":"c1","name":"shell","arguments":"pwd"}}""",
            """{"type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"D:\\repo"}}""");
        Assert.HasCount(1, result.Entries); Assert.AreEqual("shell", result.Entries[0].Label);
        StringAssert.Contains(result.Entries[0].Text, "pwd"); StringAssert.Contains(result.Entries[0].Text, "D:\\repo");
        Assert.AreEqual("pwd", result.Entries[0].Tool?.Input); Assert.AreEqual("D:\\repo", result.Entries[0].Tool?.Output);
        Assert.AreEqual(ToolActivityState.Completed, result.Entries[0].Tool?.State);
    }

    [TestMethod]
    public async Task ActiveMarkerBeforeLargeOutputIsNotLostAndFallbackTitleIsReadable()
    {
        var folder = Path.Combine(_folder, "sessions"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "a.jsonl");
        await File.WriteAllLinesAsync(path, [
            """{"type":"session_meta","payload":{"id":"a","source":"cli"}}""",
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"long"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"text":"<environment_context>ignore</environment_context>"}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"text":"Fix the layout"}]}}""",
            JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "function_call_output", output = new string('x', 200000) } })]);
        var session = (await new LocalSessionStore(_folder).ListAsync()).Single();
        Assert.AreEqual(SessionState.RecordedActive, session.State); Assert.AreEqual("Fix the layout", session.Title);
    }

    [TestMethod]
    public async Task ArchiveAndSubagentsAreListedAndIndexRenamesInvalidateTitleCache()
    {
        var sessions = Path.Combine(_folder, "sessions", "2026"); var archive = Path.Combine(_folder, "archived_sessions");
        Directory.CreateDirectory(sessions); Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(Path.Combine(sessions, "a.jsonl"), """{"type":"session_meta","payload":{"id":"a","source":"cli"}}""");
        await File.WriteAllTextAsync(Path.Combine(archive, "b.jsonl"), """{"type":"session_meta","payload":{"id":"b","source":{"subagent":{"thread_spawn":{"parent_thread_id":"a"}}}}}""");
        await File.WriteAllTextAsync(Path.Combine(_folder, "session_index.jsonl"), """{"id":"a","thread_name":"First"}""" + "\n{bad");
        var store = new LocalSessionStore(_folder); var first = await store.ListAsync();
        Assert.HasCount(2, first); Assert.IsTrue(first.Single(x => x.Id == "b").IsArchived); Assert.AreEqual("a", first.Single(x => x.Id == "b").ParentId);
        await File.WriteAllTextAsync(Path.Combine(_folder, "session_index.jsonl"), """{"id":"a","thread_name":"Renamed"}""");
        Assert.AreEqual("Renamed", (await store.ListAsync()).Single(x => x.Id == "a").Title);
    }

    [TestMethod]
    public void ExportDoesNotAllowToolOutputToEscapeItsCodeFence()
    {
        var snapshot = new TranscriptSnapshot(new SessionInfo { Id = "id", Title = "Example" }, [new("1", EntryKind.Tool, "```\n# injected heading", default)]);
        var export = TranscriptExport.Markdown(snapshot, new("en-US"));
        StringAssert.Contains(export.Replace("\r\n", "\n"), "````\n```\n# injected heading\n````");
    }
}
