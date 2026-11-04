namespace CodexMonitor.Core;

public sealed record ServerTranscript(IReadOnlyList<TranscriptEntry> Entries, IReadOnlyDictionary<string, long> ItemWatermarks, string? NextCursor = null)
{
    public static IReadOnlyList<TranscriptEntry> Merge(IReadOnlyList<TranscriptEntry> existing, IReadOnlyList<TranscriptEntry> page, bool older)
    {
        var ids = existing.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (older) return page.Where(e => !ids.Contains(e.Id)).Concat(existing).ToArray();
        var replacements = page.ToDictionary(e => e.Id); var result = existing.Select(e => replacements.TryGetValue(e.Id, out var update) ? update with { Model = update.Model ?? e.Model } : e).ToList();
        result.AddRange(page.Where(e => !ids.Contains(e.Id))); return result;
    }
    // Snapshot pages and notifications share the transport's receive order. Do not replay deltas already represented in a page.
    public bool ShouldReplay(RpcMessage message)
    {
        if (message.Method == "item/commandExecution/terminalInteraction") return true; // Stdin is not part of a historical item snapshot.
        var itemId = message.Parameters.Str("itemId", message.Parameters.Field("item").Str("id"));
        return message.Sequence == 0 || !ItemWatermarks.TryGetValue(itemId, out var watermark) || message.Sequence > watermark;
    }
}
