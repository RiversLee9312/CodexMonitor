namespace CodexMonitor.Core;

public sealed record SessionServerMatch(DiscoveredServer Server, SessionInfo Session);
public interface ISessionServerLocator
{
    Task<IReadOnlyList<SessionServerMatch>> FindAsync(string sessionId, string home, string executable, string? configuredEndpoint = null, CancellationToken ct = default);
}

/// <summary>Matches runtime ownership, never a stored thread/list entry. Lookup does not resume a thread.</summary>
public sealed class SessionServerLocator : ISessionServerLocator
{
    private readonly IServerDiscovery _discovery;
    private readonly Func<DiscoveredServer, string, string, CancellationToken, Task<CodexClient>> _open;
    public SessionServerLocator(IServerDiscovery? discovery = null,
        Func<DiscoveredServer, string, string, CancellationToken, Task<CodexClient>>? open = null)
    { _discovery = discovery ?? new LocalServerDiscovery(); _open = open ?? OpenAsync; }
    public static async Task<CodexClient> OpenAsync(DiscoveredServer server, string home, string executable, CancellationToken ct)
    {
        var client = new CodexClient(new JsonRpcClient(await SharedServerEndpoint.ConnectAsync(server.Endpoint, home, executable, ct).ConfigureAwait(false)));
        try { await client.InitializeAsync(ct).ConfigureAwait(false); return client; }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task<IReadOnlyList<SessionServerMatch>> FindAsync(string sessionId, string home, string executable, string? configuredEndpoint = null, CancellationToken ct = default)
    {
        var servers = (await _discovery.FindAsync(home, executable, ct).ConfigureAwait(false)).ToList();
        if (configuredEndpoint is not null && SharedServerEndpoint.IsAllowed(configuredEndpoint, home))
            servers.Add(new(new(configuredEndpoint)));
        var matches = new System.Collections.Concurrent.ConcurrentBag<SessionServerMatch>();
        await Parallel.ForEachAsync(servers.Where(s => s.CanConnect).DistinctBy(s => s.Endpoint, StringComparer.OrdinalIgnoreCase),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (server, token) =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    await using var client = await _open(server, home, executable, timeout.Token).ConfigureAwait(false);
                    if ((await client.LoadedThreadIdsAsync(timeout.Token).ConfigureAwait(false)).Contains(sessionId))
                    {
                        var session = await client.RuntimeAsync(sessionId, timeout.Token).ConfigureAwait(false);
                        // A thread can unload between inventory and read; it then belongs to no live runtime here.
                        if (session.IsLive) matches.Add(new(server, session));
                    }
                }
                catch (Exception e) when (e is IOException or RpcException or OperationCanceledException or ArgumentException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException) { }
            }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return matches.OrderBy(s => s.Server.Endpoint, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
