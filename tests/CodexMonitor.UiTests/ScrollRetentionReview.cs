using CodexMonitor.Core;

namespace CodexMonitor.App;

public sealed partial class MainWindow
{
    internal bool ReviewReadingWork => _readingActivityHistory;
    internal void ReviewUpdateEntries(IReadOnlyList<TranscriptEntry> entries)
    { _snapshot = new(_selected!, entries); RenderMessages(); }
    internal async Task ReviewUpdateCatalog(IReadOnlyList<SessionInfo> sessions, bool remote)
    {
        if (remote) foreach (var session in sessions) _sessionCatalog!.Observe(session);
        else _sessionCatalog!.SetLocal(sessions);
        await ReloadSessionQueryAsync();
    }
}
