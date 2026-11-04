using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMonitor.Core;

/// <summary>Versioned Codex app-server protocol. Does not edit rollouts or change authentication/sandbox policy.</summary>
public sealed class CodexClient(JsonRpcClient rpc) : IAsyncDisposable
{
    public JsonRpcClient Rpc { get; } = rpc;
    public bool IsConnected => Rpc.IsConnected;
    public string UserAgent { get; private set; } = "";
    public ModelTimeline Models { get; } = new();
    private static readonly string[] AllSources = ["cli", "vscode", "exec", "appServer", "subAgent", "subAgentReview", "subAgentCompact", "subAgentThreadSpawn", "subAgentOther", "unknown"];
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var result = await Rpc.RequestAsync("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "codex_monitor", ["title"] = "Codex Monitor", ["version"] = "0.1.16" }, ["capabilities"] = new JsonObject { ["experimentalApi"] = true } }, ct).ConfigureAwait(false);
        UserAgent = result.Str("userAgent");
        await Rpc.NotifyAsync("initialized", new JsonObject(), ct).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<SessionInfo>();
        foreach (var archived in new[] { false, true })
        {
            string? cursor = null;
            var seen = new HashSet<string>();
            do
            {
                var result = await Rpc.RequestAsync("thread/list", new JsonObject { ["cursor"] = cursor, ["limit"] = 100, ["sortKey"] = "updated_at", ["sourceKinds"] = new JsonArray(AllSources.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["modelProviders"] = new JsonArray(), ["archived"] = archived, ["useStateDbOnly"] = true }, ct).ConfigureAwait(false);
                all.AddRange(result.Field("data").Items().Select(x => ParseSession(x) with { IsArchived = archived }));
                cursor = result.Str("nextCursor");
                if (cursor.Length > 0 && !seen.Add(cursor)) throw new IOException("Repeated pagination cursor");
            } while (!string.IsNullOrEmpty(cursor));
        }
        return all;
    }
    public async Task<SessionPage> ListPageAsync(bool archived, string? cursor = null, CancellationToken ct = default)
    {
        var result = await Rpc.RequestAsync("thread/list", new JsonObject { ["cursor"] = cursor, ["limit"] = SessionCatalog.PageSize,
            ["sortKey"] = "updated_at", ["sortDirection"] = "desc", ["sourceKinds"] = new JsonArray(AllSources.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["modelProviders"] = new JsonArray(), ["archived"] = archived, ["useStateDbOnly"] = true }, ct).ConfigureAwait(false);
        var next = result.Str("nextCursor"); if (next.Length > 0 && next == cursor) throw new IOException("Repeated session pagination cursor");
        return new(result.Field("data").Items().Select(s => ParseSession(s) with { IsArchived = archived }).ToArray(), next.Length > 0 ? next : null);
    }
    public async Task<SessionInfo> ReadAsync(string id, CancellationToken ct = default)
    {
        var result = await Rpc.RequestAsync("thread/read", new JsonObject { ["threadId"] = id }, ct).ConfigureAwait(false);
        return ParseSession(result.Field("thread")) with { Model = Models.Current(id) ?? "" };
    }
    public async Task<IReadOnlySet<string>> LoadedThreadIdsAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal); var cursors = new HashSet<string>(); string? cursor = null;
        do
        {
            var page = await Rpc.RequestAsync("thread/loaded/list", new JsonObject { ["cursor"] = cursor, ["limit"] = 100 }, ct).ConfigureAwait(false);
            foreach (var id in page.Field("data").Items().Select(x => x.TextValue()).Where(x => x.Length > 0)) ids.Add(id);
            cursor = page.Str("nextCursor");
            if (cursor.Length > 0 && !cursors.Add(cursor)) throw new IOException("Repeated pagination cursor");
        } while (!string.IsNullOrEmpty(cursor));
        return ids;
    }
    public async Task<SessionInfo> RuntimeAsync(string id, CancellationToken ct = default)
    {
        var session = await ReadAsync(id, ct).ConfigureAwait(false);
        return await WithActiveTurnAsync(session, ct).ConfigureAwait(false);
    }
    public async Task<SessionInfo> WithActiveTurnAsync(SessionInfo session, CancellationToken ct = default)
    {
        if (session.State is not (SessionState.Active or SessionState.Waiting) || session.ActiveTurnId is not null) return session;
        var page = await Rpc.RequestAsync("thread/turns/list", new JsonObject { ["threadId"] = session.Id, ["limit"] = 100, ["sortDirection"] = "desc", ["itemsView"] = "notLoaded" }, ct).ConfigureAwait(false);
        var turn = page.Field("data").Items().FirstOrDefault(x => x.Str("status") == "inProgress");
        return session with { ActiveTurnId = turn.Str("id") is { Length: > 0 } value ? value : null };
    }
    public async Task<SessionInfo> ResumeAsync(string id, CancellationToken ct = default)
    {
        var result = await Rpc.RequestAsync("thread/resume", new JsonObject { ["threadId"] = id, ["excludeTurns"] = true }, ct).ConfigureAwait(false);
        Models.Configure(id, result.Str("model"));
        return ParseSession(result.Field("thread")) with { Model = result.Str("model") };
    }
    public async Task<SessionInfo> StartAsync(string cwd, CancellationToken ct = default)
    {
        var result = await Rpc.RequestAsync("thread/start", new JsonObject { ["cwd"] = Path.GetFullPath(cwd) }, ct).ConfigureAwait(false);
        var session = ParseSession(result.Field("thread")) with { Model = result.Str("model") };
        Models.Configure(session.Id, session.Model); return session;
    }
    public async Task<string> SendAsync(string id, string text, string? activeTurnId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["text_elements"] = new JsonArray() });
        if (!string.IsNullOrEmpty(activeTurnId))
        {
            var steered = await Rpc.RequestAsync("turn/steer", new JsonObject { ["threadId"] = id, ["expectedTurnId"] = activeTurnId, ["input"] = input }, ct).ConfigureAwait(false);
            return steered.Str("turnId");
        }
        var result = await Rpc.RequestAsync("turn/start", new JsonObject { ["threadId"] = id, ["input"] = input }, ct).ConfigureAwait(false);
        return result.Field("turn").Str("id");
    }
    public Task InterruptAsync(string id, string turnId, CancellationToken ct = default) => Rpc.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = id, ["turnId"] = turnId }, ct);

    public async Task<IReadOnlyList<TranscriptEntry>> ReadTranscriptAsync(string id, CancellationToken ct = default)
        => (await ReadTranscriptStateAsync(id, ct).ConfigureAwait(false)).Entries;
    public async Task<ServerTranscript> ReadTranscriptPageAsync(string id, string? cursor = null, CancellationToken ct = default)
    {
        var response = await Rpc.RequestStampedAsync("thread/items/list", new JsonObject { ["threadId"] = id, ["cursor"] = cursor, ["limit"] = 100, ["sortDirection"] = "desc" }, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var entries = new List<TranscriptEntry>(); var watermarks = new Dictionary<string, long>();
            foreach (var row in response.Value.Field("data").Items().Reverse())
            {
                ct.ThrowIfCancellationRequested();
                var item = row.Field("item"); var time = row.Field("startedAtMs").TryNumber(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : default;
                var entry = ParseItem(item, time, row.Field("completedAtMs").TryNumber(out _) ? ToolActivityState.Completed : null) with { TurnId = row.Str("turnId"), Model = item.Str("model") is { Length: > 0 } model ? model : Models.ForItem(id, item.Str("id")) };
                entry.Tool?.PreparePresentation();
                entries.Add(entry); Models.Remember(id, entry.Id, entry.Model); watermarks[entry.Id] = response.Sequence;
            }
            var next = response.Value.Str("nextCursor");
            if (next.Length > 0 && next == cursor) throw new IOException("Repeated pagination cursor");
            return new ServerTranscript(entries, watermarks, next.Length > 0 ? next : null);
        }, ct).ConfigureAwait(false);
    }
    public async Task<ServerTranscript> ReadTranscriptStateAsync(string id, CancellationToken ct = default)
    {
        var entries = new List<TranscriptEntry>();
        var watermarks = new Dictionary<string, long>();
        string? cursor = null;
        var seen = new HashSet<string>();
        do
        {
            var response = await Rpc.RequestStampedAsync("thread/turns/list", new JsonObject { ["threadId"] = id, ["cursor"] = cursor, ["limit"] = 50, ["sortDirection"] = "asc", ["itemsView"] = "full" }, ct).ConfigureAwait(false);
            var result = response.Value;
            foreach (var turn in result.Field("data").Items())
            {
                var turnId = turn.Str("id"); var active = turn.Str("status") == "inProgress";
                if (active) Models.Start(id, turnId);
                var model = turn.Str("model") is { Length: > 0 } explicitModel ? explicitModel : null;
                foreach (var item in turn.Field("items").Items())
                { var entry = ParseItem(item, turn.Date("createdAt"), turn.Str("status") == "completed" ? ToolActivityState.Completed : null) with { TurnId = turnId, Model = item.Str("model") is { Length: > 0 } itemModel ? itemModel : Models.ForItem(id, item.Str("id")) ?? model }; entries.Add(entry); Models.Remember(id, entry.Id, entry.Model); watermarks[entry.Id] = response.Sequence; }
            }
            cursor = result.Str("nextCursor");
            if (cursor.Length > 0 && !seen.Add(cursor)) throw new IOException("Repeated pagination cursor");
        } while (!string.IsNullOrEmpty(cursor));
        return new(entries, watermarks);
    }

    public static TranscriptEntry ParseItem(JsonElement item, DateTimeOffset timestamp = default, ToolActivityState? lifecycle = null)
        => TranscriptItemAdapter.Parse(item, timestamp, lifecycle);
    public static SessionState ParseState(JsonElement status) => status.Str("type") switch
        {
            "active" when status.Field("activeFlags").Items().Any(x => x.TextValue() == "waitingOnApproval") => SessionState.Waiting,
            "active" => SessionState.Active, "idle" => SessionState.Idle, "systemError" => SessionState.Error, _ => SessionState.Unknown
        };
    public static SessionInfo ParseSession(JsonElement p)
    {
        var state = ParseState(p.Field("status"));
        var activeTurn = p.Field("turns").Items().LastOrDefault(x => x.Str("status") == "inProgress");
        return new SessionInfo { Id = p.Str("id"), Title = p.Str("name", p.Str("preview")), Cwd = p.Str("cwd"), Source = p.Field("source").TextValue(),
            CreatedAt = p.Date("createdAt"), UpdatedAt = p.Date("updatedAt"), State = state, IsLive = state != SessionState.Unknown, FilePath = p.Str("path"),
            Model = p.Str("model"), ActiveTurnId = activeTurn.Str("id") is { Length: > 0 } tid ? tid : null,
            ParentId = p.Str("parentThreadId") is { Length: > 0 } parent ? parent : null };
    }
    public ValueTask DisposeAsync() => Rpc.DisposeAsync();
}
