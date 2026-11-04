using Microsoft.Data.Sqlite;

namespace CodexMonitor.Core;

/// <summary>Optional read-only metadata accelerator. Only discovered rollout files become rows.</summary>
internal static class LocalSessionDatabase
{
    private static readonly Lazy<bool> Ready = new(() => { SQLitePCL.Batteries_V2.Init(); return true; });
    internal static IReadOnlyDictionary<string, SessionInfo> Read(string home, CancellationToken ct)
    {
        var result = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            // Use Windows' SQLite, without shipping a second native database engine.
            _ = Ready.Value;
            foreach (var path in Directory.EnumerateFiles(home, "state_*.sqlite").OrderByDescending(p =>
                int.TryParse(Path.GetFileNameWithoutExtension(p).AsSpan(6), out var n) ? n : -1))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
                    db.Open();
                    using var schema = db.CreateCommand(); schema.CommandText = "PRAGMA table_info(threads)";
                    var columns = new HashSet<string>(StringComparer.Ordinal);
                    using (var reader = schema.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
                    if (!columns.Contains("id")) continue;
                    string Column(string name, string fallback = "''") => columns.Contains(name) ? '"' + name + '"' : fallback;
                    string Time(string name) => columns.Contains(name + "_ms") ? $"COALESCE({Column(name + "_ms")}, {Column(name, "0")} * 1000)" : Column(name, "0") + " * 1000";
                    using var command = db.CreateCommand();
                    command.CommandText = $"SELECT id, {Column("name")}, {Column("title")}, {Column("cwd")}, {Column("source")}, {Column("model")}, {Time("created_at")}, {Time("updated_at")}, {Column("archived", "0")}, {Column("attached_thread_parent_id")} FROM threads";
                    using var rows = command.ExecuteReader();
                    string Text(int i) => rows.IsDBNull(i) ? "" : rows.GetString(i);
                    DateTimeOffset Date(int i) => rows.IsDBNull(i) || rows.GetInt64(i) is < -62135596800000 or > 253402300799999 ? default : DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(i));
                    while (rows.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        var id = Text(0); if (id.Length == 0) continue;
                        result[id] = new() { Id = id, Title = string.IsNullOrWhiteSpace(Text(1)) ? Text(2) : Text(1), Cwd = Text(3), Source = Text(4), Model = Text(5), CreatedAt = Date(6), UpdatedAt = Date(7), IsArchived = !rows.IsDBNull(8) && rows.GetInt64(8) != 0, ParentId = Text(9) is { Length: > 0 } parent ? parent : null };
                    }
                    return result;
                }
                catch (Exception e) when (e is SqliteException or InvalidCastException or FormatException) { result.Clear(); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or TypeInitializationException) { }
        return result;
    }
}
