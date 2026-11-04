using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZstdSharp;
using CodexMonitor.Core;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class CompressedRolloutTests
{
    private string _home = null!;
    private const string Id = "12345678-1234-1234-1234-123456789abc";
    private string PathFor(string name = Id) => Path.Combine(_home, "sessions", "rollout-" + name + ".jsonl");
    [TestInitialize] public void Init() { _home = Path.Combine(Path.GetTempPath(), "CodexMonitor-zstd-tests-" + Guid.NewGuid()); Directory.CreateDirectory(Path.Combine(_home, "sessions")); }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(_home, true); }
    private static string Transcript(int records = 3) => "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + Id + "\",\"timestamp\":\"2026-09-05T03:00:00Z\",\"cwd\":\"C:\\\\Example\"}}\n" +
        string.Concat(Enumerable.Range(0, records).Select(i => "{\"timestamp\":\"2026-09-05T03:00:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"" + (i == 0 ? "user" : "assistant") + "\",\"content\":[{\"text\":\"Message " + i + "\"}]}}\n"));
    private static async Task Compress(string path, string text)
    { await using var output = File.Create(path + ".zst"); await using var encoder = new CompressionStream(output); await encoder.WriteAsync(Encoding.UTF8.GetBytes(text)); }
    [TestMethod]
    public async Task CompressedHistoryIsListedAndReadWithoutChangingTheCodexFiles()
    {
        var path = PathFor(); await Compress(path, Transcript()); var before = SHA256.HashData(await File.ReadAllBytesAsync(path + ".zst"));
        var session = (await new LocalSessionStore(_home).ListAsync()).Single(); Assert.AreEqual(Id, session.Id); Assert.AreEqual("Message 0", session.Title);
        var snapshot = await new TranscriptParser().ReadAsync(session); Assert.HasCount(3, snapshot.Entries);
        Assert.AreEqual("Message 2", snapshot.Entries[^1].Text); Assert.IsFalse(File.Exists(path));
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(path + ".zst")));
    }
    [TestMethod]
    public async Task PlainAndCompressedSiblingsAreOneConversationAndRepresentationChangesAreNotUnread()
    {
        var path = PathFor(); await File.WriteAllTextAsync(path, Transcript()); await Compress(path, Transcript());
        var duplicate = Path.Combine(_home, "sessions", "copy-" + Id + ".jsonl"); await Compress(duplicate, Transcript()); File.SetLastWriteTimeUtc(duplicate + ".zst", DateTime.UtcNow.AddDays(1));
        var store = new LocalSessionStore(_home); var plain = (await store.ListAsync()).Single(); Assert.AreEqual(path, plain.FilePath);
        File.Delete(path); var compressed = (await store.ListAsync()).Single(); Assert.IsTrue(RolloutFiles.IsCompressed(compressed.FilePath)); Assert.IsFalse(SessionMetadata.ContentChanged(plain, compressed));
        Assert.IsTrue(SessionMetadata.ContentChanged(compressed, plain with { UpdatedAt = compressed.UpdatedAt.AddMinutes(1), ContentRevision = "new-message" }));
        Assert.AreEqual(plain.UpdatedAt, compressed.UpdatedAt); Assert.HasCount(3, (await new TranscriptParser().ReadAsync(plain)).Entries);
        await File.WriteAllTextAsync(path, Transcript()); Assert.HasCount(3, (await new TranscriptParser().ReadAsync(compressed)).Entries);
    }
    [TestMethod]
    public async Task LargeCompressedHistoryKeepsOriginalByteIdsAndAutomaticPageBoundaries()
    {
        var path = PathFor(); var text = Transcript(100000); await Compress(path, text); await File.WriteAllTextAsync(path, text);
        var session = new SessionInfo { Id = Id, FilePath = path };
        var plain = await new LocalTranscriptPager().ReadAsync(session); File.Delete(path);
        var compressed = await new LocalTranscriptPager().ReadAsync(session);
        Assert.IsTrue(compressed.HasEarlier); Assert.IsLessThanOrEqualTo(LocalTranscriptPager.PageRecords, compressed.Snapshot.Entries.Count);
        CollectionAssert.AreEqual(plain.Snapshot.Entries.Select(e => e.Id).ToArray(), compressed.Snapshot.Entries.Select(e => e.Id).ToArray());
        var older = await new LocalTranscriptPager().ReadAsync(session, compressed.StartOffset, true); Assert.IsLessThan(compressed.StartOffset, older.StartOffset); Assert.IsTrue(older.Snapshot.Entries.Count > compressed.Snapshot.Entries.Count);
    }
    [TestMethod]
    public async Task StateIndexProvidesCompressedMetadataWithoutDecodingTheEntireHistory()
    {
        var path = PathFor(); await Compress(path, "{}"); SQLitePCL.Batteries_V2.Init();
        var dbPath = Path.Combine(_home, "state_5.sqlite");
        using (var db = new SqliteConnection("Data Source=" + dbPath + ";Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE threads (id TEXT, title TEXT, cwd TEXT, created_at INTEGER, updated_at INTEGER); INSERT INTO threads VALUES ($id, 'Indexed title', 'C:/IndexedProject', 1700000000, 1700000100)"; command.Parameters.AddWithValue("$id", Id); command.ExecuteNonQuery();
        }
        var before = SHA256.HashData(await File.ReadAllBytesAsync(dbPath)); var row = (await new LocalSessionStore(_home).ListAsync()).Single();
        Assert.AreEqual("Indexed title", row.Title); Assert.AreEqual("C:/IndexedProject", row.Cwd);
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(dbPath)));
    }
    [TestMethod]
    public async Task CorruptCompressedContentAndCancellationNeverBecomeAnEmptySuccessfulTranscript()
    {
        var path = PathFor(); await File.WriteAllTextAsync(path + ".zst", "invalid frame");
        await Assert.ThrowsAsync<IOException>(() => new TranscriptParser().ReadAsync(new() { Id = Id, FilePath = path }));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new LocalTranscriptPager().ReadAsync(new() { Id = Id, FilePath = path }, ct: cancel.Token));
    }
}
