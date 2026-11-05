namespace CodexMonitor.Core;

/// <summary>A view-only indication that an active server turn is awaiting its next item, not a guessed tool call.</summary>
public static class WorkProcessState
{
    public static bool IsPending(TranscriptEntry entry) => entry.Tool?.Name == "pending" && entry.Id.StartsWith("work-pending:", StringComparison.Ordinal);
    public static IReadOnlyList<TranscriptEntry> WithPending(IReadOnlyList<TranscriptEntry> entries, bool active, string? turnId)
    {
        if (!active || string.IsNullOrEmpty(turnId)) return entries;
        var current = entries.Where(e => e.TurnId == turnId).ToArray();
        if (current.Any(e => e.IsStreaming || e.Tool?.State is ToolActivityState.Running or ToolActivityState.Generating)) return entries;
        var boundary = entries.LastOrDefault(e => e.Kind is EntryKind.User or EntryKind.Assistant)?.Id ?? "start";
        var pending = new TranscriptEntry("work-pending:" + turnId + ":" + boundary, EntryKind.Tool, "", default,
            Tool: new("pending", "", State: ToolActivityState.Generating, SummaryOverride: "", CategoryOverride: "Working"), TurnId: turnId);
        return entries.Append(pending).ToArray();
    }
}
