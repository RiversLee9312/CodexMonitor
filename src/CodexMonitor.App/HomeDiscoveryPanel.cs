using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

internal sealed partial class HomeDiscoveryPanel : StackPanel, IDisposable
{
    private readonly ICodexHomeDiscovery _discovery;
    private readonly LocalizationService _text;
    private readonly CancellationTokenSource _lifetime;
    private readonly TextBox _path;
    private readonly ComboBox _choices;
    private readonly TextBlock _status;
    private readonly Button _scan;
    private readonly ProgressRing _progress;
    private CancellationTokenSource? _scanCancellation;
    private string _lastHome;
    public string Home { get => _path.Text.Trim(); set => _path.Text = value; }
    public IReadOnlyList<string> Results { get; private set; } = [];
    public bool IsScanning { get; private set; }
    public event Action? HomeChanged;

    public HomeDiscoveryPanel(ICodexHomeDiscovery discovery, string home, LocalizationService text, Func<Task<string?>> browse, CancellationToken lifetime)
    {
        _discovery = discovery; _text = text; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime); _lastHome = home.Trim(); Spacing = 8;
        Children.Add(new TextBlock { Text = text["CodexHome"], TextWrapping = TextWrapping.Wrap });
        _path = new TextBox { Text = home, FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(_path, text["CodexHome"]);
        _path.TextChanged += (_, _) => { if (Home != _lastHome) { _lastHome = Home; HomeChanged?.Invoke(); } }; Children.Add(_path);
        // Separate the wrapping action row from the path so long translations fit at the minimum width.
        var actions = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 3 };
        _scan = new Button { Content = text["ScanHomes"], Margin = new Thickness(0, 0, 8, 8) };
        _scan.Click += async (_, _) => await ScanAsync(); actions.Children.Add(_scan);
        var browseButton = new Button { Content = text["Browse"], Margin = new Thickness(0, 0, 8, 8) };
        browseButton.Click += async (_, _) => { if (await browse() is { } folder) Home = folder; }; actions.Children.Add(browseButton);
        _progress = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_progress, text["ScanningHomes"]); actions.Children.Add(_progress); Children.Add(actions);
        _choices = new ComboBox { PlaceholderText = text["SelectHome"], HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_choices, text["SelectHome"]);
        _choices.SelectionChanged += (_, _) => { if (_choices.SelectedItem is ComboBoxItem { Tag: string path }) Home = path; }; Children.Add(_choices);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite); Children.Add(_status);
    }

    public async Task ScanAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _scanCancellation?.Cancel();
        var scan = _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var original = Home; IsScanning = true; _scan.IsEnabled = false; _progress.IsActive = true; _progress.Visibility = Visibility.Visible;
        _status.Text = _text["ScanningHomes"]; _status.Visibility = Visibility.Visible;
        try
        {
            var found = await _discovery.FindAsync(original, scan.Token);
            if (scan.IsCancellationRequested || !ReferenceEquals(_scanCancellation, scan)) return;
            Results = found; _choices.Items.Clear();
            foreach (var path in found)
            {
                var item = new ComboBoxItem { Tag = path, Content = new TextBlock { Text = path, FlowDirection = FlowDirection.LeftToRight, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 } };
                AutomationProperties.SetName(item, path); ToolTipService.SetToolTip(item, path); _choices.Items.Add(item);
            }
            _choices.Visibility = found.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = _text["NoHomesFound"]; _status.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Discovery may fill a missing path, but never replace a valid choice or ongoing edits.
            if (Home == original && !Directory.Exists(original) && found.Count == 1) Home = found[0];
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        { if (!scan.IsCancellationRequested) { _status.Text = _text["HomeScanFailed"]; _status.Visibility = Visibility.Visible; } }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scan)) { IsScanning = false; _scan.IsEnabled = true; _progress.IsActive = false; _progress.Visibility = Visibility.Collapsed; _scanCancellation = null; }
            scan.Dispose();
        }
    }
    public void Cancel() => _scanCancellation?.Cancel();
    public void Dispose() { _lifetime.Cancel(); Cancel(); _lifetime.Dispose(); }
}
