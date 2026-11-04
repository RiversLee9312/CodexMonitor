using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

internal sealed partial class DateFilterPanel : StackPanel
{
    private readonly ComboBox _field, _range;
    private readonly CalendarDatePicker _from, _through;
    private readonly StackPanel _custom = new() { Spacing = 12 };
    private bool _setting;
    internal event Action<SessionDateField, DateOnly?, DateOnly?>? Applied;
    internal DateFilterPanel(LocalizationService text, SessionDateField field, DateOnly? from, DateOnly? through)
    {
        Spacing = 16; MinWidth = 240; MaxWidth = 310;
        _field = new() { Header = text["DateBasis"], HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
        _field.Items.Add(text["UpdatedTime"]); _field.Items.Add(text["CreatedTime"]); _field.SelectedIndex = (int)field;
        _range = new() { Header = text["DateRange"], HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
        foreach (var key in new[] { "AnyTime", "Today", "Last7Days", "Last30Days", "CustomRange" }) _range.Items.Add(text[key]);
        _from = new() { Header = text["StartDate"], PlaceholderText = text["ChooseDate"], Language = text.LanguageTag, Date = Offset(from), HorizontalAlignment = HorizontalAlignment.Stretch };
        _through = new() { Header = text["EndDate"], PlaceholderText = text["ChooseDate"], Language = text.LanguageTag, Date = Offset(through), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_from, text["StartDate"]); AutomationProperties.SetName(_through, text["EndDate"]);
        AutomationProperties.SetAutomationId(_from, "SessionDateFrom"); AutomationProperties.SetAutomationId(_through, "SessionDateThrough");
        _custom.Children.Add(_from); _custom.Children.Add(_through);
        var error = new TextBlock { Text = text["InvalidDateRange"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush"); AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var actions = new Grid { ColumnSpacing = 12 }; actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var clear = new Button { Content = new TextBlock { Text = text["ClearDateFilter"], TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 40 };
        var apply = new Button { Content = text["ApplyFilters"], MinHeight = 40, Style = (Style)Application.Current.Resources["AccentButtonStyle"] }; Grid.SetColumn(apply, 1);
        AutomationProperties.SetAutomationId(apply, "ApplySessionDates");
        clear.Click += (_, _) => Applied?.Invoke((SessionDateField)_field.SelectedIndex, null, null);
        apply.Click += (_, _) => Applied?.Invoke((SessionDateField)_field.SelectedIndex, Date(_from.Date), Date(_through.Date));
        void Validate() { var valid = _from.Date is null || _through.Date is null || Date(_from.Date) <= Date(_through.Date); apply.IsEnabled = valid; error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible; }
        _range.SelectedIndex = from is null && through is null ? 0 : 4;
        _custom.Visibility = _range.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        _range.SelectionChanged += (_, _) =>
        {
            _setting = true; var today = DateOnly.FromDateTime(DateTime.Today);
            if (_range.SelectedIndex is >= 0 and < 4)
            { _from.Date = _range.SelectedIndex == 0 ? null : Offset(today.AddDays(_range.SelectedIndex == 2 ? -6 : _range.SelectedIndex == 3 ? -29 : 0)); _through.Date = _range.SelectedIndex == 0 ? null : Offset(today); }
            _custom.Visibility = _range.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed; _setting = false; Validate();
        };
        _from.DateChanged += (_, _) => { if (!_setting) Validate(); }; _through.DateChanged += (_, _) => { if (!_setting) Validate(); };
        actions.Children.Add(clear); actions.Children.Add(apply);
        Children.Add(_field); Children.Add(_range); Children.Add(_custom); Children.Add(error); Children.Add(actions); Validate();
    }
    private static DateOnly? Date(DateTimeOffset? value) => value is { } date ? DateOnly.FromDateTime(date.DateTime) : null;
    private static DateTimeOffset? Offset(DateOnly? value)
    { if (value is not { } date) return null; var local = date.ToDateTime(new TimeOnly(12, 0)); return new(local, TimeZoneInfo.Local.GetUtcOffset(local)); }
}
