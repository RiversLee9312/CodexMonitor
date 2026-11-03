using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class WaitActivityTests
{
    [TestMethod]
    [DataRow("clock.sleep", "{\"duration_ms\":30000}", 30000L)]
    [DataRow("mcp__clock__sleep", "{\"duration_ms\":0}", 0L)]
    [DataRow("wait", "{\"durationMs\":1500}", 1500L)]
    public void RecognizesNamedWaitToolsAndKeepsTheirRequestedDuration(string name, string input, long milliseconds)
    {
        var activity = new ToolActivity(name, input);
        Assert.AreEqual(milliseconds, activity.WaitDurationMs); Assert.AreEqual("ToolWaiting", activity.CategoryKey); Assert.AreEqual("", activity.Summary);
    }
    [TestMethod]
    [DataRow("video", "{\"duration_ms\":30000}")]
    [DataRow("clock.sleep", "{\"duration_ms\":-1}")]
    [DataRow("clock.sleep", "{\"duration_ms\":\"30000\"}")]
    [DataRow("clock.sleep", "bad json")]
    [DataRow("functions.wait", "{\"cell_id\":\"x\",\"yield_time_ms\":1000}")]
    public void OtherDurationsAndMalformedWaitsDoNotBecomeCountdowns(string name, string input) => Assert.IsNull(new ToolActivity(name, input).WaitDurationMs);
}
