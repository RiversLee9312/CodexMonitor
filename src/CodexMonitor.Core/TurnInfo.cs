using System.Text.Json;

namespace CodexMonitor.Core;

public sealed record TokenBreakdown(long? Input, long? CachedInput, long? CacheWriteInput, long? Output, long? Reasoning, long? Total)
{
    public static TokenBreakdown? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        long? Read(string camel, string snake) => (value.Field(camel).TryNumber(out var n) || value.Field(snake).TryNumber(out n)) && n >= 0 ? n : null;
        var result = new TokenBreakdown(Read("inputTokens", "input_tokens"), Read("cachedInputTokens", "cached_input_tokens"), Read("cacheWriteInputTokens", "cache_write_input_tokens"),
            Read("outputTokens", "output_tokens"), Read("reasoningOutputTokens", "reasoning_output_tokens"), Read("totalTokens", "total_tokens"));
        return result is { Input: null, CachedInput: null, CacheWriteInput: null, Output: null, Reasoning: null, Total: null } ? null : result;
    }
}

/// <summary>Only supplied facts. Usage.last is one model request, not a whole turn.</summary>
public sealed record TurnInfo(string Id)
{
    public string? Status { get; init; }
    public string? Model { get; init; }
    public string ModelSource { get; init; } = "ModelRecorded";
    public string? Provider { get; init; }
    public string? Effort { get; init; }
    public string? ServiceTier { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long? DurationMs { get; init; }
    public string? Error { get; init; }
    public TokenBreakdown? LastRequest { get; init; }
    public TokenBreakdown? ThreadTotal { get; init; }
    public long? ContextWindow { get; init; }
    public DateTimeOffset? FirstRecord { get; init; }
    public DateTimeOffset? LastRecord { get; init; }
    public int LoadedItems { get; init; }
    public static TurnInfo ReadLifecycle(TurnInfo before, JsonElement turn)
    {
        DateTimeOffset? Date(string key) => turn.Field(key).TryNumber(out var n) && n is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(n) : null;
        return before with { Status = turn.Str("status", before.Status ?? ""), StartedAt = Date("startedAt") ?? before.StartedAt,
            CompletedAt = Date("completedAt") ?? before.CompletedAt, DurationMs = turn.Field("durationMs").TryNumber(out var ms) && ms >= 0 ? ms : before.DurationMs,
            Error = turn.Field("error").Str("message") is { Length: > 0 } error ? error : before.Error };
    }
}
