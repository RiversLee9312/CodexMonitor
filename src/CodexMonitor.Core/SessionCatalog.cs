namespace CodexMonitor.Core;

/// <summary>Small server pages and a local metadata index; no row models or transcript hydration.</summary>
public sealed class SessionCatalog(IReadOnlyList<SessionInfo> local, Func<bool, string?, CancellationToken, Task<SessionPage>>? readRemote = null)
{
    public const int PageSize = 50;
    private IReadOnlyList<SessionInfo> _local = local;
    private readonly Dictionary<string, SessionInfo> _remote = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionInfo> _observed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Feed[] _feeds = [new(false), new(true)];
    private sealed class Feed(bool archived)
    {
        internal bool Archived = archived, Started, End;
        internal string? Cursor;
        internal DateTimeOffset Oldest = DateTimeOffset.MaxValue;
        internal readonly HashSet<string> Cursors = [];
    }
    public void SetLocal(IReadOnlyList<SessionInfo> sessions) => _local = sessions;
    public void Observe(SessionInfo session) { lock (_observed) _observed[session.Id] = session; }
    public async Task<SessionListing> ReadAsync(SessionQuery query, int limit, bool refresh, TimeZoneInfo zone, CancellationToken ct = default)
    {
        if (!query.IsValid) return new(Array.Empty<SessionInfo>(), Array.Empty<SessionInfo>(), 0, false, false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (readRemote is not null && refresh)
                foreach (var feed in _feeds.Where(f => query.Scope != SessionScope.Archived || f.Archived)) await ReadFeed(feed, true, ct).ConfigureAwait(false);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var map = _local.ToDictionary(s => s.Id, StringComparer.Ordinal);
                foreach (var server in _remote.Values) map[server.Id] = map.TryGetValue(server.Id, out var disk) ? SessionMetadata.Merge(disk, server) : server;
                lock (_observed) foreach (var (id, observed) in _observed)
                    if (map.TryGetValue(id, out var stored)) map[id] = stored with
                    { State = observed.State, IsLive = observed.IsLive, ActiveTurnId = observed.ActiveTurnId, Title = observed.Title, UpdatedAt = observed.UpdatedAt > stored.UpdatedAt ? observed.UpdatedAt : stored.UpdatedAt };
                var known = map.Values.ToArray();
                var matching = known.Where(s => query.Matches(s, zone)).OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
                var cutoff = matching.Length >= limit ? matching[limit - 1].UpdatedAt : DateTimeOffset.MinValue;
                var candidates = readRemote is null ? [] : _feeds.Where(f => !f.End && (query.Scope != SessionScope.Archived || f.Archived) &&
                    // The server sorts by update time; stop only when an update-time lower bound proves the rest cannot match.
                    !(query.DateField == SessionDateField.Updated && query.From is { } from && f.Started && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(f.Oldest, zone).DateTime) < from)).ToArray();
                var needed = candidates.FirstOrDefault(f => !f.Started || f.Oldest > cutoff);
                if (needed is null) return new(matching.Take(limit).ToArray(), known, matching.Length, matching.Length > limit || candidates.Length > 0, candidates.Length > 0) { Index = map };
                await ReadFeed(needed, false, ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }
    private async Task ReadFeed(Feed feed, bool refresh, CancellationToken ct)
    {
        var cursor = refresh ? null : feed.Cursor;
        var page = await readRemote!(feed.Archived, cursor, ct).WaitAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        if (!refresh && page.NextCursor is { } next && !feed.Cursors.Add(next)) throw new IOException("Repeated session pagination cursor");
        var overlapsKnown = page.Sessions.Any(s => _remote.ContainsKey(s.Id));
        foreach (var s in page.Sessions) _remote[s.Id] = s with { ContentRevision = "server:" + s.UpdatedAt.UtcTicks };
        // A burst of new conversations can replace the whole first page. Resume from its
        // cursor, otherwise the old frontier can skip the unseen part of that burst.
        if (!feed.Started || !refresh || !overlapsKnown && page.NextCursor is not null)
        {
            if (refresh) { feed.Cursors.Clear(); if (page.NextCursor is { } refreshedCursor) feed.Cursors.Add(refreshedCursor); }
            feed.Started = true; feed.Cursor = page.NextCursor; feed.End = page.NextCursor is null;
            feed.Oldest = page.Sessions.Count > 0 ? page.Sessions.Min(s => s.UpdatedAt) : feed.Oldest;
        }
    }
}

