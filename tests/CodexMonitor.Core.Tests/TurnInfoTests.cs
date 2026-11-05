using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class TurnInfoTests
{
    private static JsonElement J(string source) => JsonDocument.Parse(source).RootElement.Clone();
    [TestMethod]
    public void TokenUpdatesKeepTheirTurnAndScopeWithoutInventingAModel()
    {
        var models = new ModelTimeline(); models.Configure("t", "current");
        models.Observe("thread/tokenUsage/updated", J("""{"threadId":"t","turnId":"old","tokenUsage":{"last":{"inputTokens":10,"outputTokens":2,"totalTokens":12},"total":{"totalTokens":700},"modelContextWindow":200000}}"""));
        Assert.IsNull(models.Details("t", "old")!.Model); Assert.AreEqual(12L, models.Details("t", "old")!.LastRequest!.Total);
        Assert.AreEqual(700L, models.Details("t", "old")!.ThreadTotal!.Total);
        models.Start("t", "new"); models.Observe("thread/tokenUsage/updated", J("""{"threadId":"t","turnId":"new","tokenUsage":{"last":{"totalTokens":20},"total":{"totalTokens":720}}}"""));
        Assert.AreEqual(700L, models.Details("t", "old")!.ThreadTotal!.Total); Assert.AreEqual("current", models.Details("t", "new")!.Model);
    }
    [TestMethod]
    public void SettingsAndReroutingHaveDifferentEvidenceAndCannotRewritePreviousTurns()
    {
        var models = new ModelTimeline(); models.Observe("thread/settings/updated", J("""{"threadId":"t","threadSettings":{"model":"a","modelProvider":"openai","effort":"high","serviceTier":"priority"}}"""));
        models.Start("t", "one"); models.Observe("thread/settings/updated", J("""{"threadId":"t","threadSettings":{"model":"b"}}""")); models.Start("t", "two");
        Assert.AreEqual("a", models.Details("t", "one")!.Model); Assert.AreEqual("ModelConfigured", models.Details("t", "one")!.ModelSource);
        models.Observe("model/rerouted", J("""{"threadId":"t","turnId":"two","toModel":"actual"}"""));
        Assert.AreEqual("actual", models.Details("t", "two")!.Model); Assert.AreEqual("ModelRerouted", models.Details("t", "two")!.ModelSource);
        Assert.AreEqual("a", models.Details("t", "one")!.Model); Assert.AreEqual("high", models.Details("t", "one")!.Effort);
    }
    [TestMethod]
    public async Task HistoricalTurnDetailsAreReadWithoutItemsAndCachedWithoutUsingTheCurrentModel()
    {
        var transport = new FakeTransport(); await using var client = new CodexClient(new JsonRpcClient(transport)); client.Models.Configure("t", "current");
        var pending = client.ReadTurnInfoAsync("t", "wanted", DateTimeOffset.FromUnixTimeSeconds(120));
        var request = await transport.SentAsync(); Assert.AreEqual("notLoaded", request.Field("params").Str("itemsView"));
        transport.Reply(request, J("""{"data":[{"id":"recent","startedAt":200,"status":"completed"}],"nextCursor":"older"}"""));
        request = await transport.SentAsync(); Assert.AreEqual("older", request.Field("params").Str("cursor"));
        transport.Reply(request, J("""{"data":[{"id":"wanted","startedAt":100,"completedAt":150,"durationMs":50000,"status":"completed"}],"nextCursor":null}"""));
        var info = await pending; Assert.IsNotNull(info); Assert.IsNull(info.Model); Assert.AreEqual(50000L, info.DurationMs);
        Assert.AreSame(info, await client.ReadTurnInfoAsync("t", "wanted"));
    }
    [TestMethod]
    public async Task LocalContextAndUsageStayWithTheirRecordedTurn()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexMonitor-turn-info-" + Guid.NewGuid() + ".jsonl");
        try
        {
            await File.WriteAllTextAsync(path, """
                {"type":"event_msg","timestamp":"2026-11-04T10:00:00Z","payload":{"type":"task_started","turn_id":"a"}}
                {"type":"turn_context","payload":{"turn_id":"a","model":"model-a"}}
                {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"total_tokens":15},"total_token_usage":{"total_tokens":100}}}}
                {"type":"event_msg","timestamp":"2026-11-04T10:01:00Z","payload":{"type":"task_complete","turn_id":"a"}}
                {"type":"event_msg","payload":{"type":"task_started","turn_id":"b"}}
                {"type":"turn_context","payload":{"turn_id":"b","model":"model-b"}}
                {"type":"event_msg","payload":{"type":"task_complete","turn_id":"b"}}
                {"type":"event_msg","payload":{"type":"task_started","turn_id":"unknown"}}
                """);
            var result = await new TranscriptParser().ReadAsync(new() { Id = "t", FilePath = path });
            Assert.AreEqual("model-a", result.Turns["a"].Model); Assert.AreEqual("completed", result.Turns["a"].Status);
            Assert.AreEqual(15L, result.Turns["a"].LastRequest!.Total); Assert.AreEqual("model-b", result.Turns["b"].Model);
            Assert.IsNull(result.Turns["b"].LastRequest);
            Assert.IsNull(result.Turns["unknown"].Model, "A new turn without model evidence must not inherit the previous model");
        }
        finally { File.Delete(path); }
    }
    [TestMethod, DataRow("en-US"), DataRow("zh-CN"), DataRow("de-DE"), DataRow("ar-SA")]
    public void MessageTimesUseTheLocalDayAndIncludeDatesForOlderRecords(string language)
    {
        var text = new LocalizationService(language); var now = new DateTimeOffset(2026, 11, 5, 0, 2, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 5)));
        var yesterday = now.AddMinutes(-5); var today = now.AddMinutes(5);
        Assert.AreEqual(yesterday.ToLocalTime().ToString("g", text.Culture), text.MessageTime(yesterday, now));
        Assert.AreEqual(today.ToLocalTime().ToString("t", text.Culture), text.MessageTime(today, now)); Assert.AreEqual("", text.MessageTime(default, now));
    }
}
