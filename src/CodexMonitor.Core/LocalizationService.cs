using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CodexMonitor.Core;

public sealed class LocalizationService
{
    private readonly Dictionary<string, string> _fallback;
    private Dictionary<string, string> _strings;
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;
    public string LanguageTag => Culture.Name;
    public static IReadOnlyList<string> AvailableLanguages => typeof(LocalizationService).Assembly.GetManifestResourceNames()
        .Where(n => n.Contains(".Localization.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
        .Select(n => n.Split(".Localization.")[1][..^5]).Order().ToArray();

    public LocalizationService(string? language = null)
    {
        _fallback = Read("en-US");
        _strings = _fallback;
        SetLanguage(language);
    }

    public void SetLanguage(string? language)
    {
        var requested = string.IsNullOrEmpty(language) ? CultureInfo.CurrentUICulture.Name : language;
        var available = AvailableLanguages;
        var tag = available.FirstOrDefault(x => x.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(x => x.Split('-')[0] == requested.Split('-')[0]) ?? "en-US";
        Culture = CultureInfo.GetCultureInfo(tag);
        _strings = Read(tag);
    }

    public string this[string key] => _strings.GetValueOrDefault(key, _fallback.GetValueOrDefault(key, key));
    public string Format(string key, params object[] values) => string.Format(Culture, this[key], values);
    public string DateTime(DateTimeOffset value) => value == default ? this["NotAvailable"] : value.ToLocalTime().ToString("g", Culture);
    public string Number(long value) => value.ToString("N0", Culture);
    public static Dictionary<string, string> Read(string tag)
    {
        using var stream = typeof(LocalizationService).Assembly.GetManifestResourceStream($"CodexMonitor.Core.Localization.{tag}.json")
            ?? throw new ArgumentException("Language resource not found", nameof(tag));
        return JsonSerializer.Deserialize(stream, CoreJsonContext.Default.DictionaryStringString)!;
    }

    public string State(SessionState state) => this[state switch
    {
        SessionState.RecordedActive => "StatusRecordedActive", SessionState.Active => "StatusActive",
        SessionState.Idle => "StatusIdle", SessionState.Completed => "StatusCompleted",
        SessionState.Interrupted => "StatusInterrupted", SessionState.Error => "StatusError",
        SessionState.Waiting => "StatusWaiting", _ => "StatusUnknown"
    }];
}
