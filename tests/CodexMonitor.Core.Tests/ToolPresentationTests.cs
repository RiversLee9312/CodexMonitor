using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ToolPresentationTests
{
    [TestMethod]
    public void FileChangePreparationStopsAtPerFileLeaves()
    {
        var tool = new ToolActivity("apply_patch", "*** Begin Patch\n*** Update File: one.cs\n@@\n-before\n+after\n*** Add File: two.cs\n+new\n*** End Patch");
        tool.PreparePresentation();
        Assert.HasCount(2, tool.DisplayParts());
        Assert.IsTrue(tool.DisplayParts().All(part => part.CategoryKey == "ToolFileChanges"));
        CollectionAssert.AreEqual(new[] { "one.cs", "two.cs" }, tool.DisplayParts().Select(part => part.Summary).ToArray());
    }
    [TestMethod]
    public void HistoryWorkersAndLiveNotificationsCanRecordModelsConcurrently()
    {
        var models = new ModelTimeline(); models.Configure("thread", "configured");
        Parallel.For(0, 10000, i =>
        {
            models.Start("thread", "turn-" + i, "model-" + i);
            models.Remember("thread", "item-" + i, "model-" + i);
            Assert.AreEqual("model-" + i, models.ForTurn("thread", "turn-" + i));
            Assert.AreEqual("model-" + i, models.ForItem("thread", "item-" + i));
        });
    }
    [TestMethod]
    public async Task CachedPresentationIsSafeAcrossReadersAndRecordCopiesDoNotKeepOldInput()
    {
        var tool = new ToolActivity("exec", "text(await tools.exec_command({cmd: 'echo first'}));");
        tool.PreparePresentation();
        var values = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => (tool.CategoryKey, tool.Summary, tool.DecodedInput))));
        Assert.IsTrue(values.All(value => value == ("ToolCommand", "echo first", "echo first")));
        var copy = tool with { Input = "text(await tools.sleep({duration_ms: 30000}));" };
        copy.PreparePresentation(); Assert.AreEqual("ToolWaiting", copy.CategoryKey); Assert.AreEqual(30000L, copy.WaitDurationMs);
        Assert.AreEqual("ToolCommand", tool.CategoryKey); Assert.AreEqual("echo first", tool.Summary);
    }
}
