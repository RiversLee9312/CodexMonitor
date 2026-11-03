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
