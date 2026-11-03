using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CodexMonitor.Core;

public sealed record ServerCandidate(string Endpoint, int? ProcessId = null, string? SocketPath = null);
public sealed record DiscoveredServer(ServerCandidate Candidate, bool RequiresAuthentication = false)
{
    public string Endpoint => Candidate.Endpoint;
    public bool CanConnect => !RequiresAuthentication;
    public bool IsLocalDaemon => Candidate.SocketPath is not null;
}
public interface IServerDiscovery
{
    Task<IReadOnlyList<DiscoveredServer>> FindAsync(string codexHome, string executable, CancellationToken ct = default);
}

public static class SharedServerEndpoint
{
    public static string DefaultSocketPath(string home) => Path.Combine(Path.GetFullPath(home), "app-server-control", "app-server-control.sock");
    public static bool TryGetSocketPath(string address, string home, out string path)
    {
        path = "";
        if (!address.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var raw = address[7..];
            path = raw.Length == 0 ? DefaultSocketPath(home) : raw;
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
            path = Path.GetFullPath(path);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { path = ""; return false; }
    }
    public static bool IsAllowed(string address, string home) => WebSocketTransport.IsAllowedEndpoint(address) || TryGetSocketPath(address, home, out _);
    public static async Task<IRpcTransport> ConnectAsync(string address, string home, string executable, CancellationToken ct = default)
    {
        if (TryGetSocketPath(address, home, out var socket)) return await WebSocketTransport.ConnectLocalSocketAsync(socket, ct).ConfigureAwait(false);
        return await WebSocketTransport.ConnectAsync(address, ct).ConfigureAwait(false);
    }
}

/// <summary>Discovers only Codex-owned local listeners and known control sockets. Never starts a daemon or a turn.</summary>
public sealed class LocalServerDiscovery : IServerDiscovery
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ServerCandidate>>> _catalog;
    private readonly Func<ServerCandidate, string, string, CancellationToken, Task<DiscoveredServer?>> _probe;
    public LocalServerDiscovery() : this(ReadCandidatesAsync, ProbeAsync) { }
    public LocalServerDiscovery(Func<string, CancellationToken, Task<IReadOnlyList<ServerCandidate>>> catalog) : this(catalog, ProbeAsync) { }
    public LocalServerDiscovery(Func<string, CancellationToken, Task<IReadOnlyList<ServerCandidate>>> catalog,
        Func<ServerCandidate, string, string, CancellationToken, Task<DiscoveredServer?>> probe)
    { _catalog = catalog; _probe = probe; }

    public async Task<IReadOnlyList<DiscoveredServer>> FindAsync(string codexHome, string executable, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        var found = new ConcurrentBag<DiscoveredServer>();
        try
        {
            var candidates = await _catalog(codexHome, budget.Token).ConfigureAwait(false);
            await Parallel.ForEachAsync(candidates.Where(c => SharedServerEndpoint.IsAllowed(c.Endpoint, codexHome))
                .DistinctBy(c => c.Endpoint, StringComparer.OrdinalIgnoreCase).Take(128),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (candidate, token) =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        if (await _probe(candidate, codexHome, executable, timeout.Token).ConfigureAwait(false) is { } server) found.Add(server);
                    }
                    catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or SocketException or
                        System.Net.WebSockets.WebSocketException or RpcException or OperationCanceledException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        return found.OrderBy(s => s.RequiresAuthentication).ThenByDescending(s => s.IsLocalDaemon).ThenBy(s => s.Endpoint, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Task<IReadOnlyList<ServerCandidate>> ReadCandidatesAsync(string home, CancellationToken ct) => Task.Run<IReadOnlyList<ServerCandidate>>(() =>
    {
        var candidates = new List<ServerCandidate>();
        var homes = CodexHomeDiscovery.CandidatePaths(home).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>();
        foreach (var root in homes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var path = SharedServerEndpoint.DefaultSocketPath(root);
                if (File.Exists(path)) candidates.Add(new(Path.GetFullPath(root).Equals(Path.GetFullPath(home), StringComparison.OrdinalIgnoreCase) ? "unix://" : "unix://" + path.Replace('\\', '/'), SocketPath: path));
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsWindows())
        {
            var ids = new HashSet<int>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { if (IsCodexProcess(process.ProcessName)) ids.Add(process.Id); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            try { if (ids.Count > 0) candidates.AddRange(CreateTcpCandidates(WindowsTcpListeners.Read(), ids)); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { } // Known local sockets remain usable if TCP inventory is unavailable.
        }
        return candidates;
    }, ct);

    public static bool IsCodexProcess(string name) => name.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("codex-cli", StringComparison.OrdinalIgnoreCase) || name.StartsWith("codex-x86_64-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("codex-aarch64-", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ServerCandidate> CreateTcpCandidates(IEnumerable<OwnedTcpListener> listeners, IReadOnlySet<int> codexProcessIds)
    {
        var result = new List<ServerCandidate>();
        foreach (var listener in listeners)
        {
            if (!codexProcessIds.Contains(listener.ProcessId) || listener.Port is < 1 or > 65535) continue;
            var address = listener.Address;
            if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
            else if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
            if (!IPAddress.IsLoopback(address)) continue;
            result.Add(new(new UriBuilder("ws", address.ToString(), listener.Port).Uri.GetLeftPart(UriPartial.Authority), listener.ProcessId));
        }
        return result.DistinctBy(c => c.Endpoint, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<DiscoveredServer?> ProbeAsync(ServerCandidate candidate, string home, string executable, CancellationToken ct)
    {
        try
        {
            await using var client = new CodexClient(new JsonRpcClient(await SharedServerEndpoint.ConnectAsync(candidate.Endpoint, home, executable, ct).ConfigureAwait(false)));
            await client.InitializeAsync(ct).ConfigureAwait(false);
            return client.UserAgent.Contains("codex", StringComparison.OrdinalIgnoreCase) ? new(candidate) : null;
        }
        catch (ServerAuthenticationRequiredException) { return new(candidate, RequiresAuthentication: true); }
    }
}
