using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class SessionCatalogTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T04:00:00Z");
    private static SessionInfo[] Sessions(int count) => Enumerable.Range(0, count).Select(i => new SessionInfo
    { Id = $"session-{i:D6}", Title = $"Conversation {i}", UpdatedAt = Now.AddMinutes(-i), CreatedAt = Now.AddDays(-i), State = SessionState.Completed }).ToArray();
    [TestMethod]
    public void HydratingRuntimeMetadataCannotPromoteContentOrMarkItUnread()
    {
        var local = Sessions(1)[0] with { FilePath = "local.jsonl", FileVersion = new(200, Now.UtcDateTime), ContentRevision = "content" };
        var server = local with { UpdatedAt = Now.AddDays(1), CreatedAt = Now.AddDays(1), FileVersion = null, ContentRevision = null, State = SessionState.Active, IsLive = true };
        var merged = SessionMetadata.Merge(local, server);
        Assert.AreEqual(local.UpdatedAt, merged.UpdatedAt); Assert.AreEqual(local.CreatedAt, merged.CreatedAt); Assert.IsFalse(SessionMetadata.ContentChanged(local, merged)); Assert.IsTrue(merged.IsLive);
        Assert.IsFalse(SessionMetadata.ContentChanged(local, local with { FileVersion = new(200, Now.AddDays(1).UtcDateTime) }));
        Assert.IsTrue(SessionMetadata.ContentChanged(local, local with { ContentRevision = "new-content" }));
    }
    [TestMethod]
    public async Task TenThousandServerSessionsReadOnePageThenContinueWithoutDrainingHistory()
    {
        var all = Sessions(10000); var calls = new List<(bool Archived, string? Cursor)>();
        Task<SessionPage> Read(bool archived, string? cursor, CancellationToken ct)
        {
            calls.Add((archived, cursor)); var start = cursor is null ? 0 : int.Parse(cursor);
            return Task.FromResult(new SessionPage(archived ? [] : all.Skip(start).Take(50).ToArray(), !archived && start + 50 < all.Length ? (start + 50).ToString() : null));
        }
        var catalog = new SessionCatalog([], Read); var first = await catalog.ReadAsync(new(), 50, false, TimeZoneInfo.Utc);
        Assert.HasCount(50, first.Visible); Assert.IsTrue(first.HasMore); Assert.HasCount(2, calls);
        var second = await catalog.ReadAsync(new(), 100, false, TimeZoneInfo.Utc); Assert.HasCount(100, second.Visible); Assert.HasCount(3, calls);
        Assert.AreEqual("50", calls[2].Cursor); Assert.AreEqual(100, second.Visible.Select(s => s.Id).Distinct().Count());
    }
    [TestMethod]
    public async Task RefreshingAnEntirelyNewFirstPageCannotSkipUnseenConversations()
    {
        var all = Sessions(250);
        var catalog = new SessionCatalog([], (archived, cursor, _) =>
        {
            var offset = cursor is null ? 0 : int.Parse(cursor);
            return Task.FromResult(new SessionPage(archived ? [] : all.Skip(offset).Take(50).ToArray(), archived || offset + 50 >= all.Length ? null : (offset + 50).ToString()));
        });
        await catalog.ReadAsync(new(), 100, false, TimeZoneInfo.Utc);
        all = Enumerable.Range(0, 80).Select(i => new SessionInfo { Id = "new-" + i, Title = "New " + i, UpdatedAt = Now.AddDays(1).AddMinutes(-i) }).Concat(all).ToArray();
        var found = await catalog.ReadAsync(new("New 75"), 50, true, TimeZoneInfo.Utc);
        Assert.HasCount(1, found.Visible); Assert.AreEqual("new-75", found.Visible[0].Id);
    }
    [TestMethod]
    public async Task EqualTimestampBoundariesDoNotEagerlyLoadThousandsOfPages()
    {
        var calls = 0; var catalog = new SessionCatalog([], (archived, cursor, _) =>
        { calls++; return Task.FromResult(new SessionPage(archived ? [] : Sessions(50).Select(s => s with { UpdatedAt = Now }).ToArray(), archived ? null : "next")); });
        var first = await catalog.ReadAsync(new(), 50, false, TimeZoneInfo.Utc); Assert.HasCount(50, first.Visible); Assert.AreEqual(2, calls);
    }
    [TestMethod]
    public async Task FilteringCanFindUnloadedHistoryAndDateBoundsStopAnUnnecessaryScan()
    {
        var all = Sessions(250); var calls = 0;
        var catalog = new SessionCatalog([], (archived, cursor, _) =>
        {
            calls++; var offset = cursor is null ? 0 : int.Parse(cursor);
            return Task.FromResult(new SessionPage(archived ? [] : all.Skip(offset).Take(50).ToArray(), archived || offset >= 200 ? null : (offset + 50).ToString()));
        });
        var filtered = await catalog.ReadAsync(new("Conversation 249"), 50, false, TimeZoneInfo.Utc);
        Assert.HasCount(1, filtered.Visible); Assert.AreEqual("session-000249", filtered.Visible[0].Id); Assert.AreEqual(6, calls);
        var boundedCalls = 0; var bounded = new SessionCatalog([], (a, c, _) => { boundedCalls++; return Task.FromResult(new SessionPage(a ? [] : Sessions(50).Select(s => s with { UpdatedAt = Now.AddYears(-1) }).ToArray(), a ? null : "older")); });
        var none = await bounded.ReadAsync(new(From: new(2026, 11, 1)), 50, false, TimeZoneInfo.Utc); Assert.HasCount(0, none.Visible); Assert.AreEqual(2, boundedCalls); Assert.IsFalse(none.HasMore);
    }
    [TestMethod]
    public async Task CancelledUncooperativeReadsDoNotBlockANewQueryOrCommitAnOldCursor()
    {
        var late = new TaskCompletionSource<SessionPage>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var catalog = new SessionCatalog([], (a, c, _) => ++calls == 1 ? late.Task : Task.FromResult(new SessionPage(a ? [] : Sessions(2), null)));
        using var cancellation = new CancellationTokenSource(); var old = catalog.ReadAsync(new("old"), 50, false, TimeZoneInfo.Utc, cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => old);
        var current = await catalog.ReadAsync(new(), 50, false, TimeZoneInfo.Utc).WaitAsync(TimeSpan.FromSeconds(3)); Assert.HasCount(2, current.Visible);
        late.SetResult(new([new() { Id = "stale", Title = "Stale response" }], "stale-cursor"));
        var after = await catalog.ReadAsync(new(), 50, false, TimeZoneInfo.Utc); Assert.IsFalse(after.Known.Any(s => s.Id == "stale"));
    }
    [TestMethod]
    public void DateSearchCombinesKeywordsAndScopesAndUsesLocalCalendarDays()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-eight", TimeSpan.FromHours(8), "test", "test");
        var session = Sessions(1)[0] with { Title = "Layout", UpdatedAt = DateTimeOffset.Parse("2026-11-03T16:00:00Z"), CreatedAt = DateTimeOffset.Parse("2026-10-01T23:59:00Z") };
        Assert.IsTrue(new SessionQuery("layout", From: new(2026, 11, 4), Through: new(2026, 11, 4)).Matches(session, zone));
        Assert.IsFalse(new SessionQuery(Through: new(2026, 11, 3)).Matches(session, zone));
        Assert.IsTrue(new SessionQuery(DateField: SessionDateField.Created, From: new(2026, 10, 2), Through: new(2026, 10, 2)).Matches(session, zone));
        Assert.IsFalse(new SessionQuery("other").Matches(session, zone)); Assert.IsFalse(new SessionQuery(Scope: SessionScope.Active).Matches(session, zone));
        Assert.IsFalse(new SessionQuery(From: new(2026, 11, 5), Through: new(2026, 11, 4)).IsValid);
    }
    [TestMethod]
    public async Task LocalMetadataUsesConversationTimeAndIgnoresMetadataOnlyWritesAcrossRestarts()
    {
        var home = Path.Combine(Path.GetTempPath(), "CodexMonitor-catalog-" + Guid.NewGuid()); Directory.CreateDirectory(Path.Combine(home, "sessions"));
        try
        {
            var path = Path.Combine(home, "sessions", "a.jsonl"); var cache = Path.Combine(home, "cache.json");
            var record = JsonSerializer.Serialize(new { timestamp = Now, type = "response_item", payload = new { type = "message", role = "assistant", content = new[] { new { text = "Hello" } } } });
            await File.WriteAllTextAsync(path, record + "\n"); var original = (await new LocalSessionStore(home, cache).ListAsync()).Single();
            await File.AppendAllTextAsync(path, "{\"timestamp\":\"2027-01-01T00:00:00Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"model\"}}\n");
            var hydrated = (await new LocalSessionStore(home, cache).ListAsync()).Single();
            Assert.AreEqual(Now, hydrated.UpdatedAt); Assert.AreEqual(original.ContentRevision, hydrated.ContentRevision); Assert.IsFalse(SessionMetadata.ContentChanged(original, hydrated));
            await File.AppendAllTextAsync(path, record.Replace("Hello", "New content").Replace(Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), Now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:sszzz")) + "\n");
            var updated = (await new LocalSessionStore(home, cache).ListAsync()).Single(); Assert.IsTrue(SessionMetadata.ContentChanged(hydrated, updated));
            var cached = (await new LocalSessionStore(home, cache).ListAsync()).Single(); Assert.AreEqual(updated, cached, "Persisted cache must preserve content timestamps and revisions");
        }
        finally { Directory.Delete(home, true); }
    }
}
