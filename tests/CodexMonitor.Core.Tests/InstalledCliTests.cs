using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class InstalledCliTests
{
    [TestMethod, TestCategory("Integration")]
    public async Task DesktopCliIsDiscoveredWithoutAnyPathAndCanReportItsVersion()
    {
        if (Environment.GetEnvironmentVariable("CODEX_MONITOR_INTEGRATION") != "1") Assert.Inconclusive("Requires opt-in local CLI validation.");
        var native = new CodexExecutableDiscovery(() => []).Resolve("codex");
        var start = new System.Diagnostics.ProcessStartInfo(native) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--version");
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(0, process.ExitCode); StringAssert.Contains(output, "codex");
        Console.WriteLine("Native desktop CLI resolved with no search PATH; --version succeeded.");
    }
    [TestMethod]
    [TestCategory("Integration")]
    public async Task ExistingCompressedHistoryCanBeListedAndReadWithoutChangingItsFiles()
    {
        if (Environment.GetEnvironmentVariable("CODEX_MONITOR_INTEGRATION") != "1") Assert.Inconclusive("Requires opt-in read-only local history validation.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var rows = await Task.Run(() => new LocalSessionStore(LocalSessionStore.DefaultHome).ListAsync(timeout.Token));
        var old = rows.Where(s => RolloutFiles.IsCompressed(s.FilePath)).OrderBy(s => s.UpdatedAt).FirstOrDefault();
        if (old is null) Assert.Inconclusive("No compressed local history available.");
        var before = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(old.FilePath, timeout.Token));
        var page = await new LocalTranscriptPager().ReadAsync(old, ct: timeout.Token);
        Assert.IsTrue(page.Snapshot.Entries.Count > 0); Assert.IsTrue(rows.Count > 50);
        CollectionAssert.AreEqual(before, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(old.FilePath, timeout.Token)));
        Console.WriteLine($"Local history: {rows.Count} logical conversations, {rows.Count(s => RolloutFiles.IsCompressed(s.FilePath))} compressed; oldest compressed page contains {page.Snapshot.Entries.Count} entries. Source hash unchanged.");
    }
    [TestMethod]
    [TestCategory("Integration")]
    public async Task DiscoveryCanFindAnExistingLocalDaemonWithoutStartingTurns()
    {
        if (Environment.GetEnvironmentVariable("CODEX_MONITOR_INTEGRATION") != "1" || !File.Exists(SharedServerEndpoint.DefaultSocketPath(LocalSessionStore.DefaultHome)))
            Assert.Inconclusive("Requires an already running local Codex daemon and opt-in integration tests.");
        var result = await new LocalServerDiscovery().FindAsync(LocalSessionStore.DefaultHome, "codex");
        Assert.IsTrue(result.Any(s => s.Endpoint == "unix://" && s.CanConnect), "The existing daemon must pass the Codex initialize handshake.");
        Console.WriteLine($"Discovered {result.Count} existing local servers; no turns started.");
        foreach (var server in result.Where(s => s.CanConnect))
        {
            await using var client = await SessionServerLocator.OpenAsync(server, LocalSessionStore.DefaultHome, "codex", CancellationToken.None);
            var loaded = await client.LoadedThreadIdsAsync(); Assert.IsNotNull(loaded);
            if (loaded.FirstOrDefault() is { } id)
            {
                var page = await client.ReadTranscriptPageAsync(id);
                Assert.IsTrue(page.Entries.Count <= 100, "The real server must respect the item page limit");
                if (page.NextCursor is not null) Assert.IsNotNull(await client.ReadTranscriptPageAsync(id, page.NextCursor));
                Console.WriteLine("Read bounded item pages from an existing loaded thread; no resume or turns started.");
            }
        }
    }
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
