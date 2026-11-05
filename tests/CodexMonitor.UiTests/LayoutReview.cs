using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CodexMonitor.App;

public sealed partial class MainWindow
{
    internal FrameworkElement ReviewRoot => Root;
    internal ElementTheme ReviewTheme => Root.ActualTheme;
    internal bool ReviewPaneOpaque => Shell.PaneBackground is SolidColorBrush brush && brush.Color.A == 255;
    internal bool ReviewIsOverlay => Shell.DisplayMode == SplitViewDisplayMode.Overlay;
    internal bool ReviewIsRtl => Root.FlowDirection == FlowDirection.RightToLeft;

    internal async Task SetReviewAsync(int width, int height, string theme, string language, bool openPane = false)
    {
        _sessionDateFlyout?.Hide(); InvalidateTranscriptRead(); SetLatestVisible(false); _restoringSessionAnchor = 0;
        _sessionQueryCancellation?.Cancel(); _sessionQueryCancellation = null; _sessionEventClient = null; _sessionCatalogBaseline = true; _localSessionSnapshot = Array.Empty<SessionInfo>(); _sessionPageIntent = _sessionConsumedPageIntent = 0; ++_sessionQueryGeneration; _sessionCatalog = null; _sessionCatalogClient = null; _sessionLimit = SessionCatalog.PageSize; _sessionHasMore = _sessionQueryLoading = _resetSessionViewport = false; _sessionReorderTimer?.Stop(); _sessionMetadataIndex = new Dictionary<string, SessionInfo>(); _knownLocalSessionIds = null; _sessionEventMetadata.Clear(); _unreadSessionIds.Clear(); _sessionDateField = SessionDateField.Updated; _sessionDateFrom = _sessionDateThrough = null;
        _syncing = true; SessionSearch.Text = ""; _syncing = false;
        _serverEvents.Clear(); _historySessionId = null; _historyClient = null; _historyStart = null; _historyCursor = null; _hasEarlier = _loadingEarlier = _pagedReview = false; _activityGroupIdentities.Clear();
        _readLocalTranscript = (session, ct) => new TranscriptParser().ReadAsync(session, ct);
        foreach (var item in _reviewFontOverrides) item.Element.SetValue(item.Property, item.Value);
        _reviewFontOverrides.Clear();
        if (_client is not null) { await _client.DisposeAsync(); _client = null; }
        _syncing = true;
        SessionList.Transitions = _noSessionTransitions;
        _visibleSessionRows.Clear(); _sessionRows.Clear(); _sessionOrder.Clear(); _sessionBaselineLoaded = false;
        _syncing = false;
        _settings = new AppSettings { CodexHome = "C:\\Users\\Example\\.codex", Theme = theme, Language = language };
        _store = new(_settings.CodexHome);
        _sessionAvailabilityCancellation?.Cancel(); _sessionServerCheckedAt.Clear();
        Notice.IsOpen = false; _attached.Clear(); _expandedActivities.Clear(); _activityScrollOffsets.Clear(); _sessionConnectionFailures.Clear(); _connectionEndpoint = null;
        _cliMissing = false; _readingActivityHistory = false; _homeDiscovery = new ReviewHomeDiscovery(); _resolveExecutable = _ => "C:\\codex.exe"; _checkResumeState = CheckResumeStateAsync;
        _text = new(language);
        var time = DateTimeOffset.Parse("2026-11-03T08:00:00Z");
        _selected = new SessionInfo { Id = "review-session", Title = "Review the transcript layout and keyboard interactions", Cwd = "D:\\Projects\\CodexMonitor", State = SessionState.Completed, CreatedAt = time, UpdatedAt = time, FileVersion = new(100, time.UtcDateTime) };
        _sessions = [_selected, new SessionInfo { Id = "child", Title = "Check localization and a longer session title", Cwd = "D:\\Projects\\CodexMonitor", ParentId = "review-session", State = SessionState.Completed, UpdatedAt = time, FileVersion = new(200, time.UtcDateTime) }];
        _snapshot = new(_selected, [new("u1", EntryKind.User, _text["ConnectionHelp"] + "\n\n" + _text["PrivacyNote"], time),
            new("a1", EntryKind.Assistant, "## Transcript\n\n" + _text["ConnectionHelp"] + "\n\n```csharp\nvar text = \"你好 · مرحبا · שלום\";\nConsole.WriteLine(text);\n```\n\n" + _text["PrivacyNote"], time)]);
        WelcomePanel.Visibility = Visibility.Collapsed; Conversation.Visibility = Visibility.Visible;
        _ready = true; Localize();
        var scale = Root.XamlRoot.RasterizationScale;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min((int)(width * scale), area.Width - 32); var h = Math.Min((int)(height * scale), area.Height - 32);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        await SettleAsync(); Shell.IsPaneOpen = !ReviewIsOverlay || openPane; await SettleAsync();
        _following = false;
        _messageScroll?.ChangeView(null, 0, null, true);
        await SettleAsync();
        // Each fixture starts with focus in the sidebar, like selecting a conversation.
        // A removed dialog's restored focus must not bring a new transcript child into view.
        SessionSearch.Focus(FocusState.Programmatic);
    }
    internal async Task SwitchReviewThemeAsync(string theme)
    {
        _settings = _settings with { Theme = theme }; Localize(); await SettleAsync();
    }
    internal static async Task SettleAsync()
    {
        // Allow XAML measure/arrange, native non-client updates and composition to complete.
        await Task.Delay(180);
    }
    internal IReadOnlyDictionary<string, Windows.Foundation.Rect> ReviewBounds()
    {
        var bounds = new Dictionary<string, Windows.Foundation.Rect>
        {
            ["title"] = Bounds(SessionTitle),
            ["find"] = Bounds(FindButton), ["more"] = Bounds(MoreButton), ["messages"] = Bounds(MessageList),
            ["footer"] = Bounds(SessionFooter), ["count"] = Bounds(SessionCount)
        };
        if (Composer.Visibility == Visibility.Visible) { bounds["composer"] = Bounds(Composer); bounds["send"] = Bounds(SendButton); }
        if (SessionSetupActions.Visibility == Visibility.Visible)
        {
            if (SessionSetupHelp.Visibility == Visibility.Visible) bounds["setupHelp"] = Bounds(SessionSetupHelp);
            if (ResumeButton.Visibility == Visibility.Visible) bounds["resume"] = Bounds(ResumeButton);
            if (SessionConnectButton.Visibility == Visibility.Visible) bounds["sessionConnect"] = Bounds(SessionConnectButton);
            if (SessionConnectionUnavailable.Visibility == Visibility.Visible) bounds["connectionUnavailable"] = Bounds(SessionConnectionUnavailable);
            if (SessionServerChecking.Visibility == Visibility.Visible) bounds["serverChecking"] = Bounds(SessionServerChecking);
        }
        return bounds;
        Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(Root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
    }
    internal async Task<string> CaptureReviewAsync(string name, FrameworkElement? element = null)
    {
        // Render the production XAML on its native opaque fallback; desktop wallpaper is intentionally excluded.
        Root.Background = new SolidColorBrush(Root.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        await SettleAsync();
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element ?? Root);
        var pixels = await bitmap.GetPixelsAsync();
        var path = Path.Combine(ScreenshotDirectory, name + ".png"); Directory.CreateDirectory(ScreenshotDirectory);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync(); stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes); await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
    internal static string ScreenshotDirectory => Environment.GetEnvironmentVariable("CODEX_MONITOR_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "screenshots");
    internal bool ReviewSessionAttached => _selected is not null && _attached.Contains(_selected.Id);
    internal bool ReviewCanCompose => Composer.IsEnabled;
    internal bool ReviewHasServer => _client is { IsConnected: true };
    internal void SetSessionServerReview(CodexClient client) { _client = client; ObserveClient(client); _connectionEndpoint = "ws://127.0.0.1:45123"; UpdateChrome(); }
    internal void NotifyTranscriptReview(RpcMessage message) { if (_client is not null) HandleMessage(_client, message); }
    internal string ReviewSessionConnectionLabel => SessionConnectLabel.Text;
    internal string ReviewConnectionReason => SessionConnectionReason.Text;
    internal bool ReviewBannerOpen => Notice.IsOpen;
    internal bool ReviewSetupVisible => SessionSetupActions.Visibility == Visibility.Visible;
    internal bool ReviewComposerVisible => Composer.Visibility == Visibility.Visible;
    internal bool ReviewConnectionButtonVisible => ReviewSetupVisible && SessionConnectButton.Visibility == Visibility.Visible;
    internal bool ReviewConnectionHelpVisible => SessionSetupHelp.Visibility == Visibility.Visible && ConnectExplanation.Visibility == Visibility.Visible;
    internal string ReviewConnectionExplanation => ConnectExplanation.Text;
    internal bool ReviewCanResume => ResumeButton.IsEnabled;
    internal bool ReviewResumeButtonVisible => ReviewSetupVisible && ResumeButton.Visibility == Visibility.Visible;
    internal bool ReviewServerChecking => ReviewSetupVisible && SessionServerChecking.Visibility == Visibility.Visible && SessionServerCheckingRing.IsActive;
    internal Task CheckSessionAvailabilityReviewAsync(bool force = false) => CheckSessionAvailabilityAsync(force);
    internal Task RetrySessionActionReviewAsync() => RetrySessionActionAsync();
    internal void ChangeSelectedSessionReview(string id) { _sessionAvailabilityCancellation?.Cancel(); _selected = _selected! with { Id = id }; UpdateChrome(); }
    internal void SetSessionStateReview(SessionState state, bool live = false)
    {
        _selected = _selected! with { State = state, IsLive = live };
        _sessions = _sessions.Select(s => s.Id == _selected.Id ? _selected : s).ToArray();
        if (_snapshot is not null) _snapshot = _snapshot with { Session = _selected };
        UpdateTitle(); UpdateChrome();
    }
    internal void ConfigureResumeGuardReview(Func<SessionInfo, CancellationToken, Task<SessionInfo>> check)
    {
        _checkResumeState = check;
        _resolveExecutable = _ => throw new InvalidOperationException("A changed active session must not start another CLI server");
    }
    internal Task ResumeSessionReviewAsync() => ResumeAsync();
    internal bool ReviewWaitAnimating => Descendants(MessageList).OfType<ProgressRing>().Any(r => r.IsActive && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(r) == _text["ToolWaiting"]);
    internal string ReviewNotice => Notice.Message;
    internal void ConfigureSessionConnectionReview(ISessionServerLocator locator, Func<SessionServerMatch, CancellationToken, Task<CodexClient>> open)
    { _sessionServerLocator = locator; _openSessionServer = open; }
    internal Task ConnectSessionReviewAsync() => ConnectSessionAsync();
    internal async Task ShowStructuredActivityReviewAsync(string json, string? method = null)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var activity = method is null ? CodexClient.ParseItem(doc.RootElement, lifecycle: ToolActivityState.Completed) : TranscriptEvents.Parse(method, doc.RootElement)!;
        _snapshot = new(_selected!, [new("public-summary", EntryKind.Reasoning, "先核对运行状态，再检查操作结果。", DateTimeOffset.Now), activity]);
        _expandedActivities.Add(activity.Id); _expandedActivities.Add(activity.Id + ":edit:0"); RenderMessages(true); await SettleAsync();
    }
    internal IReadOnlyList<T> ReviewVisualElements<T>() where T : DependencyObject => Descendants(Root).OfType<T>().ToArray();
    internal async Task ShowConversationPresentationReviewAsync(IReadOnlyList<TranscriptEntry> entries, bool live)
    {
        if (live) { await ConnectReviewServerAsync(); _selected = _selected! with { IsLive = true, State = SessionState.Active, ActiveTurnId = "turn" }; }
        _snapshot = new(_selected!, entries); _expandedActivities.Clear(); _following = false; RenderMessages(true); UpdateChrome(); await SettleAsync();
    }
    internal async Task UpdateConversationPresentationReviewAsync(IReadOnlyList<TranscriptEntry> entries)
    { _snapshot = new(_selected!, entries); RenderMessages(); await SettleAsync(); }
    internal void ReviewServerWorkAuthority()
    {
        _historyClient = _client; _historySessionId = _selected?.Id; _sessionEventClient = _client;
        if (_selected is not null) _sessionEventMetadata[_selected.Id] = _selected;
        RenderMessages();
    }
    internal async Task ShowProtocolEntryReviewAsync(TranscriptEntry entry, bool expand = true, bool live = false)
    {
        if (live) { await ConnectReviewServerAsync(); _selected = _selected! with { IsLive = true, State = SessionState.Active }; }
        _snapshot = new(_selected!, new[] { entry }); _expandedActivities.Clear(); if (expand) _expandedActivities.Add(entry.Id);
        _following = false; RenderMessages(true); await SettleAsync();
    }
    internal async Task<(ContentDialog Dialog, Task Closing)> OpenProtocolDetailsReviewAsync()
    {
        var closing = DetailsAsync(); await SettleAsync();
        return (VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child as ContentDialog ?? FindVisual<ContentDialog>(p.Child)).First(d => d is not null)!, closing);
    }
    private readonly List<(DependencyObject Element, DependencyProperty Property, object Value)> _reviewFontOverrides = [];
    internal async Task ScaleReviewTextAsync(double factor)
    {
        // Layout stress equivalent, without changing the user's system-wide Text size setting.
        bool WithinIcon(DependencyObject element)
        {
            for (var parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent)) if (parent is IconElement) return true;
            return false;
        }
        var values = Descendants(Root).Where(element => !WithinIcon(element)).Select(element => element switch
        {
            TextBlock t => (Element: element, Property: TextBlock.FontSizeProperty, Size: t.FontSize),
            RichTextBlock t => (Element: element, Property: RichTextBlock.FontSizeProperty, Size: t.FontSize),
            Control c => (Element: element, Property: Control.FontSizeProperty, Size: c.FontSize),
            _ => (Element: element, Property: (DependencyProperty?)null, Size: 0d)
        }).Where(item => item.Property is not null).ToArray();
        foreach (var item in values) { _reviewFontOverrides.Add((item.Element, item.Property!, item.Size)); item.Element.SetValue(item.Property!, item.Size * factor); }
        await SettleAsync();
    }
    internal void ToggleFindReview(bool open) => ToggleFind(open);
    internal bool ReviewFindHasFocus => AccessibilitySupport.ContainsFocus(FindButton);
    internal bool ReviewSearchHasFocus => AccessibilitySupport.ContainsFocus(TranscriptSearch);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    internal async Task<double> SessionAnchorReviewAsync(string id, bool scroll = false)
    {
        if (scroll) { SessionList.ScrollIntoView(_sessionRows[id], ScrollIntoViewAlignment.Leading); await SettleAsync(); }
        var container = (FrameworkElement?)SessionList.ContainerFromItem(_sessionRows[id]);
        return container?.TransformToVisual(Root).TransformPoint(new(0, 0)).Y ?? throw new InvalidOperationException("Anchor is not realized");
    }
    internal async Task<double> TranscriptAnchorReviewAsync(bool update)
    {
        if (!update)
        {
            _snapshot = new(_selected!, Enumerable.Range(0, 40).Select(i => new TranscriptEntry("message-" + i, EntryKind.Assistant, "Message " + i + "\n\nA readable paragraph.", DateTimeOffset.Now)).ToArray());
            _following = false; RenderMessages(true); await SettleAsync(); MessageList.ScrollIntoView(_messages[18], ScrollIntoViewAlignment.Leading); await SettleAsync(); _following = false;
        }
        else
        {
            _snapshot = _snapshot! with { Entries = _snapshot.Entries.Select(e => e.Id == "message-2" ? e with { Text = e.Text + string.Concat(Enumerable.Repeat("\n\nEarlier content expanded.", 10)) } : e).Append(new("message-40", EntryKind.Assistant, "New response", DateTimeOffset.Now)).ToArray() };
            RenderMessages(); await SettleAsync();
        }
        var row = _messages.First(e => e.Entry.Id == "message-18");
        var container = (FrameworkElement?)MessageList.ContainerFromItem(row);
        return container?.TransformToVisual(Root).TransformPoint(new(0, 0)).Y ?? throw new InvalidOperationException("Transcript anchor was lost");
    }
    internal async Task<double> ActivityScrollReviewAsync(bool update)
    {
        if (!update)
        {
            _snapshot = new(_selected!, Enumerable.Range(0, 100).Select(i => new TranscriptEntry("activity-" + i, EntryKind.Tool, "echo " + i, DateTimeOffset.Now, Tool: new("shell", "echo " + i, "OK", ToolActivityState.Completed))).ToArray());
            _following = false; RenderMessages(true); await SettleAsync();
        }
        else { _snapshot = _snapshot! with { Entries = _snapshot.Entries.Append(new("activity-new", EntryKind.Tool, "echo new", DateTimeOffset.Now, Tool: new("shell", "echo new", "OK", ToolActivityState.Completed))).ToArray() }; RenderMessages(); await SettleAsync(); }
        var group = FindVisual<RepeaterList>(MessageList) ?? throw new InvalidOperationException("Activity list not realized");
        var scroll = group.Scroll;
        if (!update) { group.BeginNavigation(true); scroll.ChangeView(null, 700, null, true); await SettleAsync(); }
        return scroll.VerticalOffset;
    }
    internal async Task<(double Before, double After, bool Paused, bool SameList)> NewReplyWhileReadingActivityReviewAsync()
    {
        var before = await ActivityScrollReviewAsync(false);
        var group = FindVisual<RepeaterList>(MessageList)!; _following = true;
        _messages[0].HistoryChanged?.Invoke(true);
        _snapshot = _snapshot! with { Entries = _snapshot.Entries.Append(new("reply-new", EntryKind.Assistant, "A new reply arrived.", DateTimeOffset.Now)).ToArray() };
        RenderMessages(); await SettleAsync();
        var current = FindVisual<RepeaterList>(MessageList)!;
        return (before, current.Scroll.VerticalOffset, !_following && LatestButton.Visibility == Visibility.Visible, ReferenceEquals(group, current));
    }
    internal async Task<bool> ActivityAtEndFollowsNewItemsReviewAsync()
    {
        await ActivityScrollReviewAsync(false);
        var group = FindVisual<RepeaterList>(MessageList)!; var scroll = group.Scroll;
        // Following requires both the outer transcript and the inner work viewport at
        // their ends; use the same action as the floating button to establish that state.
        ReviewJumpLatest(); await SettleAsync();
        await ActivityScrollReviewAsync(true); await SettleAsync();
        return scroll.ScrollableHeight - scroll.VerticalOffset < 2;
    }
    internal void ReviewAutoCollapse(bool value) { _settings = _settings with { AutoCollapseWork = value }; RenderMessages(); }
    internal bool ReviewNativeHighContrast => _themeSettings.HighContrast;
    internal void ReviewTranscriptFile(string path) { _selected = _selected! with { FilePath = path }; }
    internal void ReviewLocalReader(Func<SessionInfo, CancellationToken, Task<TranscriptSnapshot>> reader) => _readLocalTranscript = reader;
    internal Task ReviewReloadTranscript(bool open = false) => LoadTranscriptAsync(_selected!, open);
    internal void ReviewUsePaging() { _pagedReview = true; _following = true; }
    internal int ReviewLoadedCount => _snapshot?.Entries.Count ?? 0;
    internal bool ReviewHasEarlier => _hasEarlier;
    internal bool ReviewHistoryIoPending => _loadingEarlier || _serverRead is not null;
    internal RepeaterList ReviewTranscriptSurface => MessageList;
    internal RepeaterList ReviewSessionSurface => SessionList;
    internal void ReviewBlockHistoryPaging(bool blocked) => _loadingEarlier = blocked;
    internal void ReviewUnacknowledgedHistoryLayout() { _loadingEarlier = false; _earlierRequested = false; _historyAwaitingLayout = true; }
    internal void ReviewBlockSessionPaging(bool blocked) => _sessionQueryLoading = blocked;
    internal void ReviewUnacknowledgedSessionLayout() { _sessionQueryLoading = false; _sessionAwaitingLayout = true; }
    internal bool ReviewLoadingEarlier => _loadingEarlier || _earlierRequested;
    internal void ReviewQueueEarlier() => RequestEarlier();
    internal void ReviewTurnMetadata(params TurnInfo[] turns) { _snapshot = _snapshot! with { Turns = turns.ToDictionary(t => t.Id) }; RenderMessages(); }
    internal async Task<Flyout> ReviewOpenTurnInfoAsync()
    {
        var entry = new TranscriptEntry("turn-info-message", EntryKind.Assistant, "A completed response.", DateTimeOffset.Now.AddDays(-1), TurnId: "turn-info", Model: "example-model");
        await ShowConversationPresentationReviewAsync(new[] { entry }, false);
        ReviewTurnMetadata(new TurnInfo("turn-info") { Model = "example-model", Status = "completed", StartedAt = entry.Timestamp.AddMinutes(-1), CompletedAt = entry.Timestamp,
            LastRequest = new(120, 40, 0, 30, 10, 150), ThreadTotal = new(1000, 600, 0, 300, 90, 1300), ContextWindow = 200000 });
        await SettleAsync();
        var button = ReviewVisualElements<Button>().Single(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "TurnInfo-turn-info");
        var flyout = (Flyout)button.Flyout; flyout.ShowAt(button); await SettleAsync(); return flyout;
    }
    internal Task ReviewLoadEarlierAsync() => LoadEarlierAsync();
    internal async Task ReviewNativeWorkScrollAsync()
    {
        var group = FindVisual<ActivityGroupView>(MessageList)!;
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(group.List);
        ((Microsoft.UI.Xaml.Automation.Provider.IScrollProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Scroll)).SetScrollPercent(-1, 0);
        await SettleAsync();
        for (int i = 0; i < 100 && ReviewLoadingEarlier; i++) await Task.Delay(20);
        await SettleAsync();
        if (group.List.FirstVisibleIndex < 0 || ScrollAnchor.Capture(group.List) is null) throw new InvalidOperationException("The nested viewport has no visible realized item");
    }
    internal string ReviewHistoryState => $"loaded={ReviewLoadedCount}, earlier={_hasEarlier}, loading={_loadingEarlier}, offset={_messageScroll?.VerticalOffset}, start={_historyStart}, latest={_jumpingLatest}, notice={(Notice.IsOpen ? Notice.Message : "")}";
    internal async Task<(object Item, double Y)> ReviewScrollToEarlierAsync()
    {
        TrackTranscriptScroll(); _loadingEarlier = true; _following = false; _jumpingLatest = false; ++_latestScrollGeneration;
        _messageScroll!.ChangeView(null, 150, null, true); await SettleAsync();
        var anchor = ScrollAnchor.Capture(MessageList)!; _loadingEarlier = false;
        // This is the same upward gesture entry point used by pointer wheel and keyboard input.
        RequestEarlier(); return (anchor.Item, anchor.Y);
    }
    internal double ReviewItemY(object item) => ((FrameworkElement)MessageList.ContainerFromItem(item)).TransformToVisual(MessageList).TransformPoint(new(0, 0)).Y;
    internal double ReviewFirstMessageY => ScrollAnchor.Capture(MessageList)?.Y ?? double.PositiveInfinity;
    internal async Task<(RepeaterList List, object Item, double Y)> ReviewScrollToolHistoryAsync()
    {
        var group = FindVisual<RepeaterList>(MessageList)!; var scroll = ScrollAnchor.FindScroll(group)!;
        _loadingEarlier = true; _following = false; _jumpingLatest = false; ++_latestScrollGeneration;
        MessageList.BeginNavigation(false); group.BeginNavigation(false);
        scroll.ChangeView(null, 150, null, true); await SettleAsync();
        var anchor = ScrollAnchor.Capture(group)!; _loadingEarlier = false; RequestEarlier(); return (group, anchor.Item, anchor.Y);
    }
    internal Task ReviewUserInputAsync(CodexClient client, RpcMessage message) => HandleRequestAsync(client, message);
    internal ContentDialog? ReviewInputDialog => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child as ContentDialog ?? FindVisual<ContentDialog>(p.Child)).FirstOrDefault(d => d?.Content is ScrollViewer { Content: UserInputPanel });
    internal void ReviewAttachTranscriptServer(CodexClient client)
    { _client = client; ObserveClient(client); ApplyAttachedSession(_selected!, _selected! with { IsLive = true, State = SessionState.Active }); }
    internal Task ReviewDisconnectTranscriptServer() => ConnectAsync();
    internal string ReviewTranscriptText => string.Join("\n", _snapshot?.Entries.Select(e => e.Text) ?? Array.Empty<string>());
    internal async Task ReviewReasoning(string summary, string full)
    {
        var reasoning = new ReasoningContent(summary, full);
        _snapshot = new(_selected!, new[] { new TranscriptEntry("thinking-review", EntryKind.Reasoning, reasoning.DisplayText, default, Reasoning: reasoning) });
        _expandedActivities.Clear(); RenderMessages(true); await SettleAsync();
    }
    internal bool ReviewLatestVisible => _latestVisible && LatestButton.Visibility == Visibility.Visible;
    internal bool ReviewReasoningHasInlineSummary => Descendants(Descendants(MessageList).OfType<TranscriptCard>().First(c => c.Row?.Entry.Id == "thinking-review")).OfType<RichTextBlock>().Any();
    internal string ReviewScrollDebug => $"offset={_messageScroll?.VerticalOffset}, max={_messageScroll?.ScrollableHeight}, following={_following}, visible={_latestVisible}, reading={_readingActivityHistory}, input={_transcriptScrollingFromInput}, jumping={_jumpingLatest}";
    internal double ReviewLatestOpacity => LatestButton.Opacity;
    internal string ReviewLatestName => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(LatestButton);
    internal Windows.Foundation.Rect ReviewLatestBounds => LatestButton.TransformToVisual(Root).TransformBounds(new(0, 0, LatestButton.ActualWidth, LatestButton.ActualHeight));
    internal void ReviewToggleLatest(bool visible) => SetLatestVisible(visible);
    internal void ReviewJumpLatest() => OnLatest(LatestButton, new RoutedEventArgs());
    internal async Task<(ContentDialog Dialog, Task Closing)> ReviewOpenSettings()
    {
        var opening = SettingsAsync(); await SettleAsync();
        var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child as ContentDialog ?? FindVisual<ContentDialog>(p.Child)).First(d => d is not null)!;
        return (dialog, opening);
    }
    internal async Task<(double Before, double After, bool Expanded, bool Focused)> ExpandedOutputUpdateReviewAsync()
    {
        var entry = new TranscriptEntry("stream-output", EntryKind.Tool, "echo output", DateTimeOffset.Now,
            Tool: new("shell", "echo output", string.Join("\n", Enumerable.Range(0, 120).Select(i => "Output line " + i)), ToolActivityState.Running));
        _snapshot = new(_selected!, [entry]); _expandedActivities.Add(entry.Id); _following = false;
        RenderMessages(true); await SettleAsync();
        ScrollViewer Output() => Descendants(MessageList).OfType<ScrollViewer>().First(s => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(s) == _text["ToolOutput"]);
        var scroll = Output(); scroll.ChangeView(null, 180, null, true); await SettleAsync(); var before = scroll.VerticalOffset;
        Descendants(MessageList).OfType<ActivityDisclosure>().Single(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "ActivityToggle-stream-output").Focus(FocusState.Keyboard);
        _snapshot = new(_selected!, [entry with { Tool = entry.Tool! with { Output = entry.Tool.Output + "\nMore output arrived." } }]);
        RenderMessages(); await SettleAsync();
        var disclosure = Descendants(MessageList).OfType<ActivityDisclosure>().Single(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "ActivityToggle-stream-output");
        return (before, Output().VerticalOffset, disclosure.IsExpanded, AccessibilitySupport.ContainsFocus(disclosure));
    }
    internal async Task ShowActivityReviewAsync(bool expand = false)
    {
        var time = DateTimeOffset.Parse("2026-11-03T08:00:00Z");
        var entries = new List<TranscriptEntry> { new("question", EntryKind.User, "检查连接状态并更新界面。", time), new("summary", EntryKind.Reasoning, "先核对服务器实际加载的会话，再检查当前轮次。", time) };
        for (int i = 0; i < 5; i++)
        {
            var file = new[] { "MainWindow.xaml", "MainWindow.xaml.cs", "Presentation.cs" }[i % 3];
            var input = i < 3 ? "*** Begin Patch\n*** Update File: D:/repo/" + file + "\n@@\n-old\n+new\n+updated\n*** End Patch" : "rg -n 'OnNewSession' src/CodexMonitor.App/MainWindow.xaml.cs";
            entries.Add(new("tool" + i, EntryKind.Tool, input + "\n\nCompleted", time, "exec", Tool: new(i < 3 ? "apply_patch" : "exec", input, i < 3 ? "Updated " + file : "662: private async void OnNewSession(object sender, RoutedEventArgs e)", ToolActivityState.Completed, i < 3 ? null : 720)));
        }
        entries.Add(new("reply", EntryKind.Assistant, "会话归属已确认，界面已更新。", time));
        _snapshot = new(_selected!, entries);
        if (expand) _expandedActivities.Add("tool3");
        else _expandedActivities.Clear();
        RenderMessages(true); await SettleAsync();
    }
    internal IReadOnlyList<EntryRow> ReviewMessageRows => _messages.ToArray();
    internal bool ReviewFollowing => _following;
    internal async Task ReviewAdvanceTranscriptSourceAsync()
    { InvalidateTranscriptRead(); _following = true; _readingActivityHistory = false; RenderMessages(); await SettleAsync(); }
    internal async Task ShowWaitReviewAsync(bool live, bool completed, string language, string theme)
    {
        await SetReviewAsync(760, 650, theme, language);
        if (live) { await ConnectReviewServerAsync(); _selected = _selected! with { IsLive = true, State = SessionState.Active, ActiveTurnId = "turn" }; _attached.Add(_selected.Id); }
        _snapshot = new(_selected!, [new("wait", EntryKind.Tool, "{\"duration_ms\":30000}", DateTimeOffset.Now, "sleep", Tool: new("clock.sleep", "{\"duration_ms\":30000}", completed ? "Wall time: 30.0293 seconds\nSleep completed." : null, completed ? ToolActivityState.Completed : ToolActivityState.Running))]);
        _expandedActivities.Add("wait"); RenderMessages(true); UpdateTitle(); UpdateChrome(); await SettleAsync();
    }

    internal System.Collections.ObjectModel.ObservableCollection<SessionRow> ReviewSessionRows => _visibleSessionRows;
    internal bool ReviewSessionsLoading => _sessionQueryCancellation is not null;
    internal int ReviewSessionRowModels => _sessionRows.Count;
    internal string ReviewPagingState => $"Rows={_visibleSessionRows.Count}, offset={_sessionScroll?.VerticalOffset}, extent={_sessionScroll?.ExtentHeight}, pending={_pendingSessionAnchor?.Session.Id}, restoring={_restoringSessionAnchor}, input={_sessionAnchorInputVersion}, intents={_sessionPageIntent}/{_sessionConsumedPageIntent}, limit={_sessionLimit}, visible={SessionList.FirstVisibleIndex}";
    internal async Task ReviewSessionCatalogAsync(IReadOnlyList<SessionInfo> local, Func<bool, string?, CancellationToken, Task<SessionPage>>? remote = null)
    {
        _sessionQueryCancellation?.Cancel(); _sessionQueryCancellation = null; _sessionEventClient = null; _sessionCatalogBaseline = true; _localSessionSnapshot = Array.Empty<SessionInfo>(); _sessionPageIntent = _sessionConsumedPageIntent = 0; ++_sessionQueryGeneration; _sessionCatalogClient = _client;
        _sessionCatalog = new(local, remote); _sessionCatalogBaseline = true; _sessionLimit = SessionCatalog.PageSize;
        await ReloadSessionQueryAsync(); await SettleAsync();
    }
    internal async Task ReviewSessionQueryAsync(string query, SessionDateField field = SessionDateField.Updated, DateOnly? from = null, DateOnly? through = null)
    {
        SessionSearch.Text = query; _sessionDateField = field; _sessionDateFrom = from; _sessionDateThrough = through; UpdateDateFilterChrome();
        await ReloadSessionQueryAsync(reset: true); await SettleAsync(); await ReviewWaitSessionQueryAsync();
    }
    internal async Task ReviewScrollSessionsEndAsync()
    {
        TrackSessionListScroll(); ++_sessionPageIntent; ++_sessionAnchorInputVersion; _pendingSessionAnchor = null;
        _sessionScroll!.ChangeView(null, _sessionScroll.ScrollableHeight, null, true); RequestMoreSessions(); await Task.Delay(25);
    }
    internal async Task ReviewNativeSessionScrollAsync()
    {
        TrackSessionListScroll();
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(SessionList);
        var scroll = (Microsoft.UI.Xaml.Automation.Provider.IScrollProvider)peer!.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Scroll);
        scroll.SetScrollPercent(-1, 100);
        await SettleAsync(); await ReviewWaitSessionQueryAsync();
    }
    internal void ReviewNativeSessionScroll(double percent)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(SessionList);
        ((Microsoft.UI.Xaml.Automation.Provider.IScrollProvider)peer!.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Scroll)).SetScrollPercent(-1, percent);
    }
    internal void ReviewSelectSession(int index) => SessionList.SelectedItem = _visibleSessionRows[index];
    internal bool ReviewTranscriptLoading => LoadingRing.IsActive;
    internal async Task ReviewWaitSessionQueryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_sessionQueryCancellation is not null) await Task.Delay(10, deadline.Token); await SettleAsync();
    }
    internal async Task<DateFilterPanel> ReviewOpenDateFilterAsync()
    {
        OnSessionDates(DateFilterButton, new RoutedEventArgs()); await SettleAsync();
        return Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).SelectMany(p => Descendants(p.Child)).OfType<DateFilterPanel>().Single();
    }
    internal void ReviewCloseDateFilter() => _sessionDateFlyout?.Hide();
    internal object ReviewSessionSource => SessionList.ItemsSource;
    internal object ReviewSelectedRow => SessionList.SelectedItem;
    internal int ReviewListTransitions => SessionList.Transitions.Count;
    internal void ApplySessionReviewSnapshot(params SessionInfo[] sessions) { _sessions = sessions; ApplyFilter(true); }
    internal void ReadReviewSession(string id)
    {
        _selected = _sessionRows[id].Session;
        _syncing = true;
        try { SessionList.SelectedItem = _sessionRows[id]; _sessionRows[id].MarkRead(); }
        finally { _syncing = false; }
    }
    internal async Task ConnectReviewServerAsync()
    {
        _client = new CodexClient(new JsonRpcClient(new ReviewTransport()));
        _attached.Add(_selected!.Id);
        UpdateChrome(); ApplyFilter(); await SettleAsync();
    }
    internal async Task DisconnectReviewServerAsync()
    {
        var client = _client; _client = null;
        if (client is not null) await client.DisposeAsync();
        UpdateChrome();
    }
    internal void NotifyReviewSession(RpcMessage message) => UpdateSessionActivity(message);
    internal async Task<(ContentDialog Dialog, ServerDiscoveryPanel Panel, Task Closing)> OpenServerDiscoveryReviewAsync(IServerDiscovery discovery)
    {
        _serverDiscovery = discovery;
        _settings = _settings with { UseSharedServer = true };
        var closing = SettingsAsync(); await SettleAsync();
        var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault()
            ?? VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).FirstOrDefault(d => d is not null)
            ?? throw new InvalidOperationException("Settings dialog did not open");
        var panel = FindVisual<ServerDiscoveryPanel>(dialog) ?? throw new InvalidOperationException("Server discovery panel did not appear");
        return (dialog, panel, closing);
    }
    private sealed class ReviewTransport : IRpcTransport
    {
        public Task SendAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public async Task<string?> ReceiveAsync(CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return null; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ReviewHomeDiscovery : ICodexHomeDiscovery
    {
        public Task<IReadOnlyList<string>> FindAsync(string home, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([home]);
    }

    internal async Task<(ContentDialog Dialog, HomeDiscoveryPanel Home, CliSetupPanel Cli, Task Closing)> OpenSetupReviewAsync(ICodexHomeDiscovery homes, Func<string, string> resolve, string home)
    {
        _homeDiscovery = homes; _resolveExecutable = resolve;
        _settings = _settings with { UseSharedServer = false, CodexHome = home };
        var closing = SettingsAsync(); await SettleAsync();
        var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault()
            ?? VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).First(d => d is not null)!;
        return (dialog, FindVisual<HomeDiscoveryPanel>(dialog)!, FindVisual<CliSetupPanel>(dialog)!, closing);
    }
    internal async Task<(ContentDialog Dialog, Task Closing)> OpenMissingCliReviewAsync()
    {
        _settings = _settings with { UseSharedServer = false };
        _resolveExecutable = _ => throw new FileNotFoundException();
        var closing = ConnectAsync(); await SettleAsync();
        var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault()
            ?? VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).First(d => d is not null)!;
        return (dialog, closing);
    }
    internal async Task<bool> SetMissingCliWelcomeReviewAsync(bool missing)
    {
        _settings = _settings with { UseSharedServer = false };
        _resolveExecutable = missing ? _ => throw new FileNotFoundException() : _ => "C:\\codex.exe";
        WelcomePanel.Visibility = Visibility.Visible; Conversation.Visibility = Visibility.Collapsed;
        await CheckCliWelcomeAsync(); await SettleAsync();
        return SetupCodexButton.Visibility == Visibility.Visible;
    }

    internal async Task<string> CaptureSettingsReviewAsync()
    {
        var opening = SettingsAsync(); await SettleAsync();
        var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault();
        if (dialog is null)
        {
            dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).FirstOrDefault(d => d is not null);
        }
        if (dialog is null) throw new InvalidOperationException("Settings dialog did not open");
        var path = await CaptureReviewAsync("settings-560-dark-de", dialog);
        if (dialog.ActualWidth > Root.ActualWidth + 1 || dialog.ActualHeight > Root.ActualHeight + 1) throw new InvalidOperationException("Settings dialog exceeds the window bounds");
        dialog.Hide(); await opening; return path;
    }
}
