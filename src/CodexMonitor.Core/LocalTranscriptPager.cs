namespace CodexMonitor.Core;

public sealed record LocalTranscriptPage(TranscriptSnapshot Snapshot, long StartOffset, long EndOffset)
{ public bool HasEarlier => StartOffset > 0; public long WindowBytes => EndOffset - StartOffset; }

/// <summary>Reads from the tail, without scanning or parsing the entire rollout first.</summary>
public sealed class LocalTranscriptPager(bool retainSnapshot = false) : IAsyncDisposable
{
    public const int PageRecords = 300;
    private readonly SemaphoreSlim _reads = new(1);
    private readonly CancellationTokenSource _disposed = new();
    private FileStream? _snapshot;
    private string? _identity;
    private LocalTranscriptPage? _lastPage;
    private SessionInfo? _lastInput;
    private int _isDisposed;
    public async Task<LocalTranscriptPage> ReadAsync(SessionInfo session, long? loadedStart = null, bool older = false, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        return await Task.Run(() => ReadCoreAsync(session, loadedStart, older, linked.Token), linked.Token).ConfigureAwait(false);
    }
    private async Task<LocalTranscriptPage> ReadCoreAsync(SessionInfo session, long? loadedStart, bool older, CancellationToken ct)
    {
        await _reads.WaitAsync(ct).ConfigureAwait(false);
        FileStream? stream = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var info = RolloutFiles.Find(session.FilePath) ?? throw new FileNotFoundException("Rollout unavailable");
            var identity = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            if (_snapshot is not null && _identity != identity) { await _snapshot.DisposeAsync().ConfigureAwait(false); _snapshot = null; _lastPage = null; }
            stream = _snapshot ?? await RolloutFiles.OpenSeekableAsync(info.FullName, ct).ConfigureAwait(false);
            if (retainSnapshot) { _snapshot = stream; _identity = identity; }
            var end = stream.Length;
            var start = loadedStart is { } known && known <= end ? known : end;
            if (loadedStart is null || loadedStart > end || older) start = await FindStartAsync(stream, start, ct).ConfigureAwait(false);
            if (retainSnapshot && _lastPage is { } cached && cached.StartOffset == start && cached.EndOffset == end && _lastInput == session) return cached;
            var snapshot = await new TranscriptParser().ReadStreamAsync(session, stream, start, end, ct).ConfigureAwait(false);
            var page = new LocalTranscriptPage(snapshot, start, end);
            if (retainSnapshot) { _lastPage = page; _lastInput = session; }
            return page;
        }
        finally { if (!retainSnapshot && stream is not null) await stream.DisposeAsync().ConfigureAwait(false); _reads.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return ValueTask.CompletedTask;
        _disposed.Cancel();
        return new(Task.Run(async () =>
        {
            await _reads.WaitAsync().ConfigureAwait(false);
            try { if (_snapshot is not null) await _snapshot.DisposeAsync().ConfigureAwait(false); _snapshot = null; }
            finally { _reads.Release(); }
        }));
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
