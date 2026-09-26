using System.IO;

namespace RemoteAssistant;

internal static class ApplicationPaths
{
    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string LocalApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string UserDataDirectory => Path.Combine(UserProfile, ".remote-assistant");
    public static string SettingsFile => Path.Combine(UserDataDirectory, "settings.json");
    public static string PositionFile => Path.Combine(UserDataDirectory, "position.txt");
    public static string DropsDirectory => Path.Combine(UserDataDirectory, "drops");

    public static void MigrateLegacyUserDataIfNeeded()
    {
        var legacyDirectories = new[]
        {
            Path.Combine(LocalApplicationData, "RemoteAssistant"),
            Path.Combine(LocalApplicationData, "LunaDesktopHelper")
        };

        foreach (var legacyDirectory in legacyDirectories)
        {
            if (!Directory.Exists(legacyDirectory)) continue;
            if (!Directory.Exists(UserDataDirectory)) Directory.Move(legacyDirectory, UserDataDirectory);
            else MergeDirectory(legacyDirectory, UserDataDirectory);
        }
    }

    private static void MergeDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory).ToArray())
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            if (!File.Exists(destinationFile))
            {
                File.Move(sourceFile, destinationFile);
            }
            else if (FilesMatch(sourceFile, destinationFile))
            {
                File.Delete(sourceFile);
            }
            else
            {
                var legacyBackup = Path.Combine(
                    destinationDirectory,
                    $"legacy-migration-{Guid.NewGuid():N}-{Path.GetFileName(sourceFile)}");
                File.Move(sourceFile, legacyBackup);
            }
        }

        foreach (var childSourceDirectory in Directory.EnumerateDirectories(sourceDirectory).ToArray())
        {
            var childDestinationDirectory = Path.Combine(destinationDirectory, Path.GetFileName(childSourceDirectory));
            MergeDirectory(childSourceDirectory, childDestinationDirectory);
        }

        if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any()) Directory.Delete(sourceDirectory);
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        return first.Length == second.Length && File.ReadAllBytes(firstPath).SequenceEqual(File.ReadAllBytes(secondPath));
    }
}
