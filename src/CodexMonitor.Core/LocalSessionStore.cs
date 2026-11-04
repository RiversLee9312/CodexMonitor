using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexMonitor.Core;

public sealed partial class LocalSessionStore(string home)
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, SessionInfo Info)> _cache = new();
    public string Home { get; } = Path.GetFullPath(home);
    public static string DefaultHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct = default)
    {
        var titles = new Dictionary<string, string>();
        var indexPath = Path.Combine(Home, "session_index.jsonl");
        if (File.Exists(indexPath))
        {
            await using var index = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lines = new StreamReader(index);
            while (await lines.ReadLineAsync(ct) is { } line)
            {
                try { using var d = JsonDocument.Parse(line); var r = d.RootElement; titles[r.Str("id")] = r.Str("thread_name"); }
                catch (JsonException) { }
            }
        }
        var result = new ConcurrentBag<SessionInfo>();
        var files = new[] { "sessions", "archived_sessions" }.SelectMany(dir =>
            Directory.Exists(Path.Combine(Home, dir)) ? Directory.EnumerateFiles(Path.Combine(Home, dir), "*.jsonl", SearchOption.AllDirectories) : []);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (path, token) =>
        {
            try
            {
                var file = new FileInfo(path);
                SessionInfo info;
                if (_cache.TryGetValue(path, out var c) && c.Length == file.Length && c.Modified == file.LastWriteTimeUtc) info = c.Info;
                else
                {
                    var match = IdPattern().Match(file.Name);
                    info = new SessionInfo { Id = match.Success ? match.Value : Path.GetFileNameWithoutExtension(path), FilePath = path,
                        UpdatedAt = file.LastWriteTimeUtc, CreatedAt = file.CreationTimeUtc, FileVersion = new(file.Length, file.LastWriteTimeUtc),
                        IsArchived = Path.GetRelativePath(Home, path).StartsWith("archived_sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) };
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                    // Metadata and recent lifecycle markers never require scanning a multi-gigabyte rollout.
                    const int headBytes = 256 * 1024, tailBytes = 1024 * 1024;
                    var length = stream.Length;
                    var ranges = length <= headBytes + tailBytes ? new[] { (Start: 0L, End: length) } : new[] { (Start: 0L, End: (long)headBytes), (Start: length - tailBytes, End: length) };
                    foreach (var range in ranges)
                    {
                        stream.Position = range.Start;
                        var records = new Utf8RecordReader(stream, range.Start, range.End);
                        if (range.Start > 0)
                        {
                            // Discard a leading fragment; the next record starts at an exact UTF-8 line boundary.
                            stream.Position = range.Start - 1; var previous = stream.ReadByte(); stream.Position = range.Start;
                            if (previous != '\n') await records.ReadAsync(token);
                            info = info with { State = SessionState.Unknown, ActiveTurnId = null };
                        }
                        while (await records.ReadAsync(token) is { } record)
                        {
                            var line = record.Text;
                            if (record.Offset == 0)
                            {
                                try { using var meta = JsonDocument.Parse(line); if (meta.RootElement.Str("type") == "session_meta") info = TranscriptParser.Metadata(info, meta.RootElement.Field("payload")); }
                                catch (JsonException) { }
                            }
                        if (!line.Contains("task_started", StringComparison.Ordinal) && !line.Contains("task_complete", StringComparison.Ordinal)
                            && !line.Contains("turn_started", StringComparison.Ordinal) && !line.Contains("turn_complete", StringComparison.Ordinal)
                            && !line.Contains("turn_aborted", StringComparison.Ordinal) && !line.Contains("task_interrupted", StringComparison.Ordinal)
                            && !line.Contains("turn_context", StringComparison.Ordinal) && !(info.Title.Length == 0 && line.Contains("response_item", StringComparison.Ordinal) && line.Contains("user", StringComparison.Ordinal))) continue;
                        try
                        {
                            using var d = JsonDocument.Parse(line); var r = d.RootElement; var p = r.Field("payload");
                            if (info.Title.Length == 0 && r.Str("type") == "response_item" && p.Str("type") == "message" && p.Str("role") == "user")
                            {
                                var message = string.Join(" ", p.Field("content").Items().Select(x => x.Str("text"))).Trim();
                                if (message.Length > 0 && !message.StartsWith('<')) info = info with { Title = message[..Math.Min(180, message.Length)].Replace('\n', ' ').Replace('\r', ' ') };
                            }
                            if (r.Str("type") == "turn_context") info = info with { Model = p.Str("model", info.Model) };
                            if (r.Str("type") != "event_msg") continue;
                            info = p.Str("type") switch
                            {
                                "task_started" or "turn_started" => info with { State = SessionState.RecordedActive, ActiveTurnId = p.Str("turn_id") },
                                "task_complete" or "turn_complete" => info with { State = SessionState.Completed, ActiveTurnId = null },
                                "turn_aborted" or "task_interrupted" => info with { State = SessionState.Interrupted, ActiveTurnId = null },
                                _ => info
                            };
                        }
                        catch (JsonException) { }
                        }
                    }
                    _cache[path] = (file.Length, file.LastWriteTimeUtc, info);
                }
                if (titles.TryGetValue(info.Id, out var title) && !string.IsNullOrWhiteSpace(title)) info = info with { Title = title };
                result.Add(info);
            }
            catch (IOException) { } // A rollout can be moved to the archive during enumeration.
            catch (UnauthorizedAccessException) { }
        });
        return result.OrderByDescending(x => x.UpdatedAt).ToArray();
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex IdPattern();
}
