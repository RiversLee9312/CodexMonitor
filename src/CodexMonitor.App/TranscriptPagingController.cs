using Microsoft.UI.Dispatching;

namespace CodexMonitor.App;

public sealed partial class MainWindow
{
    private bool _earlierRequested, _earlierCheckQueued, _historyAwaitingLayout;
    private int _hiddenHistoryPageBudget;
    private void CancelEarlierIntent() { _earlierRequested = false; _hiddenHistoryPageBudget = 0; }
    private void QueueEarlierCheck()
    {
        if (_closing || _earlierCheckQueued || !_earlierRequested) return;
        _earlierCheckQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _earlierCheckQueued = false;
            if (_closing || _loadingEarlier || _serverRead is not null || _snapshot is null) return;
            // At offset zero a wheel produces no ViewChanged or new layout event. Settle
            // outstanding work here instead of making input wait for an event that may never arrive.
            if (_historyAwaitingLayout) { MessageList.UpdateLayout(); _historyAwaitingLayout = false; }
            if (MessageList.IsRestoringPosition) return;
            if (!_hasEarlier) { _earlierRequested = false; return; }
            // Wheel/pan input is delivered before its animated viewport reaches the boundary.
            // Preserve that intent until a later view change, opposite input, or source change.
            if (!AtEarlierBoundary()) return;
            if (!_earlierRequested) return;
            _earlierRequested = false; _following = false; _jumpingLatest = false; ++_latestScrollGeneration;
            _ = LoadEarlierAsync();
        });
    }
    private bool AtEarlierBoundary()
    {
        if (_messageScroll is { } scroll && scroll.VerticalOffset > Math.Max(200, scroll.ViewportHeight * 2)) return false;
        // Restoring a nested anchor isn't a new upward gesture. Do not drain history just
        // because a capped tool group leaves the outer viewport at zero.
        if (_messages.FirstOrDefault() is { IsActivityGroup: true, IsGroupCollapsed: false } &&
            MessageList.ContainerFromIndex(0) is Microsoft.UI.Xaml.DependencyObject first && FindVisual<ActivityGroupView>(first) is { } group &&
            ScrollAnchor.FindScroll(group.List) is { VerticalOffset: > 160 }) return false;
        return true;
    }
}
