namespace CursorCleaner.Helpers;

public static class AppStorage
{
    public static string DefaultRoot
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Application Support",
                    "CursorCleaner");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CursorCleaner");
        }
    }

    public static string DefaultLogs => Path.Combine(DefaultRoot, "logs");

    public static string DefaultBackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "CursorCleanerBackup");
}
