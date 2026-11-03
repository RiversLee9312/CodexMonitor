using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class InstalledCliTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task InstalledCliCanInitializeAndListWithoutStartingInference()
    {
        if (Environment.GetEnvironmentVariable("CODEX_MONITOR_INTEGRATION") != "1") Assert.Inconclusive("Set CODEX_MONITOR_INTEGRATION=1 to test the installed CLI.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = new CodexClient(new JsonRpcClient(StdioTransport.Start("codex", LocalSessionStore.DefaultHome)));
        await client.InitializeAsync(timeout.Token);
        var sessions = await client.ListAsync(timeout.Token);
        Assert.IsNotNull(sessions);
        Console.WriteLine($"Initialized installed CLI. Read {sessions.Count} session summaries; no turns started.");
    }
}
