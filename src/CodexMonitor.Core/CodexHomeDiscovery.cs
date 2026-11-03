namespace CodexMonitor.Core;

public interface ICodexHomeDiscovery
{
    Task<IReadOnlyList<string>> FindAsync(string currentHome, CancellationToken ct = default);
}

/// <summary>Checks explicit configuration and standard locations without traversing drives or reading credentials.</summary>
public sealed class CodexHomeDiscovery : ICodexHomeDiscovery
{
    public static bool IsValidPath(string path)
    {
        try { return Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Length > 0; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private readonly Func<string, IEnumerable<string?>> _candidates;
    public CodexHomeDiscovery() : this(CandidatePaths) { }
    public CodexHomeDiscovery(Func<string, IEnumerable<string?>> candidates) => _candidates = candidates;

    public Task<IReadOnlyList<string>> FindAsync(string currentHome, CancellationToken ct = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var found = new List<string>();
        foreach (var candidate in _candidates(currentHome))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                if (Directory.Exists(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return found;
    }, ct);

    public static IEnumerable<string?> CandidatePaths(string currentHome)
    {
        yield return Environment.GetEnvironmentVariable("CODEX_HOME");
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User);
            yield return Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.Machine);
        }
        yield return currentHome;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }
}
