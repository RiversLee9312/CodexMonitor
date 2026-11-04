using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class PagingAndQuestionsTests
{
    public TestContext TestContext { get; set; } = null!;
    private static JsonElement J(string value) { using var d = JsonDocument.Parse(value); return d.RootElement.Clone(); }
    private static string Record(int i) => JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "message", role = i % 2 == 0 ? "user" : "assistant", content = new[] { new { type = "text", text = $"Message {i}: 你好 · مرحبا" } } } });
    [TestMethod]
    public async Task LargeLocalHistoryStartsAtTheTailAndOlderPagesKeepByteStableIds()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                for (int i = 0; i < 100000; i++) await writer.WriteLineAsync(Record(i));
            var session = new SessionInfo { Id = "large", FilePath = path }; var pager = new LocalTranscriptPager(); var watch = Stopwatch.StartNew();
            var tail = await pager.ReadAsync(session); var elapsed = watch.Elapsed;
            Assert.IsTrue(tail.HasEarlier); Assert.IsTrue(tail.WindowBytes < new FileInfo(path).Length / 100); Assert.HasCount(300, tail.Snapshot.Entries);
            StringAssert.StartsWith(tail.Snapshot.Entries[0].Text, "Message 99700:");
            var older = await pager.ReadAsync(session, tail.StartOffset, true); Assert.HasCount(600, older.Snapshot.Entries);
            CollectionAssert.AreEqual(tail.Snapshot.Entries.Select(e => e.Id).ToArray(), older.Snapshot.Entries.Skip(300).Select(e => e.Id).ToArray());
            await File.AppendAllTextAsync(path, Record(100000) + "\r\n"); var appended = await pager.ReadAsync(session, older.StartOffset);
            Assert.HasCount(601, appended.Snapshot.Entries); Assert.AreEqual(older.StartOffset, appended.StartOffset);
            // No brittle timing assertion: the byte window proves work does not scale with the untouched prefix.
            TestContext.WriteLine($"100000 records, {new FileInfo(path).Length} bytes; initial read {tail.WindowBytes} bytes / {elapsed.TotalMilliseconds:F1} ms");
        }
        finally { File.Delete(path); }
    }
    [TestMethod]
    public async Task LocalPagingHandlesPartialWritesTruncationAndLongUtf8Records()
    {
        var path = Path.GetTempFileName();
        try
        {
            var big = new string('中', 60000); var record = JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "message", role = "assistant", content = new[] { new { type = "text", text = big } } } });
            await File.WriteAllTextAsync(path, record + "\r\n" + Record(2)[..50]); var pager = new LocalTranscriptPager(); var s = new SessionInfo { Id = "utf8", FilePath = path };
            var first = await pager.ReadAsync(s); Assert.HasCount(1, first.Snapshot.Entries); Assert.AreEqual(big, first.Snapshot.Entries[0].Text);
            await File.AppendAllTextAsync(path, Record(2)[50..] + "\r\n"); var completed = await pager.ReadAsync(s, first.StartOffset); Assert.HasCount(2, completed.Snapshot.Entries); Assert.AreEqual(first.Snapshot.Entries[0].Id, completed.Snapshot.Entries[0].Id);
            await File.WriteAllTextAsync(path, Record(3)); var truncated = await pager.ReadAsync(s, new FileInfo(path).Length + 100); Assert.HasCount(1, truncated.Snapshot.Entries);
        }
        finally { File.Delete(path); }
    }
    [TestMethod]
    public async Task ServerUsesOneItemPageEvenForOneHugeTurnAndOlderOverlapCannotOverwriteLiveText()
    {
        var transport = new FakeTransport(); await using var client = new CodexClient(new JsonRpcClient(transport));
        var reading = client.ReadTranscriptPageAsync("thread"); var request = await transport.SentAsync(); Assert.AreEqual("thread/items/list", request.Str("method"));
        Assert.AreEqual("desc", request.Field("params").Str("sortDirection")); Assert.AreEqual(100L, request.Field("params").Field("limit").GetInt64());
        transport.Reply(request, J("""{"data":[{"turnId":"huge","completedAtMs":1000,"item":{"id":"b","type":"sleep","durationMs":20}},{"turnId":"huge","item":{"id":"a","type":"agentMessage","text":"First"}}],"nextCursor":"older"}"""));
        var page = await reading; Assert.HasCount(2, page.Entries); Assert.AreEqual("a", page.Entries[0].Id); Assert.AreEqual("older", page.NextCursor); Assert.AreEqual(ToolActivityState.Completed, page.Entries[1].Tool!.State);
        var history = ServerTranscript.Merge([page.Entries[1] with { Text = "Live second" }], page.Entries, true);
        Assert.AreEqual("Live second", history[1].Text); Assert.HasCount(2, history);
        var next = client.ReadTranscriptPageAsync("thread", page.NextCursor); var second = await transport.SentAsync(); Assert.AreEqual("older", second.Field("params").Str("cursor")); transport.Reply(second, J("""{"data":[],"nextCursor":null}""")); Assert.IsNull((await next).NextCursor);
    }
    [TestMethod]
    public void QuestionsRequireExplicitValidAnswersAndCancellationHasNoSelectedAnswer()
    {
        var request = UserInputRequest.Parse(J("""{"threadId":"t","turnId":"r","itemId":"i","questions":[{"id":"choice","header":"Mode","question":"Choose","options":[{"label":"Safe","description":"Review first"}]},{"id":"secret","header":"Value","question":"Enter","isSecret":true},{"id":"other","header":"Other","question":"Choose or write","isOther":true,"options":[{"label":"One","description":""}]}]}"""));
        Assert.IsTrue(request.Questions[1].Secret); Assert.Throws<InvalidDataException>(() => request.Respond(new Dictionary<string,string>()));
        Assert.Throws<InvalidDataException>(() => request.Respond(new Dictionary<string,string> { ["choice"] = "Invalid", ["secret"] = "value", ["other"] = "Custom" }));
        var response = request.Respond(new Dictionary<string,string> { ["choice"] = "Safe", ["secret"] = "  value  ", ["other"] = "Custom" });
        Assert.AreEqual("  value  ", response["answers"]!["secret"]!["answers"]![0]!.GetValue<string>());
        Assert.AreEqual("{}", UserInputRequest.Cancelled()["answers"]!.ToJsonString());
    }
}
