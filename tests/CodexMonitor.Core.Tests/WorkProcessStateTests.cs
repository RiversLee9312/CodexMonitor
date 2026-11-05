using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class WorkProcessStateTests
{
    private static JsonElement J(string value) { using var d = JsonDocument.Parse(value); return d.RootElement.Clone(); }
    [TestMethod]
    public void OnlyAnActiveKnownServerTurnGetsAViewOnlyPendingIndicator()
    {
        TranscriptEntry[] entries = [new("question", EntryKind.User, "Do something", default, TurnId: "turn")];
        Assert.AreSame(entries, WorkProcessState.WithPending(entries, false, "turn"));
        Assert.AreSame(entries, WorkProcessState.WithPending(entries, true, null));
        var view = WorkProcessState.WithPending(entries, true, "turn");
        Assert.HasCount(1, entries); Assert.HasCount(2, view);
        Assert.AreEqual(ToolActivityState.Generating, view[^1].Tool!.State); Assert.AreEqual("", view[^1].Text);
        Assert.AreEqual(view[^1].Id, WorkProcessState.WithPending(entries, true, "turn")[^1].Id);
    }
    [TestMethod, DataRow("agentMessage"), DataRow("reasoning")]
    public void NativeLifecycleKeepsDirectStreamsVisibleAndPendingResumesOnlyAfterCompletion(string type)
    {
        var payload = J($$$"""{"threadId":"thread","turnId":"turn","item":{"id":"text","type":"{{{type}}}","text":"Hello","summary":["Summary"]}}""");
        var entries = TranscriptUpdates.Apply([], "item/started", payload);
        Assert.IsTrue(entries[0].IsStreaming); Assert.AreSame(entries, WorkProcessState.WithPending(entries, true, "turn"));
        entries = TranscriptUpdates.Apply(entries, "item/completed", payload);
        Assert.IsFalse(entries[0].IsStreaming); Assert.HasCount(2, WorkProcessState.WithPending(entries, true, "turn"));
        Assert.HasCount(1, WorkProcessState.WithPending(entries, false, "turn"));
    }
    [TestMethod]
    public void ParallelExecutingToolsSuppressPendingUntilAllHaveFinishedAndOldTurnsDoNotBlockIt()
    {
        TranscriptEntry Entry(string id, string turn, ToolActivityState state) => new(id, EntryKind.Tool, "", default, Tool: new("shell", "echo", State: state), TurnId: turn);
        var entries = new[] { Entry("old", "old-turn", ToolActivityState.Running), Entry("a", "turn", ToolActivityState.Completed), Entry("b", "turn", ToolActivityState.Running) };
        Assert.AreSame(entries, WorkProcessState.WithPending(entries, true, "turn"));
        entries[2] = Entry("b", "turn", ToolActivityState.Completed);
        Assert.HasCount(4, WorkProcessState.WithPending(entries, true, "turn"));
    }
    [TestMethod]
    public void InterruptionClearsOnlyTheEndedTurnsStreamingStateAndDrafts()
    {
        TranscriptEntry[] entries = [new("a", EntryKind.Assistant, "A", default, TurnId: "a", IsStreaming: true), new("b", EntryKind.Assistant, "B", default, TurnId: "b", IsStreaming: true)];
        var ended = TranscriptUpdates.Apply(entries, "turn/completed", J("""{"turn":{"id":"a","status":"interrupted"}}"""));
        Assert.IsFalse(ended.Single(e => e.Id == "a").IsStreaming); Assert.IsTrue(ended.Single(e => e.Id == "b").IsStreaming);
    }
}
