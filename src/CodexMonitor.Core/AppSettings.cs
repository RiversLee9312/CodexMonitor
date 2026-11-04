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

    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMonitor", "settings.json");
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), CoreJsonContext.Default.AppSettings) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static Task<AppSettings> LoadAsync() => Task.Run(async () =>
    {
        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.AppSettings).ConfigureAwait(false) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    });
    private static readonly SemaphoreSlim Writes = new(1);
    public Task SaveAsync() => Task.Run(async () =>
    {
        await Writes.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temp = SettingsPath + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, this, CoreJsonContext.Default.AppSettings).ConfigureAwait(false);
            File.Move(temp, SettingsPath, true);
        }
        finally { Writes.Release(); }
    });
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, CoreJsonContext.Default.AppSettings));
        File.Move(temp, SettingsPath, true);
    }
}
