using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ActivityEventTests
{
    [TestMethod]
    [DataRow("""{"type":"commandExecution","command":"cat readme.md","commandActions":[{"type":"read","path":"readme.md"}],"status":"completed","aggregatedOutput":"Hello"}""", "ToolReadFile", "readme.md")]
    [DataRow("""{"type":"commandExecution","command":"ls src","commandActions":[{"type":"listFiles","path":"src"}]}""", "ToolListFiles", "src")]
    [DataRow("""{"type":"webSearch","action":{"type":"openPage","url":"https://example.com"}}""", "ToolOpenPage", "https://example.com")]
    [DataRow("""{"type":"webSearch","action":{"type":"findInPage","url":"https://example.com","pattern":"Fluent"}}""", "ToolFindInPage", "Fluent")]
    [DataRow("""{"type":"webSearch","action":{"type":"search","queries":["WinUI layout","MSTest"]}}""", "ToolSearch", "MSTest")]
    [DataRow("""{"type":"mcpToolCall","server":"github","tool":"search","arguments":{"query":"WinUI"},"result":{"content":[{"type":"text","text":"Found"}]},"status":"completed"}""", "ToolMcp", "github")]
    [DataRow("""{"type":"dynamicToolCall","tool":"compile","arguments":{"path":"app.csproj"},"contentItems":[{"type":"text","text":"OK"}],"success":true}""", "ToolDynamic", "compile")]
    [DataRow("""{"type":"collabToolCall","tool":"spawnAgent","newThreadId":"child","prompt":"Review layout","agentStatus":{"status":"running"}}""", "ToolCollaboration", "spawnAgent")]
    [DataRow("""{"type":"imageView","path":"D:/images/preview.png"}""", "ToolImageView", "preview.png")]
    [DataRow("""{"type":"imageGeneration","prompt":"A simple icon"}""", "ToolImageGeneration", "simple icon")]
    [DataRow("""{"type":"plan","text":"Check before publishing"}""", "ToolPlan", "Check")]
    [DataRow("""{"type":"exitedReviewMode","review":"Ready to ship"}""", "ToolReview", "Ready")]
    public void DocumentedItemsRetainReadableSummariesAndStructuredResults(string json, string category, string preview)
    {
        using var doc = JsonDocument.Parse(json); var entry = CodexClient.ParseItem(doc.RootElement, lifecycle: ToolActivityState.Completed);
        Assert.IsNotNull(entry.Tool); Assert.AreEqual(category, entry.Tool.CategoryKey); StringAssert.Contains(entry.Tool.Summary, preview);
        Assert.AreEqual(ToolActivityState.Completed, entry.Tool.State);
        if (category == "ToolMcp") StringAssert.Contains(entry.Tool.Output!, "Found");
    }
    [TestMethod]
    public void FailureAndFinalLifecycleRemainAuthoritativeForToolsWithoutStatus()
    {
        using var failed = JsonDocument.Parse("""{"id":"t","type":"dynamicToolCall","tool":"compile","arguments":{},"success":false}""");
        Assert.AreEqual(ToolActivityState.Failed, CodexClient.ParseItem(failed.RootElement, lifecycle: ToolActivityState.Completed).Tool?.State);
        using var compacted = JsonDocument.Parse("""{"id":"c","type":"contextCompaction"}""");
        Assert.AreEqual(ToolActivityState.Running, CodexClient.ParseItem(compacted.RootElement, lifecycle: ToolActivityState.Running).Tool?.State);
        Assert.AreEqual(ToolActivityState.Completed, CodexClient.ParseItem(compacted.RootElement, lifecycle: ToolActivityState.Completed).Tool?.State);
    }
    [TestMethod]
    public void SummaryObjectsNeverExposeRawReasoningAndUnknownItemsRetainTheirPayload()
    {
        using var summary = JsonDocument.Parse("""{"id":"r","type":"reasoning","summary":[{"type":"summary_text","text":"Readable summary"}],"content":[{"text":"PRIVATE"}],"encrypted_content":"SECRET"}""");
        var entry = CodexClient.ParseItem(summary.RootElement); Assert.AreEqual("Readable summary", entry.Text); Assert.IsNull(entry.Tool);
        using var future = JsonDocument.Parse("""{"id":"x","type":"futureActivity","data":{"n":5}}""");
        var unknown = CodexClient.ParseItem(future.RootElement); StringAssert.Contains(unknown.Text, "futureActivity"); Assert.AreEqual(EntryKind.Event, unknown.Kind);
    }
    [TestMethod]
    [DataRow("warning", """{"message":"Low disk space"}""", "EventWarning")]
    [DataRow("error", """{"willRetry":true,"error":{"message":"Temporary failure"}}""", "EventRetrying")]
    [DataRow("turn/completed", """{"turn":{"id":"t","status":"failed","error":{"message":"Request failed"}}}""", "EventError")]
    [DataRow("turn/completed", """{"turn":{"status":"interrupted"}}""", "EventInterrupted")]
    [DataRow("hook/started", """{"run":{"id":"h","name":"Validate"}}""", "EventHook")]
    [DataRow("model/rerouted", """{"fromModel":"old","toModel":"new"}""", "EventModelChanged")]
    [DataRow("model/verification", """{"verifications":[]}""", "EventVerification")]
    [DataRow("mcpServer/startupStatus/updated", """{"serverName":"github","status":"ready"}""", "EventMcpStatus")]
    [DataRow("thread/tokenUsage/updated", """{"threadId":"s","tokenUsage":{"total":{"totalTokens":100}}}""", "EventUsage")]
    [DataRow("account/rateLimits/updated", """{"rateLimits":{"primary":{"usedPercent":5}}}""", "EventLimits")]
    public void RuntimeEventsBecomeCompactActivitiesInsteadOfRawRecords(string method, string json, string category)
    {
        using var doc = JsonDocument.Parse(json); var entry = TranscriptEvents.Parse(method, doc.RootElement);
        Assert.IsNotNull(entry); Assert.AreEqual(category, entry.Tool?.CategoryKey); Assert.IsFalse(entry.Tool!.Summary.Contains('{'));
    }
    [TestMethod]
    public void ProgressEventsReuseIdentityWhileSeparateWarningsRemainSeparate()
    {
        using var first = JsonDocument.Parse("""{"threadId":"s","turnId":"t","plan":[{"step":"Build","status":"pending"}]}""");
        using var next = JsonDocument.Parse("""{"threadId":"s","turnId":"t","plan":[{"step":"Build","status":"completed"}]}""");
        var a = TranscriptEvents.Parse("turn/plan/updated", first.RootElement)!; var b = TranscriptEvents.Parse("turn/plan/updated", next.RootElement)!;
        Assert.AreEqual(a.Id, b.Id); StringAssert.Contains(b.Tool!.Input, "completed");
        Assert.AreNotEqual(TranscriptEvents.Parse("warning", first.RootElement)!.Id, TranscriptEvents.Parse("warning", first.RootElement)!.Id);
        Assert.IsNull(TranscriptEvents.Parse("item/reasoning/textDelta", first.RootElement));
    }
}
