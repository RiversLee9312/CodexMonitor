namespace CodexMonitor.Core;

public sealed record ServerTranscript(IReadOnlyList<TranscriptEntry> Entries, IReadOnlyDictionary<string, long> ItemWatermarks)
{
    // Snapshot pages and notifications share the transport's receive order. Do not replay deltas already represented in a page.
    public bool ShouldReplay(RpcMessage message)
    {
        var itemId = message.Parameters.Str("itemId", message.Parameters.Field("item").Str("id"));
        return message.Sequence == 0 || !ItemWatermarks.TryGetValue(itemId, out var watermark) || message.Sequence > watermark;
    }
}
