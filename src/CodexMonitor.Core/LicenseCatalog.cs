using System.Text.Json;

namespace CodexMonitor.Core;

public sealed record ComponentLicense(string Name, string Version, string License, string Text, string Notices, string Source);
public static class LicenseCatalog
{
    public static IReadOnlyList<ComponentLicense> Load()
    {
        using var stream = typeof(LicenseCatalog).Assembly.GetManifestResourceStream("CodexMonitor.Core.Licenses.components.json")!;
        using var document = JsonDocument.Parse(stream);
        using var own = new StreamReader(typeof(LicenseCatalog).Assembly.GetManifestResourceStream("CodexMonitor.Core.AppLicense")!);
        return new[] { new ComponentLicense("Codex Monitor", "", "MIT", own.ReadToEnd(), "", "https://github.com/RiversLee9312/CodexMonitor/blob/main/LICENSE") }
            .Concat(document.RootElement.Items().Select(item => new ComponentLicense(item.Str("Name"), item.Str("Version"), item.Str("License"), item.Str("Text"), item.Str("Notices"), item.Str("Source")))).ToArray();
    }
}
