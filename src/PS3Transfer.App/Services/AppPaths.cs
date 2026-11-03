namespace PS3Transfer.App.Services;

public static class AppPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PS3TransferSuite");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogFile => Path.Combine(Root, "log.txt");

    public static string Manifests => Path.Combine(Root, "manifests");
}
