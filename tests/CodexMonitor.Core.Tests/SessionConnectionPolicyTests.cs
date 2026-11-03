using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class SessionConnectionPolicyTests
{
    [TestMethod]
    [DataRow(SessionState.Completed, false, false, true)]
    [DataRow(SessionState.Completed, true, false, false)]
    [DataRow(SessionState.RecordedActive, false, false, false)]
    [DataRow(SessionState.Active, false, false, false)]
    [DataRow(SessionState.Waiting, false, false, false)]
    [DataRow(SessionState.Unknown, false, false, false)]
    [DataRow(SessionState.Idle, false, false, true)]
    [DataRow(SessionState.Error, false, false, true)]
    [DataRow(SessionState.Interrupted, false, false, true)]
    [DataRow(SessionState.Completed, false, true, false)]
    public void OnlyConfirmedInactiveUnloadedHistoryCanBeRestored(SessionState state, bool loaded, bool archived, bool expected) =>
        Assert.AreEqual(expected, SessionConnectionPolicy.CanResume(new() { Id = "thread", State = state, IsLive = loaded, IsArchived = archived }));
}
