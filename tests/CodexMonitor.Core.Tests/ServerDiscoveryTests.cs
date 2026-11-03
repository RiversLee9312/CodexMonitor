using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ServerDiscoveryTests
{
    [TestMethod]
    public void CandidateSelectionRequiresCodexOwnershipAndUsesOnlyLoopbackAddresses()
    {
        var rows = new[] {
            new OwnedTcpListener(IPAddress.Any, 4500, 10), new OwnedTcpListener(IPAddress.IPv6Any, 4501, 10),
            new OwnedTcpListener(IPAddress.Loopback, 4500, 10), new OwnedTcpListener(IPAddress.Loopback, 80, 20),
            new OwnedTcpListener(IPAddress.Parse("192.168.1.2"), 4502, 10), new OwnedTcpListener(IPAddress.Loopback, 0, 10) };
        var candidates = LocalServerDiscovery.CreateTcpCandidates(rows, new HashSet<int> { 10 });
        Assert.HasCount(2, candidates);
        Assert.IsTrue(candidates.All(c => WebSocketTransport.IsAllowedEndpoint(c.Endpoint)));
        Assert.IsTrue(candidates.Any(c => c.Endpoint == "ws://127.0.0.1:4500"));
        Assert.IsTrue(candidates.Any(c => c.Endpoint == "ws://[::1]:4501"));
    }
    [TestMethod]
    [DataRow("codex", true)] [DataRow("codex-cli", true)] [DataRow("Codex", true)]
    [DataRow("codex-aarch64-pc-windows-msvc", true)] [DataRow("node", false)]
    [DataRow("CodexMonitor.App", false)] [DataRow("codex-helper", false)]
    public void OnlyKnownCodexExecutableNamesAreScanned(string name, bool expected) => Assert.AreEqual(expected, LocalServerDiscovery.IsCodexProcess(name));

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void NativeTablesDecodeNetworkOrderPortsAndIgnoreNonListeningRows(bool ipv6)
    {
        var width = ipv6 ? 56 : 24; var bytes = new byte[4 + 2 * width];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        var row = bytes.AsSpan(4, width);
        (ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback).GetAddressBytes().CopyTo(row[(ipv6 ? 0 : 4)..]);
        BinaryPrimitives.WriteUInt16BigEndian(row.Slice(ipv6 ? 20 : 8, 2), 48765);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(ipv6 ? 48 : 0, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(ipv6 ? 52 : 20, 4), 12345);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + width + (ipv6 ? 48 : 0), 4), 5);
        var decoded = WindowsTcpListeners.Decode(bytes, ipv6);
        Assert.HasCount(1, decoded); Assert.AreEqual(48765, decoded[0].Port); Assert.AreEqual(12345, decoded[0].ProcessId);
        Assert.AreEqual(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, decoded[0].Address);
    }
    [TestMethod]
    public void TruncatedNativeTableIsRejected()
    {
        var bytes = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 999);
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsTcpListeners.Decode(bytes, true));
    }
    [TestMethod]
    public void NativeInventoryFindsAnOwnedWindowsListenerWithoutElevation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows-only listener inventory");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.IsTrue(WindowsTcpListeners.Read().Any(l => l.ProcessId == Environment.ProcessId && l.Port == port && l.Address.Equals(IPAddress.Loopback)));
    }
    [TestMethod]
    public async Task ScanDeduplicatesFiltersNonLocalCandidatesAndToleratesADeadServer()
    {
        var checkedEndpoints = new List<string>();
        var service = new LocalServerDiscovery((_, _) => Task.FromResult<IReadOnlyList<ServerCandidate>>([
            new("ws://127.0.0.1:4500"), new("ws://127.0.0.1:4500"), new("ws://example.com:4500"), new("ws://127.0.0.1:4501")]),
            (candidate, _, _, _) =>
            {
                lock (checkedEndpoints) checkedEndpoints.Add(candidate.Endpoint);
                if (candidate.Endpoint.EndsWith("4501")) throw new IOException("Server exited");
                return Task.FromResult<DiscoveredServer?>(new(candidate));
            });
        var found = await service.FindAsync(LocalSessionStore.DefaultHome, "codex");
        Assert.HasCount(1, found); Assert.HasCount(2, checkedEndpoints);
    }
    [TestMethod]
    public async Task CancellingScanCancelsItsProbes()
    {
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new LocalServerDiscovery((_, _) => Task.FromResult<IReadOnlyList<ServerCandidate>>([new("ws://127.0.0.1:4500")]),
            async (_, _, _, ct) => { begun.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; });
        using var cancellation = new CancellationTokenSource();
        var finding = service.FindAsync(LocalSessionStore.DefaultHome, "codex", cancellation.Token);
        await begun.Task; cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => finding);
    }
    [TestMethod]
    public void LocalSocketAddressResolvesHomeAndRejectsRemoteOrRelativePaths()
    {
        var home = Path.GetTempPath();
        Assert.IsTrue(SharedServerEndpoint.TryGetSocketPath("unix://", home, out var path));
        Assert.AreEqual(Path.Combine(Path.GetFullPath(home), "app-server-control", "app-server-control.sock"), path);
        Assert.IsFalse(SharedServerEndpoint.TryGetSocketPath("unix://relative.sock", home, out _));
        Assert.IsFalse(SharedServerEndpoint.TryGetSocketPath("unix://\\\\server\\share\\socket", home, out _));
        Assert.IsFalse(SharedServerEndpoint.IsAllowed("ws://192.168.1.2:4500", home));
    }
}
