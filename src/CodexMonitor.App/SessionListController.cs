using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

public sealed partial class MainWindow
{
    private SessionCatalog? _sessionCatalog;
    private CodexClient? _sessionCatalogClient;
    private CodexClient? _sessionEventClient;
    private bool _sessionCatalogBaseline = true;
    private SessionQuery _sessionQuery = new();
    private CancellationTokenSource? _sessionQueryCancellation;
    private long _sessionQueryGeneration;
    private int _sessionLimit = SessionCatalog.PageSize;
    private bool _sessionHasMore, _sessionQueryLoading;
    private bool _resetSessionViewport;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sessionReorderTimer;
    private readonly Dictionary<string, SessionInfo> _sessionEventMetadata = [];
    private IReadOnlyDictionary<string, SessionInfo> _sessionMetadataIndex = new Dictionary<string, SessionInfo>();
    private ScrollViewer? _sessionScroll;
    private long _sessionPageIntent, _sessionConsumedPageIntent;
    private double _sessionPreviousOffset;
    private SessionDateField _sessionDateField;
    private DateOnly? _sessionDateFrom, _sessionDateThrough;
    private Flyout? _sessionDateFlyout;
    private HashSet<string>? _knownLocalSessionIds;
    private IReadOnlyList<SessionInfo> _localSessionSnapshot = Array.Empty<SessionInfo>();
    private readonly HashSet<string> _unreadSessionIds = [];
    private readonly DateTimeOffset _sessionStartup = DateTimeOffset.UtcNow;
    private SessionQuery CurrentSessionQuery() => new(SessionSearch.Text, (SessionScope)Math.Max(0, SessionFilter.SelectedIndex), _sessionDateField, _sessionDateFrom, _sessionDateThrough);
    private void ResetSessionSource()
    {
        _sessionQueryCancellation?.Cancel(); ++_sessionQueryGeneration; _sessionQueryCancellation = null;
        _sessionCatalogClient = null; _sessionEventClient = null; _sessionEventMetadata.Clear(); _sessionCatalogBaseline = true;
        _sessionCatalog = _reviewMode ? null : new(_localSessionSnapshot); _sessionHasMore = false; SetSessionLoading(false);
        if (_sessionCatalog is not null) _ = ReloadSessionQueryAsync();
    }
    private void SetSessionLoading(bool loading)
    {
        _sessionQueryLoading = loading; SessionLoadingRing.IsActive = loading;
        SessionLoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        NoSessionsPanel.Visibility = !loading && _visibleSessionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task<bool> ReloadSessionQueryAsync(bool refresh = false, bool reset = false, bool debounce = false)
    {
        if (_sessionCatalog is not { } catalog || _closing) return false;
        var query = CurrentSessionQuery();
        if (reset || query != _sessionQuery) { _sessionLimit = SessionCatalog.PageSize; _resetSessionViewport = true; }
        _sessionQuery = query;
        var retainedIds = !_resetSessionViewport && SessionList.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0 } stack
            ? _visibleSessionRows.Skip(stack.FirstVisibleIndex).Take(Math.Max(1, stack.LastVisibleIndex - stack.FirstVisibleIndex + 1)).Select(row => row.Session.Id).ToArray() : [];
        _sessionQueryCancellation?.Cancel();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sessionQueryCancellation = cancel; var generation = ++_sessionQueryGeneration; var limit = _sessionLimit;
        if (reset || debounce || refresh && _visibleSessionRows.Count == 0) SetSessionLoading(true);
        try
        {
            if (debounce) await Task.Delay(180, cancel.Token);
            var listing = await Task.Run(() => catalog.ReadAsync(query, limit, refresh, TimeZoneInfo.Local, cancel.Token), cancel.Token);
            if (_closing || generation != _sessionQueryGeneration || !ReferenceEquals(catalog, _sessionCatalog) || cancel.IsCancellationRequested) return false;
            _sessions = listing.Known; _sessionMetadataIndex = listing.Index; _sessionHasMore = listing.HasMore;
            var retained = retainedIds.Where(listing.Index.ContainsKey).Select(id => listing.Index[id]).Where(s => query.Matches(s, TimeZoneInfo.Local));
            var display = listing.Visible.Concat(retained).DistinctBy(s => s.Id).OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
            ApplySessionRows(display, false, !_sessionCatalogBaseline); _sessionCatalogBaseline = false;
            if (_resetSessionViewport) { _sessionScroll?.ChangeView(null, 0, null, true); _resetSessionViewport = false; }
            SessionCount.Text = _text.Format(listing.RemoteIncomplete ? "SessionCountAtLeast" : "SessionCount", _text.Number(listing.MatchingCount));
            SetSessionLoading(false); TrackSessionListScroll();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { SessionList.UpdateLayout(); RequestMoreSessions(); });
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is IOException or RpcException or UnauthorizedAccessException)
        { if (generation == _sessionQueryGeneration && !cancel.IsCancellationRequested) NoticeText("ReadFailed", InfoBarSeverity.Warning); return false; }
        finally { if (ReferenceEquals(_sessionQueryCancellation, cancel)) { SetSessionLoading(false); _sessionQueryCancellation = null; } }
    }
    private void ScheduleSessionQuery(bool reset = false, bool debounce = false)
    {
        if (!_ready) return;
        if (_sessionCatalog is null) { ApplyFilter(); return; }
        ++_sessionAnchorInputVersion; _pendingSessionAnchor = null;
        _ = ReloadSessionQueryAsync(reset: reset, debounce: debounce);
    }
    private void TrackSessionListScroll()
    {
        var scroll = ScrollAnchor.FindScroll(SessionList); if (ReferenceEquals(scroll, _sessionScroll)) return;
        if (_sessionScroll is not null) _sessionScroll.ViewChanged -= OnSessionListViewChanged;
        _sessionScroll = scroll; if (scroll is not null) scroll.ViewChanged += OnSessionListViewChanged;
    }
    private void OnSessionListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) { ++_sessionAnchorInputVersion; _pendingSessionAnchor = null; if (_sessionScroll!.VerticalOffset > _sessionPreviousOffset) ++_sessionPageIntent; }
        _sessionPreviousOffset = _sessionScroll!.VerticalOffset; RequestMoreSessions();
    }
    private void RequestMoreSessions()
    {
        if (_closing || _sessionQueryLoading || _sessionQueryCancellation is not null || !_sessionHasMore || _sessionCatalog is null || _sessionScroll is null ||
            _sessionScroll.ScrollableHeight - _sessionScroll.VerticalOffset > Math.Max(200, _sessionScroll.ViewportHeight) ||
            _sessionPageIntent <= _sessionConsumedPageIntent && _sessionScroll.ScrollableHeight > 1) return;
        _sessionConsumedPageIntent = _sessionPageIntent;
        _sessionLimit += SessionCatalog.PageSize; SetSessionLoading(true); _ = ReloadSessionQueryAsync();
    }
    private void QueueSessionReorder()
    {
        if (_sessionReorderTimer is null)
        {
            _sessionReorderTimer = DispatcherQueue.CreateTimer(); _sessionReorderTimer.Interval = TimeSpan.FromMilliseconds(250);
            _sessionReorderTimer.Tick += (_, _) => { if (_sessionQueryCancellation is not null) return; _sessionReorderTimer.Stop(); if (!_closing) ApplyFilter(); };
        }
        if (!_sessionReorderTimer.IsRunning) _sessionReorderTimer.Start();
    }
    private void OnSessionDates(object sender, RoutedEventArgs args)
    {
        var panel = new DateFilterPanel(_text, _sessionDateField, _sessionDateFrom, _sessionDateThrough) { RequestedTheme = Root.RequestedTheme, FlowDirection = Root.FlowDirection, Language = Root.Language };
        _sessionDateFlyout?.Hide();
        var flyout = _sessionDateFlyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(200, Root.ActualHeight - 160), HorizontalScrollMode = ScrollMode.Disabled } };
        flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter));
        flyout.FlyoutPresenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, Root.RequestedTheme));
        flyout.FlyoutPresenterStyle.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, Root.FlowDirection));
        panel.Applied += (field, from, through) =>
        { _sessionDateField = field; _sessionDateFrom = from; _sessionDateThrough = through; UpdateDateFilterChrome(); flyout.Hide(); ScheduleSessionQuery(true); };
        flyout.ShowAt(DateFilterButton);
    }
    private void UpdateDateFilterChrome()
    {
        var active = _sessionDateFrom is not null || _sessionDateThrough is not null;
        var range = active ? _text[_sessionDateField == SessionDateField.Created ? "CreatedTime" : "UpdatedTime"] + ": " +
            (_sessionDateFrom?.ToString("d", _text.Culture) ?? "…") + " – " + (_sessionDateThrough?.ToString("d", _text.Culture) ?? "…") : _text["AnyTime"];
        AutomationProperties.SetName(DateFilterButton, _text["FilterDates"]); AutomationProperties.SetItemStatus(DateFilterButton, range);
        AutomationProperties.SetHelpText(DateFilterButton, range); ToolTipService.SetToolTip(DateFilterButton, _text["FilterDates"] + " · " + range);
        DateFilterGlyph.SetResourceReference(FontIcon.ForegroundProperty, active ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        AutomationProperties.SetName(SessionLoadingRing, _text["LoadingSessions"]);
    }
}
