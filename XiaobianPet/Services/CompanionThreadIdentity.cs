using System.IO;

namespace XiaobianPet.Services;

internal static class CompanionThreadIdentity
{
    internal const string ConversationSource = "xiaobian_pet_companion_chat";
    internal const string AmbientSource = "xiaobian_pet_companion_ambient";
    internal const string ConversationTitle = "柯朵的房间";

    internal static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XiaobianPet");

    internal static string ConversationWorkspace => Path.Combine(DataRoot, "CompanionWorkspace");

    internal static string AmbientWorkspace => Path.Combine(DataRoot, "CompanionAmbientWorkspace");

    internal static bool IsCompanionWorkspace(string? cwd) =>
        IsConversationWorkspace(cwd) || IsAmbientWorkspace(cwd);

    internal static bool IsConversationWorkspace(string? cwd) =>
        IsExpectedWorkspace(cwd, "CompanionWorkspace");

    internal static bool IsAmbientWorkspace(string? cwd) =>
        IsExpectedWorkspace(cwd, "CompanionAmbientWorkspace");

    private static bool IsExpectedWorkspace(string? cwd, string leafName)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return false;
        }

        try
        {
            var candidate = NormalizePath(cwd);
            var expected = NormalizePath(Path.Combine(DataRoot, leafName));
            if (string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // The packaged Codex binary can report LocalAppData paths through
            // Windows package virtualization. Accept only the exact virtualized
            // suffix under LocalAppData\Packages; source and provider are checked
            // independently before a companion thread is resumed.
            var localAppData = NormalizePath(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var packagesRoot = localAppData + Path.DirectorySeparatorChar + "Packages" +
                               Path.DirectorySeparatorChar;
            var virtualizedSuffix = Path.DirectorySeparatorChar + "LocalCache" +
                                    Path.DirectorySeparatorChar + "Local" +
                                    Path.DirectorySeparatorChar + "XiaobianPet" +
                                    Path.DirectorySeparatorChar + leafName;
            return candidate.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase) &&
                   candidate.EndsWith(virtualizedSuffix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsCompanionSource(string? source) =>
        string.Equals(source, ConversationSource, StringComparison.Ordinal) ||
        string.Equals(source, AmbientSource, StringComparison.Ordinal);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
