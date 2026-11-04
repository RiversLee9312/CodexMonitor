using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

internal sealed partial class UserInputPanel : StackPanel
{
    private readonly Dictionary<string, Func<string>> _values = [];
    internal event Action? AnswerChanged;
    internal bool Complete => _values.Values.All(read => !string.IsNullOrWhiteSpace(read()));
    internal IReadOnlyDictionary<string, string> Answers => _values.ToDictionary(pair => pair.Key, pair => pair.Value());
    internal UserInputPanel(UserInputRequest request, LocalizationService text, string sessionTitle)
    {
        Spacing = 20; MaxWidth = 520;
        Children.Add(new TextBlock { Text = text["UserInputHint"], TextWrapping = TextWrapping.Wrap });
        Children.Add(new TextBlock { Text = sessionTitle, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        foreach (var question in request.Questions)
        {
            var block = new StackPanel { Spacing = 8 };
            if (question.Header.Length > 0) block.Children.Add(new TextBlock { Text = question.Header, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var label = new TextBlock { Text = question.Question, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }; block.Children.Add(label);
            var group = Guid.NewGuid().ToString(); string? selected = null; var controls = new List<RadioButton>();
            TextBox? input = null; PasswordBox? secret = null; RadioButton? other = null;
            string Read() => question.Options.Count > 0 && other?.IsChecked != true ? selected ?? "" : secret?.Password ?? input?.Text ?? "";
            foreach (var option in question.Options)
            {
                var content = new StackPanel { Spacing = 2 };
                content.Children.Add(new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap });
                if (option.Description.Length > 0) { var description = new TextBlock { Text = option.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12 }; description.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); content.Children.Add(description); }
                var button = new RadioButton { Content = content, GroupName = group, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(button, option.Label); AutomationProperties.SetHelpText(button, option.Description);
                button.Checked += (_, _) => { selected = option.Label; AnswerChanged?.Invoke(); }; controls.Add(button); block.Children.Add(button);
            }
            if (question.Options.Count == 0 || question.AllowOther)
            {
                if (question.Options.Count > 0) { other = new RadioButton { Content = text["OtherAnswer"], GroupName = group }; other.Checked += (_, _) => AnswerChanged?.Invoke(); block.Children.Add(other); }
                Control field;
                if (question.Secret) { secret = new PasswordBox { PlaceholderText = text["AnswerPlaceholder"] }; secret.PasswordChanged += (_, _) => { if (other is not null) other.IsChecked = true; AnswerChanged?.Invoke(); }; field = secret; }
                else { input = new TextBox { PlaceholderText = text["AnswerPlaceholder"], TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 160 }; input.TextChanged += (_, _) => { if (other is not null) other.IsChecked = true; AnswerChanged?.Invoke(); }; field = input; }
                AutomationProperties.SetLabeledBy(field, label); AutomationProperties.SetAutomationId(field, "Answer-" + question.Id); block.Children.Add(field);
            }
            _values[question.Id] = Read; Children.Add(block);
        }
    }
}
