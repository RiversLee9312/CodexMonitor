using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

internal sealed partial class LicensePanel : Grid
{
    internal LicensePanel(LocalizationService text)
    {
        RowSpacing = 16; MaxWidth = 520; MinWidth = 240;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var components = LicenseCatalog.Load();
        var chooser = new ComboBox { Header = text["Components"], HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(chooser, text["Components"]);
        foreach (var component in components) chooser.Items.Add(component.Name + " " + component.Version);
        var body = new StackPanel { Spacing = 16, FlowDirection = FlowDirection.LeftToRight };
        var scroller = new ScrollViewer { Content = body, MaxHeight = 400, FlowDirection = FlowDirection.LeftToRight, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(scroller, text["License"]); SetRow(scroller, 1); Children.Add(chooser); Children.Add(scroller);
        chooser.SelectionChanged += (_, _) =>
        {
            body.Children.Clear(); if (chooser.SelectedIndex < 0) return;
            var component = components[chooser.SelectedIndex];
            if (component.Text.Length == 0) body.Children.Add(new HyperlinkButton { Content = text["License"], NavigateUri = new Uri(component.Source), HorizontalAlignment = HorizontalAlignment.Left });
            Add(component.Text); if (component.Notices.Length > 0) { var heading = new TextBlock { Text = text["Notices"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }; AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2); body.Children.Add(heading); Add(component.Notices); }
            scroller.ChangeView(null, 0, null, true);
        };
        chooser.SelectedIndex = 0;
        void Add(string value) => body.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
    }
}
