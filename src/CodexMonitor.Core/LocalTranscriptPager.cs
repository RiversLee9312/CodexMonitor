namespace CodexMonitor.Core;

public sealed record LocalTranscriptPage(TranscriptSnapshot Snapshot, long StartOffset, long EndOffset)
{ public bool HasEarlier => StartOffset > 0; public long WindowBytes => EndOffset - StartOffset; }

/// <summary>Reads from the tail, without scanning or parsing the entire rollout first.</summary>
public sealed class LocalTranscriptPager
{
    public const int PageRecords = 300;
    public async Task<LocalTranscriptPage> ReadAsync(SessionInfo session, long? loadedStart = null, bool older = false, CancellationToken ct = default)
    {
        await using var stream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var end = stream.Length;
        var start = loadedStart is { } known && known <= end ? known : end;
        if (loadedStart is null || loadedStart > end || older) start = await FindStartAsync(stream, start, ct);
        var snapshot = await new TranscriptParser().ReadRangeAsync(session, start, end, ct);
        return new(snapshot, start, end);
    }
    private static async Task<long> FindStartAsync(FileStream stream, long end, CancellationToken ct)
    {
        var buffer = new byte[65536]; var position = end; var records = 0; long earliestBoundary = end;
        while (position > 0)
        {
            ct.ThrowIfCancellationRequested(); var size = (int)Math.Min(buffer.Length, position); position -= size; stream.Position = position;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, size), ct);
            for (int i = size - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n' || position + i == end - 1) continue;
                earliestBoundary = position + i + 1;
                if (++records >= PageRecords) return earliestBoundary;
            }
            // A byte budget bounds ordinary pages; a single record remains atomic.
            if (end - position >= 2 * 1024 * 1024 && earliestBoundary < end) return earliestBoundary;
            if (end - position > 64 * 1024 * 1024) throw new IOException("A single transcript record exceeds the preview limit");
        }
        return 0;
    }
}
