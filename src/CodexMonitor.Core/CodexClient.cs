using System.Text.Json;

namespace CodexMonitor.Core;

/// <summary>Versioned Codex app-server protocol. Does not edit rollouts or change authentication/sandbox policy.</summary>
public sealed class CodexClient(JsonRpcClient rpc) : IAsyncDisposable
{
    public JsonRpcClient Rpc { get; } = rpc;
    public bool IsConnected => Rpc.IsConnected;
    private static readonly string[] AllSources = ["cli", "vscode", "exec", "appServer", "subAgent", "subAgentReview", "subAgentCompact", "subAgentThreadSpawn", "subAgentOther", "unknown"];
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await Rpc.RequestAsync("initialize", new { clientInfo = new { name = "codex_monitor", title = "Codex Monitor", version = "0.1.0" }, capabilities = new { experimentalApi = true } }, ct).ConfigureAwait(false);
        await Rpc.NotifyAsync("initialized", new { }, ct).ConfigureAwait(false);
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
                var result = await Rpc.RequestAsync("thread/list", new { cursor, limit = 100, sortKey = "updated_at", sourceKinds = AllSources, modelProviders = Array.Empty<string>(), archived, useStateDbOnly = true }, ct).ConfigureAwait(false);
                all.AddRange(result.Field("data").Items().Select(x => ParseSession(x) with { IsArchived = archived }));
                cursor = result.Str("nextCursor");
                if (cursor.Length > 0 && !seen.Add(cursor)) throw new IOException("Repeated pagination cursor");
            } while (!string.IsNullOrEmpty(cursor));
        }
        return all;
    }
    public async Task<SessionInfo> ReadAsync(string id, CancellationToken ct = default)
    {
        var result = await Rpc.RequestAsync("thread/read", new { threadId = id }, ct).ConfigureAwait(false);
        return ParseSession(result.Field("thread"));
    }
    public async Task<SessionInfo> ResumeAsync(string id, CancellationToken ct = default)
    {
        var result = await Rpc.RequestAsync("thread/resume", new { threadId = id }, ct).ConfigureAwait(false);
        return ParseSession(result.Field("thread"));
    }
    public async Task<SessionInfo> StartAsync(string cwd, CancellationToken ct = default)
    {
        var result = await Rpc.RequestAsync("thread/start", new { cwd = Path.GetFullPath(cwd) }, ct).ConfigureAwait(false);
        return ParseSession(result.Field("thread"));
    }
    public async Task<string> SendAsync(string id, string text, string? activeTurnId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } };
        if (!string.IsNullOrEmpty(activeTurnId))
        {
            var steered = await Rpc.RequestAsync("turn/steer", new { threadId = id, expectedTurnId = activeTurnId, input }, ct).ConfigureAwait(false);
            return steered.Str("turnId");
        }
        var result = await Rpc.RequestAsync("turn/start", new { threadId = id, input }, ct).ConfigureAwait(false);
        return result.Field("turn").Str("id");
    }
    public Task InterruptAsync(string id, string turnId, CancellationToken ct = default) => Rpc.RequestAsync("turn/interrupt", new { threadId = id, turnId }, ct);

    public async Task<IReadOnlyList<TranscriptEntry>> ReadTranscriptAsync(string id, CancellationToken ct = default)
    {
        var entries = new List<TranscriptEntry>();
        string? cursor = null;
        var seen = new HashSet<string>();
        do
        {
            var result = await Rpc.RequestAsync("thread/turns/list", new { threadId = id, cursor, limit = 50, sortDirection = "asc", itemsView = "full" }, ct).ConfigureAwait(false);
            foreach (var turn in result.Field("data").Items())
                foreach (var item in turn.Field("items").Items())
                    entries.Add(ParseItem(item, turn.Date("createdAt")));
            cursor = result.Str("nextCursor");
            if (cursor.Length > 0 && !seen.Add(cursor)) throw new IOException("Repeated pagination cursor");
        } while (!string.IsNullOrEmpty(cursor));
        return entries;
    }

    public static TranscriptEntry ParseItem(JsonElement item, DateTimeOffset timestamp = default)
    {
        var type = item.Str("type");
        var kind = type switch { "userMessage" => EntryKind.User, "agentMessage" => EntryKind.Assistant, "reasoning" => EntryKind.Reasoning,
            "commandExecution" or "fileChange" or "mcpToolCall" or "dynamicToolCall" or "webSearch" => EntryKind.Tool, _ => EntryKind.Event };
        var text = type switch
        {
            "userMessage" => string.Join("\n", item.Field("content").Items().Select(x => x.Str("text", x.Str("type")))),
            "agentMessage" => item.Str("text"),
            "reasoning" => string.Join("\n", item.Field("summary").Items().Select(x => x.TextValue())),
            "commandExecution" => item.Str("command") + "\n" + item.Str("aggregatedOutput"),
            _ => item.GetRawText()
        };
        return new(item.Str("id", Guid.NewGuid().ToString()), kind, text, timestamp, type);
    }
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
            Model = p.Str("modelProvider"), ActiveTurnId = activeTurn.Str("id") is { Length: > 0 } tid ? tid : null,
            ParentId = p.Str("parentThreadId") is { Length: > 0 } parent ? parent : null };
    }
    public ValueTask DisposeAsync() => Rpc.DisposeAsync();
}
