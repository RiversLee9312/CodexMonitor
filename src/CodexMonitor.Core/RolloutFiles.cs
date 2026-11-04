using ZstdSharp;
using System.Buffers;

namespace CodexMonitor.Core;

/// <summary>Logical JSONL offsets remain stable across Codex's plain/compressed transitions.</summary>
public static class RolloutFiles
{
    private sealed record Decoded(string Identity, string Path, FileStream Owner, long Length);
    private static readonly SemaphoreSlim DecodeGate = new(1);
    private static readonly LinkedList<Decoded> DecodedFiles = new();
    private const long CacheBudget = 512L * 1024 * 1024;
    public static bool IsCompressed(string path) => path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);
    public static string PlainPath(string path) => IsCompressed(path) ? path[..^4] : path;
    public static FileInfo? Find(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var plain = new FileInfo(PlainPath(path)); if (plain.Exists) return plain;
        var compressed = new FileInfo(plain.FullName + ".zst"); return compressed.Exists ? compressed : null;
    }
    public static bool Exists(string path) => Find(path) is not null;
    internal static IEnumerable<string> Enumerate(string directory) => Directory.EnumerateFiles(directory, "*.jsonl*", SearchOption.AllDirectories)
        .Where(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || IsCompressed(p) && !File.Exists(PlainPath(p)));

    internal static async Task<FileStream> OpenSeekableAsync(string path, CancellationToken ct)
    {
        // Codex can replace a representation between discovery and open. Prefer the live plain file.
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = Find(path) ?? throw new FileNotFoundException("Rollout representation unavailable");
                if (!IsCompressed(info.FullName)) return OpenRead(info.FullName);
                return await DecodeAsync(info, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException) when (attempt < 3) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
    }
    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
    private static async Task<FileStream> DecodeAsync(FileInfo info, CancellationToken ct)
    {
        var identity = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        await DecodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var node = DecodedFiles.First; node is not null; node = node.Next)
                if (node.Value.Identity == identity)
                { var value = node.Value; DecodedFiles.Remove(node); DecodedFiles.AddFirst(value); return OpenRead(value.Path); }
            var tempPath = Path.Combine(Path.GetTempPath(), "CodexMonitor-rollout-" + Guid.NewGuid().ToString("N") + ".tmp");
            var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.RandomAccess);
            try
            {
                await using var source = OpenRead(info.FullName);
                await using var decoder = new DecompressionStream(source);
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    while (await decoder.ReadAsync(buffer.AsMemory(0, 65536), ct).ConfigureAwait(false) is var count && count > 0)
                    { await temp.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); HistoryMetrics.DecodeBytes.Add(count); ct.ThrowIfCancellationRequested(); }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                await temp.FlushAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
                // Do not cache a partial or changed representation.
                var now = new FileInfo(info.FullName);
                if (!now.Exists || now.Length != info.Length || now.LastWriteTimeUtc != info.LastWriteTimeUtc) throw new IOException("Rollout changed during decompression");
                var decoded = new Decoded(identity, tempPath, temp, temp.Length);
                HistoryMetrics.Decoded.Add(1);
                var read = OpenRead(tempPath); DecodedFiles.AddFirst(decoded);
                while (DecodedFiles.Count > 2 || DecodedFiles.Count > 1 && DecodedFiles.Sum(v => v.Length) > CacheBudget)
                { var last = DecodedFiles.Last!; DecodedFiles.RemoveLast(); last.Value.Owner.Dispose(); }
                return read;
            }
            catch (Exception e)
            {
                await temp.DisposeAsync().ConfigureAwait(false);
                if (e is ZstdException) throw new IOException("Compressed rollout could not be decoded", e);
                throw;
            }
        }
        finally { DecodeGate.Release(); }
    }
    internal static async Task<byte[]> ReadPrefixAsync(string path, int maxBytes, CancellationToken ct)
    {
        await using var source = OpenRead(path);
        await using var decoder = new DecompressionStream(source);
        var bytes = new byte[maxBytes]; var length = 0;
        try { while (length < bytes.Length && await decoder.ReadAsync(bytes.AsMemory(length), ct).ConfigureAwait(false) is var n && n > 0) length += n; }
        catch (ZstdException e) { throw new IOException("Compressed rollout prefix unavailable", e); }
        return bytes.AsSpan(0, length).ToArray();
    }
}
