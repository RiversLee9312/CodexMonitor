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
        if (_client is not null) { await _client.DisposeAsync(); _client = null; }
        _syncing = true;
        SessionList.ItemContainerTransitions = _noSessionTransitions;
        _visibleSessionRows.Clear(); _sessionRows.Clear(); _sessionOrder.Clear(); _sessionBaselineLoaded = false;
        _syncing = false;
        _settings = new AppSettings { CodexHome = "C:\\Users\\Example\\.codex", Theme = theme, Language = language, ShowTools = false };
        _cliMissing = false; _homeDiscovery = new ReviewHomeDiscovery(); _resolveExecutable = _ => "C:\\codex.exe";
        _text = new(language);
        var time = DateTimeOffset.Parse("2026-11-03T08:00:00Z");
        _selected = new SessionInfo { Id = "review-session", Title = "Review the transcript layout and keyboard interactions", Cwd = "D:\\Projects\\CodexMonitor", State = SessionState.Completed, CreatedAt = time, UpdatedAt = time, FileVersion = new(100, time.UtcDateTime) };
        _sessions = [_selected, new SessionInfo { Id = "child", Title = "Check localization and a longer session title", Cwd = "D:\\Projects\\CodexMonitor", ParentId = "review-session", State = SessionState.Completed, UpdatedAt = time, FileVersion = new(200, time.UtcDateTime) }];
        _snapshot = new(_selected, [new("u1", EntryKind.User, _text["ConnectionHelp"] + "\n\n" + _text["PrivacyNote"], time),
            new("a1", EntryKind.Assistant, "## Transcript\n\n" + _text["ConnectionHelp"] + "\n\n```csharp\nvar text = \"你好 · مرحبا · שלום\";\nConsole.WriteLine(text);\n```\n\n" + _text["ActivityNote"], time)]);
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
        return new Dictionary<string, Windows.Foundation.Rect>
        {
            ["title"] = Bounds(SessionTitle), ["composer"] = Bounds(Composer), ["send"] = Bounds(SendButton),
            ["find"] = Bounds(FindButton), ["more"] = Bounds(MoreButton), ["messages"] = Bounds(MessageList),
            ["tools"] = Bounds(ToolsCheck), ["status"] = Bounds(ConnectionStatus)
        };
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

    internal System.Collections.ObjectModel.ObservableCollection<SessionRow> ReviewSessionRows => _visibleSessionRows;
    internal object ReviewSessionSource => SessionList.ItemsSource;
    internal object ReviewSelectedRow => SessionList.SelectedItem;
    internal int ReviewListTransitions => SessionList.ItemContainerTransitions.Count;
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
