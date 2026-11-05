using System.Runtime.InteropServices;

namespace CodexMonitor.Core;

/// <summary>Resolves native binaries without launching npm shims or depending on Explorer's inherited PATH.</summary>
public sealed class CodexExecutableDiscovery(Func<IEnumerable<string?>>? searchDirectories = null, Func<IEnumerable<string>>? installationDirectories = null)
{
    public string Resolve(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        executable = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        var automatic = executable.Equals("codex", StringComparison.OrdinalIgnoreCase) || executable.Equals("codex.exe", StringComparison.OrdinalIgnoreCase);
        if (Path.IsPathFullyQualified(executable))
        {
            if (NativeAt(executable) is { } explicitPath) return explicitPath;
        }
        else
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in (searchDirectories ?? SearchDirectories)())
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var path = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(path) || !visited.Add(path)) continue;
                try
                {
                    foreach (var name in Path.HasExtension(executable) ? new[] { executable } : new[] { executable + ".exe", executable + ".cmd", executable + ".ps1", executable })
                        if (NativeAt(Path.Combine(path, name)) is { } native) return native;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            if (OperatingSystem.IsWindows() && automatic)
                foreach (var directory in (installationDirectories ?? InstallationDirectories)())
                {
                    try
                    {
                        if (NativeAt(Path.Combine(directory, "codex.exe")) is { } direct) return direct;
                        // Desktop updates install into hash-named children. Do not pin a version in settings.
                        if (!Directory.Exists(directory)) continue;
                        foreach (var child in new DirectoryInfo(directory).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc))
                            if (NativeAt(Path.Combine(child.FullName, "codex.exe")) is { } versioned) return versioned;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
        }
        throw new FileNotFoundException("Choose the native codex.exe executable in Settings", executable);
    }

    private static string? NativeAt(string candidate)
    {
        if (!File.Exists(candidate)) return null;
        if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows() && !Path.HasExtension(candidate)) return Path.GetFullPath(candidate);
        if (!candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !candidate.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return null;
        var openai = Path.Combine(Path.GetDirectoryName(candidate)!, "node_modules", "@openai");
        var (package, target) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => ("codex-win32-arm64", "aarch64-pc-windows-msvc"),
            Architecture.X64 => ("codex-win32-x64", "x86_64-pc-windows-msvc"),
            _ => ("codex-win32-ia32", "i686-pc-windows-msvc")
        };
        // npm hoists optional platform packages in some versions and nests them in others.
        foreach (var root in new[] { Path.Combine(openai, package), Path.Combine(openai, "codex", "node_modules", "@openai", package), Path.Combine(openai, "codex") })
        {
            var native = Path.Combine(root, "vendor", target, "codex", "codex.exe");
            if (File.Exists(native)) return native;
        }
        return null;
    }

    public static IEnumerable<string?> SearchDirectories()
    {
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            foreach (var path in (Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(Path.PathSeparator)) yield return path;
        if (!OperatingSystem.IsWindows()) yield break;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, ".local", "bin");
        yield return Path.Combine(profile, ".cargo", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
    }

    public static IEnumerable<string> InstallationDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "OpenAI", "Codex", "bin");
        yield return Path.Combine(local, "Programs", "OpenAI", "Codex", "bin");
    }
}
