namespace PS3Transfer.App.Services;

public static class AppLog
{
    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(AppPaths.LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
