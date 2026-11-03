using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ReasoningTests
{
    [TestMethod]
    [DataRow("""{"type":"reasoning","summary":[{"type":"summary_text","text":"Readable summary"}],"content":[{"type":"reasoning_text","text":"Long thinking"}]}""", "Readable summary", "Long thinking")]
    [DataRow("""{"type":"reasoning","summary":[],"content":["Open-model thinking"]}""", "", "Open-model thinking")]
    [DataRow("""{"type":"reasoning","summary":["A summary"],"encrypted_content":"SECRET","content":[{"type":"encrypted_text","text":"SECRET"}]}""", "A summary", "")]
    [DataRow("""{"type":"reasoning","text":"Untyped provider reasoning"}""", "", "Untyped provider reasoning")]
    public void ProtocolMetadataSeparatesSummariesFromPlainReasoning(string json, string summary, string full)
    {
        using var document = JsonDocument.Parse(json);
        var local = TranscriptParser.ParseResponseItem(document.RootElement, "r", default)!;
        var server = CodexClient.ParseItem(document.RootElement);
        Assert.AreEqual(new ReasoningContent(summary, full), local.Reasoning); Assert.AreEqual(local.Reasoning, server.Reasoning);
        Assert.AreEqual(summary.Length == 0 ? full : summary, local.Text); Assert.IsFalse(local.Text.Contains("SECRET"));
    }
    [TestMethod]
    public void LegacyAnalysisMessagesAreThinkingRatherThanAssistantReplies()
    {
        using var document = JsonDocument.Parse("""{"type":"message","role":"assistant","channel":"analysis","content":[{"type":"text","text":"Long analysis"}]}""");
        var item = TranscriptParser.ParseResponseItem(document.RootElement, "r", default)!;
        Assert.AreEqual(EntryKind.Reasoning, item.Kind); Assert.AreEqual("", item.Reasoning?.Summary); Assert.AreEqual("Long analysis", item.Reasoning?.FullText);
    }
    [TestMethod]
    public void SnapshotWatermarksAvoidDuplicatingDeltasAlreadyIncludedInHistory()
    {
        var snapshot = new ServerTranscript(Array.Empty<TranscriptEntry>(), new Dictionary<string, long> { ["r"] = 10 });
        using var p = JsonDocument.Parse("""{"itemId":"r","delta":"text"}""");
        Assert.IsFalse(snapshot.ShouldReplay(new("item/agentMessage/delta", p.RootElement, Sequence: 9)));
        Assert.IsTrue(snapshot.ShouldReplay(new("item/agentMessage/delta", p.RootElement, Sequence: 11)));
        using var unknown = JsonDocument.Parse("""{"itemId":"new"}""");
        Assert.IsTrue(snapshot.ShouldReplay(new("item/started", unknown.RootElement, Sequence: 9)));
    }
    [TestMethod]
    public void LicenseCatalogContainsRealLicenseTextAndOriginalNotices()
    {
        var licenses = LicenseCatalog.Load(); Assert.IsTrue(licenses.Count >= 8);
        Assert.IsTrue(licenses.All(c => c.Text.Length > 100 || Uri.IsWellFormedUriString(c.Source, UriKind.Absolute)));
        Assert.IsTrue(licenses.Single(c => c.Name == "Microsoft.NETCore.App.Runtime.win-x64").Notices.Contains("LICENSE"));
    }
}
