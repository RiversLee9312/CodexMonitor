using System.Text.Json;

namespace CodexMonitor.Core;

public sealed record AppSettings
{
    public string CodexHome { get; init; } = LocalSessionStore.DefaultHome;
    public string Executable { get; init; } = "codex";
    public string Endpoint { get; init; } = "ws://127.0.0.1:4500";
    public bool UseSharedServer { get; init; }
    public string Language { get; init; } = "";
    public string Theme { get; init; } = "System";
    public bool ShowTools { get; init; }

    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMonitor", "settings.json");
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }
}
