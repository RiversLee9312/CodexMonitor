using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace CodexMonitor.Core;

public sealed partial class CodexClient
{
    private sealed class TurnPages { internal readonly SemaphoreSlim Gate = new(1); internal string? Cursor; internal bool End; }
    private readonly ConcurrentDictionary<string, TurnPages> _turnPages = new(StringComparer.Ordinal);
    /// <summary>Fetch lightweight lifecycle metadata on demand. Never hydrate turn items or guess historical models.</summary>
    public async Task<TurnInfo?> ReadTurnInfoAsync(string thread, string turn, DateTimeOffset? firstRecord = null, CancellationToken ct = default)
    {
        var pages = _turnPages.GetOrAdd(thread, static _ => new());
        await pages.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Models.Details(thread, turn) is { Status: not null } cached) return cached;
            // Bound random access when old servers omit dates; subsequent requests reuse this frontier.
            for (int attempt = 0; attempt < (firstRecord is null ? 2 : 20) && !pages.End; attempt++)
            {
                var result = await Rpc.RequestAsync("thread/turns/list", new JsonObject { ["threadId"] = thread, ["cursor"] = pages.Cursor, ["limit"] = 50,
                    ["sortDirection"] = "desc", ["itemsView"] = "notLoaded" }, ct).ConfigureAwait(false);
                var oldest = DateTimeOffset.MaxValue;
                foreach (var item in result.Field("data").Items())
                {
                    var id = item.Str("id"); Models.Record(thread, id, old => TurnInfo.ReadLifecycle(old, item));
                    if (Models.Details(thread, id)?.StartedAt is { } start && start < oldest) oldest = start;
                }
                var next = result.Str("nextCursor");
                if (next.Length > 0 && next == pages.Cursor) throw new IOException("Repeated turn metadata cursor");
                pages.Cursor = next.Length > 0 ? next : null; pages.End = pages.Cursor is null;
                if (Models.Details(thread, turn) is { Status: not null } found) return found;
                if (firstRecord is { } boundary && oldest < boundary) break;
            }
            return Models.Details(thread, turn);
        }
        finally { pages.Gate.Release(); }
    }
}
