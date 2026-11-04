using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexMonitor.Core;

public sealed partial class LocalSessionStore(string home, string? cachePath = null)
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, SessionInfo Info)> _cache = new();
    private readonly SemaphoreSlim _scan = new(1);
    private bool _loadedCache;
    public string Home { get; } = Path.GetFullPath(home);
    public static string DefaultHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct = default)
    {
        await _scan.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        if (!_loadedCache)
        {
            _loadedCache = true;
            try
            {
                if (cachePath is not null && File.Exists(cachePath))
                {
                    await using var cachedStream = File.OpenRead(cachePath);
                    var saved = await JsonSerializer.DeserializeAsync(cachedStream, CoreJsonContext.Default.LocalSessionCache, ct).ConfigureAwait(false);
                    if (saved is { Version: 1 } && string.Equals(saved.Home, Home, StringComparison.OrdinalIgnoreCase))
                        foreach (var entry in saved.Entries) _cache[entry.Path] = (entry.Length, entry.Modified, entry.Session);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        var changed = 0;
        var database = LocalSessionDatabase.Read(Home, ct);
        var previous = _cache.Values.Select(v => v.Info).DistinctBy(s => s.Id).ToDictionary(s => s.Id, StringComparer.Ordinal);
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
            Directory.Exists(Path.Combine(Home, dir)) ? RolloutFiles.Enumerate(Path.Combine(Home, dir)) : []).ToArray();
        var paths = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in _cache.Keys) if (!paths.Contains(old)) { _cache.TryRemove(old, out _); changed = 1; }
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
                    info = new SessionInfo { Id = match.Success ? match.Value : Path.GetFileNameWithoutExtension(RolloutFiles.PlainPath(path)), FilePath = path,
                        UpdatedAt = file.LastWriteTimeUtc, CreatedAt = file.CreationTimeUtc, FileVersion = new(file.Length, file.LastWriteTimeUtc),
                        IsArchived = Path.GetRelativePath(Home, path).StartsWith("archived_sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) };
                    if (RolloutFiles.IsCompressed(path))
                    {
                        // Listing cold history must not decode hundreds of complete rollouts.
                        // Codex's local state DB supplies metadata; without it, read a bounded prefix.
                        var seed = previous.GetValueOrDefault(info.Id) ?? database.GetValueOrDefault(info.Id);
                        if (seed is not null) info = seed with { FilePath = path, FileVersion = info.FileVersion, IsLive = false, ActiveTurnId = null, UpdatedAt = previous.ContainsKey(info.Id) ? seed.UpdatedAt : file.LastWriteTimeUtc };
                        else
                        {
                            var bytes = await RolloutFiles.ReadPrefixAsync(path, 256 * 1024, token);
                            foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
                            {
                                try
                                {
                                    using var d = JsonDocument.Parse(line); var r = d.RootElement; var p = r.Field("payload");
                                    if (r.Str("type") == "session_meta") info = TranscriptParser.Metadata(info, p);
                                    if (info.Title.Length == 0 && r.Str("type") == "response_item" && p.Str("role") == "user")
                                    { var message = string.Join(" ", p.Field("content").Items().Select(x => x.Str("text"))).Trim(); info = info with { Title = message[..Math.Min(180, message.Length)].Replace('\n', ' ').Replace('\r', ' ') }; }
                                }
                                catch (JsonException) { }
                            }
                        }
                        info = info with { IsArchived = Path.GetRelativePath(Home, path).StartsWith("archived_sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), ContentRevision = seed?.ContentRevision ?? "zstd:" + file.LastWriteTimeUtc.Ticks };
                    }
                    else
                    {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                    // Metadata and recent lifecycle markers never require scanning a multi-gigabyte rollout.
                    const int headBytes = 64 * 1024, tailBytes = 256 * 1024;
                    var length = stream.Length;
                    var ranges = length <= headBytes + tailBytes ? new[] { (Start: 0L, End: length) } : new[] { (Start: 0L, End: (long)headBytes), (Start: length - tailBytes, End: length) };
                    DateTimeOffset contentTime = default; (string Text, long Offset)? lastContent = null;
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
                            try
                            {
                                using var d = JsonDocument.Parse(line); var r = d.RootElement; var p = r.Field("payload");
                                var type = r.Str("type"); var kind = p.Str("type");
                                var semantic = type == "response_item" && kind != "configuration_update" || type == "event_msg" && kind is
                                    "user_message" or "agent_message" or "agent_reasoning" or "agent_reasoning_delta" or "agent_message_delta" or
                                    "item_started" or "item_completed" or "task_started" or "task_complete" or "turn_started" or "turn_complete" or "turn_aborted" or "task_interrupted" ||
                                    type is "item.started" or "item.completed" or "agent_message";
                                if (semantic)
                                {
                                    var time = r.Date("timestamp"); if (time > contentTime) contentTime = time; lastContent = record;
                                }
                                if (info.Title.Length == 0 && type == "response_item" && kind == "message" && p.Str("role") == "user")
                                {
                                    var message = string.Join(" ", p.Field("content").Items().Select(x => x.Str("text"))).Trim();
                                    if (message.Length > 0 && !message.StartsWith('<')) info = info with { Title = message[..Math.Min(180, message.Length)].Replace('\n', ' ').Replace('\r', ' ') };
                                }
                                if (type == "turn_context") info = info with { Model = p.Str("model", info.Model) };
                                if (type != "event_msg") continue;
                                info = kind switch
                                {
                                    "task_started" or "turn_started" => info with { State = SessionState.RecordedActive, ActiveTurnId = p.Str("turn_id") },
                                    "task_complete" or "turn_complete" => info with { State = SessionState.Completed, ActiveTurnId = null },
                                    "turn_aborted" or "task_interrupted" => info with { State = SessionState.Interrupted, ActiveTurnId = null }, _ => info
                                };
                            }
                            catch (JsonException) { }
                        }
                    }
                    if (lastContent is { } last)
                    {
                        var signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(last.Text)));
                        info = info with { UpdatedAt = contentTime != default ? contentTime : info.UpdatedAt, ContentRevision = last.Offset + ":" + signature };
                    }
                    }
                    _cache[path] = (file.Length, file.LastWriteTimeUtc, info);
                    Interlocked.Exchange(ref changed, 1);
                }
                if (database.TryGetValue(info.Id, out var indexed)) info = info with { Title = indexed.Title.Length > 0 ? indexed.Title : info.Title, Cwd = info.Cwd.Length > 0 ? info.Cwd : indexed.Cwd, Model = info.Model.Length > 0 ? info.Model : indexed.Model, Source = info.Source.Length > 0 ? info.Source : indexed.Source, IsArchived = info.IsArchived || indexed.IsArchived, ParentId = info.ParentId ?? indexed.ParentId };
                if (titles.TryGetValue(info.Id, out var title) && !string.IsNullOrWhiteSpace(title)) info = info with { Title = title };
                result.Add(info);
            }
            catch (IOException) { } // A rollout can be moved to the archive during enumeration.
            catch (UnauthorizedAccessException) { }
        });
        if (changed != 0 && cachePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await using (var output = new FileStream(cachePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    await JsonSerializer.SerializeAsync(output, new LocalSessionCache(1, Home, _cache.Select(p => new LocalSessionCacheEntry(p.Key, p.Value.Length, p.Value.Modified, p.Value.Info)).ToArray()), CoreJsonContext.Default.LocalSessionCache, ct).ConfigureAwait(false);
                File.Move(cachePath + ".tmp", cachePath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result.OrderBy(x => x.IsArchived).ThenBy(x => RolloutFiles.IsCompressed(x.FilePath)).ThenByDescending(x => x.UpdatedAt).DistinctBy(x => x.Id).OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        }
        finally { _scan.Release(); }
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex IdPattern();
}
internal sealed record LocalSessionCache(int Version, string Home, LocalSessionCacheEntry[] Entries);
internal sealed record LocalSessionCacheEntry(string Path, long Length, DateTime Modified, SessionInfo Session);
