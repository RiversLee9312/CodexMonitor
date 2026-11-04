using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ConversationPresentationTests
{
    private static JsonElement J(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    [TestMethod]
    public async Task LocalRepliesKeepTheirTurnModelsAndLateNativeMirrorsDoNotRetagEarlierReplies()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(path, [
                """{"type":"event_msg","payload":{"type":"task_started","turn_id":"a"}}""",
                """{"type":"turn_context","payload":{"turn_id":"a","model":"model-a"}}""",
                """{"type":"response_item","payload":{"id":"reply-a","type":"message","role":"assistant","content":[{"type":"output_text","text":"First"}]}}""",
                """{"type":"event_msg","payload":{"type":"task_started","turn_id":"b"}}""",
                """{"type":"turn_context","payload":{"turn_id":"b","model":"model-b"}}""",
                """{"type":"response_item","payload":{"id":"reply-b","type":"message","role":"assistant","content":[{"type":"output_text","text":"Second"}]}}""",
                """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"reply-a","content":[{"type":"Text","text":"First"}]}}}""" ]);
            var snapshot = await new TranscriptParser().ReadAsync(new() { Id = "test", FilePath = path });
            Assert.HasCount(2, snapshot.Entries); Assert.AreEqual("model-a", snapshot.Entries[0].Model); Assert.AreEqual("a", snapshot.Entries[0].TurnId);
            Assert.AreEqual("model-b", snapshot.Entries[1].Model); Assert.AreEqual("model-b", snapshot.Session.Model);
            StringAssert.Contains(TranscriptExport.Markdown(snapshot, new("en-US")), "model-a");
        }
        finally { File.Delete(path); }
    }
    [TestMethod]
    public void ConfigChangesAndRoutingAffectNewRepliesWithoutRewritingExistingMessageModels()
    {
        var timeline = new ModelTimeline(); timeline.Configure("t", "first"); timeline.Start("t", "a");
        timeline.Observe("thread/settings/updated", J("""{"threadId":"t","threadSettings":{"model":"second"}}""")); timeline.Start("t", "b");
        Assert.AreEqual("first", timeline.ForTurn("t", "a")); Assert.AreEqual("second", timeline.ForTurn("t", "b"));
        var entries = TranscriptUpdates.Apply([], "item/agentMessage/delta", J("""{"itemId":"m","turnId":"b","delta":"Hello"}"""), model: timeline.ForTurn("t", "b"));
        timeline.Observe("model/rerouted", J("""{"threadId":"t","turnId":"b","toModel":"routed"}"""));
        entries = TranscriptUpdates.Apply(entries, "item/completed", J("""{"item":{"id":"m","type":"agentMessage","text":"Hello"}}"""), model: timeline.ForTurn("t", "b"));
        Assert.AreEqual("second", entries[0].Model); Assert.AreEqual("routed", timeline.ForTurn("t", "b")); Assert.AreEqual("second", timeline.Current("t"));
    }
    [TestMethod]
    public async Task ServerHistoryDoesNotSubstituteTheProviderOrCurrentConfigurationForMissingHistoricalModels()
    {
        var transport = new FakeTransport(); await using var client = new CodexClient(new JsonRpcClient(transport)); client.Models.Configure("t", "current");
        client.Models.Remember("t", "seen", "observed"); var pending = client.ReadTranscriptStateAsync("t"); var request = await transport.SentAsync();
        transport.Reply(request, J("""{"data":[{"id":"old","status":"completed","items":[{"id":"unknown","type":"agentMessage","text":"Old"},{"id":"seen","type":"agentMessage","text":"Observed"}]},{"id":"live","status":"inProgress","items":[{"id":"new","type":"agentMessage","text":"Not observed"}]}]}"""));
        var entries = (await pending).Entries; Assert.IsNull(entries[0].Model); Assert.AreEqual("observed", entries[1].Model); Assert.IsNull(entries[2].Model);
        Assert.AreEqual("", CodexClient.ParseSession(J("""{"id":"t","modelProvider":"openai"}""")).Model);
    }
    [TestMethod]
    [DataRow("function_call", "response.function_call_arguments.delta")]
    [DataRow("custom_tool_call", "response.custom_tool_call_input.delta")]
    [DataRow("mcp_call", "response.mcp_call_arguments.delta")]
    [DataRow("code_interpreter_call", "response.code_interpreter_call_code.delta")]
    [DataRow("image_generation_call", "response.image_generation_call.partial_image")]
    [DataRow("future_operation", "response.future_operation.in_progress")]
    public void AllNonMessageGenerationUsesOnePlaceholderAndFinalItemsReplaceIt(string type, string deltaMethod)
    {
        var item = JsonSerializer.Serialize(new { item = new { id = "pending", type, name = "exec_command", arguments = "" } });
        var entries = TranscriptUpdates.Apply([], "response.output_item.added", J(item), model: "model-a", turnId: "turn");
        entries = TranscriptUpdates.Apply(entries, deltaMethod, J("""{"item_id":"pending","delta":"PARTIAL_SECRET_INPUT","partial_image_b64":"IGNORED_BINARY"}"""));
        Assert.HasCount(1, entries); Assert.AreEqual(ToolActivityState.Generating, entries[0].Tool!.State); Assert.AreEqual("", entries[0].Text);
        entries = TranscriptUpdates.Apply(entries, "response.output_item.done", J("""{"item":{"id":"pending","type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"echo done\"}"}}"""));
        Assert.HasCount(1, entries); Assert.AreEqual("pending", entries[0].Id); Assert.AreEqual("echo done", entries[0].Tool!.DecodedInput); Assert.AreEqual("model-a", entries[0].Model);
        Assert.AreNotEqual(ToolActivityState.Generating, entries[0].Tool!.State);
        var late = TranscriptUpdates.Apply(entries, deltaMethod, J("""{"item_id":"pending","delta":"late"}""")); Assert.AreSame(entries, late);
    }
    [TestMethod]
    public void TextAndReasoningRemainDirectStreamsAndCancellationRemovesUnfinishedPlaceholders()
    {
        var entries = TranscriptUpdates.Apply([], "response.output_item.added", J("""{"item":{"id":"m","type":"message","role":"assistant","content":[]}}"""));
        entries = TranscriptUpdates.Apply(entries, "response.output_text.delta", J("""{"item_id":"m","delta":"Hello"}"""));
        entries = TranscriptUpdates.Apply(entries, "response.reasoning_summary_text.delta", J("""{"item_id":"r","delta":"Summary"}"""));
        Assert.AreEqual(EntryKind.Assistant, entries[0].Kind); Assert.AreEqual("Hello", entries[0].Text); Assert.AreEqual("Summary", entries[1].Reasoning!.Summary);
        entries = TranscriptUpdates.Apply(entries, "response.output_item.added", J("""{"item":{"id":"tool","type":"function_call","name":"exec","arguments":""}}"""), turnId: "a");
        entries = TranscriptUpdates.Apply(entries, "turn/completed", J("""{"turn":{"id":"a","status":"interrupted"}}"""), turnId: "a");
        Assert.IsFalse(entries.Any(e => e.Tool?.State == ToolActivityState.Generating));
    }
}
