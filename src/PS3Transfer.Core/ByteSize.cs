using System.Globalization;

namespace PS3Transfer.Core;

public static class ByteSize
{
    public static string Format(long bytes)
    {
        var sign = bytes < 0 ? "-" : "";
        var n = Math.Abs((double)bytes);
        if (n >= 1024d * 1024d * 1024d)
            return sign + (n / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        if (n >= 1024d * 1024d)
            return sign + (n / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (n >= 1024d)
            return sign + (n / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return sign + n.ToString("0", CultureInfo.InvariantCulture) + " B";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1)
            return "0 MB/s";
        return Format((long)bytesPerSecond) + "/s";
    }

    public static string FormatEta(long bytesLeft, double bytesPerSecond)
    {
        if (bytesPerSecond < 1024 * 64 || bytesLeft <= 0)
            return "estimating time";
        var seconds = bytesLeft / bytesPerSecond;
        if (seconds < 90)
            return ((int)Math.Ceiling(seconds)).ToString(CultureInfo.InvariantCulture) + " sec left";
        if (seconds < 3600)
            return ((int)Math.Ceiling(seconds / 60)).ToString(CultureInfo.InvariantCulture) + " min left";
        return (seconds / 3600d).ToString("0.0", CultureInfo.InvariantCulture) + " hr left";
    }
}
