using System.IO;

namespace XiaobianPet.Services;

internal static class CodexDesktopInstallation
{
    internal static bool IsPluginDirectory(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, "server.mjs")) &&
        File.Exists(Path.Combine(directory, "scripts", "launch_codex_app_tools_mcp.cmd"));

    internal static string? FindCachedPluginDirectory(string? codexHome)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) return null;
        try
        {
            var root = Path.Combine(codexHome, "plugins", "cache", "openai-bundled", "codex-app-tools");
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .Where(IsPluginDirectory)
                .OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version)
                    ? version : new Version(0, 0))
                .ThenByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static bool IsAppToolsPipeCandidate(string path) =>
        path.StartsWith(@"\\.\pipe\codex-browser-use\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(@"\\.\pipe\codex-browser-use-", StringComparison.OrdinalIgnoreCase);
}
