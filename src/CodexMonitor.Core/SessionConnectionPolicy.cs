namespace CodexMonitor.Core;

public static class SessionConnectionPolicy
{
    // Loaded runtimes are joined; an unconfirmed active record must never be restored elsewhere.
    public static bool CanResume(SessionInfo session) => !session.IsArchived && !session.IsLive &&
        session.State is SessionState.Idle or SessionState.Completed or SessionState.Interrupted or SessionState.Error;
}
