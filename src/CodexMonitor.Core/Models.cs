using System.Text.Json;

namespace CodexMonitor.Core;

public enum SessionState { Unknown, RecordedActive, Active, Idle, Completed, Interrupted, Error, Waiting }
public enum EntryKind { User, Assistant, Tool, Reasoning, Event }
public readonly record struct TranscriptFileVersion(long Length, DateTime LastWriteTimeUtc);

public sealed record SessionInfo
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string Source { get; init; } = "";
    public string? ParentId { get; init; }
    public string Model { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public SessionState State { get; init; }
    public string? ActiveTurnId { get; init; }
    public bool IsArchived { get; init; }
    public bool IsLive { get; init; }
    public TranscriptFileVersion? FileVersion { get; init; }
    public string FolderName => string.IsNullOrEmpty(Cwd) ? "" : Path.GetFileName(Cwd.TrimEnd('\\', '/'));
}

public sealed record TranscriptEntry(string Id, EntryKind Kind, string Text, DateTimeOffset Timestamp,
    string? Label = null, string? RawJson = null, ToolActivity? Tool = null, ReasoningContent? Reasoning = null,
    string? TurnId = null, string? Model = null);

public sealed record TranscriptSnapshot(SessionInfo Session, IReadOnlyList<TranscriptEntry> Entries,
    int InvalidRecords = 0, int UnknownRecords = 0, long Tokens = 0);

public static class JsonFields
{
    public static JsonElement Field(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p : default;
    public static string Str(this JsonElement e, string name, string fallback = "") =>
        e.Field(name) is { ValueKind: JsonValueKind.String } p ? p.GetString() ?? fallback : fallback;
    public static string TextValue(this JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" :
        e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : e.GetRawText();
    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    public static DateTimeOffset Date(this JsonElement e, string name, DateTimeOffset fallback = default) =>
        DateTimeOffset.TryParse(e.Str(name), out var dt) ? dt :
        e.Field(name).TryNumber(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : fallback;
    public static bool TryNumber(this JsonElement e, out long n) { n = 0; return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out n); }
}
