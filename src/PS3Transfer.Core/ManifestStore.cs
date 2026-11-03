using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PS3Transfer.Core;

public sealed class CopyProgress
{
    public int Index { get; set; }
    public long Committed { get; set; }
    public bool Done { get; set; }
}

public sealed class ResumeManifest
{
    public long SourceStamp { get; set; }
    public string Signature { get; set; } = "";
    public List<CopyProgress> Copies { get; set; } = [];
}

public sealed class ManifestStore
{
    private readonly string _directory;

    public ManifestStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public static string Key(uint volumeSerial, string signature)
    {
        var bytes = Encoding.UTF8.GetBytes(volumeSerial.ToString("X8") + "|" + signature);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public ResumeManifest? Load(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ResumeManifest>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(string key, ResumeManifest manifest)
    {
        var path = PathFor(key);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathFor(string key)
    {
        if (key.Length != 64)
            throw new ArgumentException("Manifest key must be a SHA-256 hex string.", nameof(key));
        foreach (var c in key)
        {
            var hex = (c is >= '0' and <= '9') || (c is >= 'A' and <= 'F') || (c is >= 'a' and <= 'f');
            if (!hex)
                throw new ArgumentException("Manifest key must be a SHA-256 hex string.", nameof(key));
        }

        return Path.Combine(_directory, key + ".json");
    }
}
