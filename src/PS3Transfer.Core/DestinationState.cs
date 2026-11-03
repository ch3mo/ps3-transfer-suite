namespace PS3Transfer.Core;

public static class DestinationState
{
    public static ExistResult Inspect(string basePath, SplitScheme scheme, long length, long splitSize)
    {
        var expected = Ps3Layout.ExpectedParts(basePath, scheme, length, splitSize);
        var actual = new long?[expected.Count];
        var extras = 0;
        var directory = Path.GetDirectoryName(basePath);
        var baseName = Path.GetFileName(basePath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (!SplitNames.IsRelatedName(name, baseName))
                    continue;
                var match = -1;
                for (var i = 0; i < expected.Count; i++)
                {
                    if (string.Equals(Path.GetFileName(expected[i].Path), name, StringComparison.OrdinalIgnoreCase))
                    {
                        match = i;
                        break;
                    }
                }

                if (match >= 0)
                    actual[match] = new FileInfo(file).Length;
                else
                    extras++;
            }
        }

        var lengths = new long[expected.Count];
        for (var i = 0; i < expected.Count; i++)
            lengths[i] = expected[i].Length;
        return ExistRules.Evaluate(lengths, actual, extras);
    }
}
