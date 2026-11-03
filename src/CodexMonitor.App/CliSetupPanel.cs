using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

internal sealed partial class CliSetupPanel : StackPanel, IDisposable
{
    public static readonly Uri InstallationUri = new("https://learn.chatgpt.com/docs/cli");
    private readonly LocalizationService _text;
    private readonly Func<string, string> _resolve;
    private readonly TextBox _executable;
    private readonly TextBlock _status;
    private readonly HyperlinkButton _install;
    private readonly CancellationTokenSource _lifetime;
    private int _revision;
    public string Executable { get => _executable.Text.Trim(); set => _executable.Text = value; }
    public bool IsAvailable { get; private set; }

    public CliSetupPanel(string executable, LocalizationService text, Func<Task<string?>> choose, CancellationToken lifetime, Func<string, string>? resolve = null)
    {
        _text = text; _resolve = resolve ?? StdioTransport.ResolveExecutable;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Spacing = 8;
        _executable = new TextBox { Header = text["CodexExecutable"], Text = executable, FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(_executable, text["CodexExecutable"]);
        _executable.TextChanged += async (_, _) => await CheckAsync(); Children.Add(_executable);
        var actions = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 2 };
        var select = new Button { Content = text["ChooseExecutable"], Margin = new Thickness(0, 0, 8, 8) };
        select.Click += async (_, _) => { if (await choose() is { } path) { Executable = path; await CheckAsync(); } }; actions.Children.Add(select);
        var check = new Button { Content = text["CheckAgain"], Margin = new Thickness(0, 0, 8, 8) };
        check.Click += async (_, _) => await CheckAsync(); actions.Children.Add(check); Children.Add(actions);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite); Children.Add(_status);
        _install = new HyperlinkButton { Content = text["InstallCodex"], NavigateUri = InstallationUri, Padding = new Thickness(0, 4, 0, 4), Visibility = Visibility.Collapsed };
        Children.Add(_install);
    }
    public async Task CheckAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        var revision = ++_revision; var executable = Executable;
        _status.Text = _text["CliChecking"];
        var available = await Task.Run(() =>
        {
            try { _resolve(executable); return true; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception) { return false; }
        });
        if (_lifetime.IsCancellationRequested || revision != _revision || executable != Executable) return;
        IsAvailable = available; _status.Text = _text[available ? "CliAvailable" : "CliNotInstalled"];
        _install.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
