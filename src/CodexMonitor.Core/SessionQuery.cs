namespace CodexMonitor.Core;

public enum SessionDateField { Updated, Created }
public enum SessionScope { All, Active, Archived }
public sealed record SessionQuery(string Text = "", SessionScope Scope = SessionScope.All, SessionDateField DateField = SessionDateField.Updated,
    DateOnly? From = null, DateOnly? Through = null)
{
    public bool IsValid => From is null || Through is null || From <= Through;
    public bool Matches(SessionInfo s, TimeZoneInfo zone)
    {
        if (!IsValid || Scope == SessionScope.Archived && !s.IsArchived || Scope == SessionScope.Active &&
            (s.IsArchived || s.State is not (SessionState.Active or SessionState.RecordedActive or SessionState.Waiting))) return false;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateField == SessionDateField.Created ? s.CreatedAt : s.UpdatedAt, zone).DateTime);
        if (From is { } from && date < from || Through is { } through && date > through) return false;
        return string.IsNullOrWhiteSpace(Text) || $"{s.Title} {s.Cwd} {s.Id} {s.ParentId} {s.Source}".Contains(Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }
}
public sealed record SessionPage(IReadOnlyList<SessionInfo> Sessions, string? NextCursor);
public sealed record SessionListing(IReadOnlyList<SessionInfo> Visible, IReadOnlyList<SessionInfo> Known, int MatchingCount, bool HasMore, bool RemoteIncomplete)
{ public IReadOnlyDictionary<string, SessionInfo> Index { get; init; } = new Dictionary<string, SessionInfo>(); }

/// <summary>Canonical metadata merge. Transport hydration does not change content time or its revision.</summary>
public static class SessionMetadata
{
    public static SessionInfo Merge(SessionInfo disk, SessionInfo server) => server with
    {
        FilePath = disk.FilePath, FileVersion = disk.FileVersion, ContentRevision = disk.ContentRevision,
        CreatedAt = disk.CreatedAt, UpdatedAt = disk.UpdatedAt,
        Title = string.IsNullOrWhiteSpace(server.Title) ? disk.Title : server.Title,
        ParentId = server.ParentId ?? disk.ParentId, Model = server.Model.Length > 0 ? server.Model : disk.Model,
        State = server.State == SessionState.Unknown ? disk.State : server.State
    };
    public static bool ContentChanged(SessionInfo before, SessionInfo after)
    {
        if (before.ContentRevision is { } a && after.ContentRevision is { } b)
            return a.StartsWith("server:", StringComparison.Ordinal) == b.StartsWith("server:", StringComparison.Ordinal) && a != b;
        // Old/custom files without semantic records: size changes are useful; mtime or source-clock changes aren't.
        return before.FileVersion is { } old && after.FileVersion is { } current && old.Length != current.Length;
    }
}
