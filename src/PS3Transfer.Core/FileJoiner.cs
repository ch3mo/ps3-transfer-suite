using System.Buffers;

namespace PS3Transfer.Core;

public static class FileJoiner
{
    public static bool TryDiscover(string anyPart, out IReadOnlyList<string> parts, out string? error)
    {
        parts = [];
        var name = Path.GetFileName(anyPart);
        if (PkgParts.TryParse(name, out _, out _))
            return PkgParts.TryCollect(anyPart, out parts, out error);

        if (!SplitNames.TryParse(name, out var baseName, out _, out var scheme))
        {
            error = "Choose the first split piece: a .iso.0 file, a .pkg.1 file, a _1.pkg file, or a .66600 file.";
            return false;
        }

        var directory = Path.GetDirectoryName(anyPart);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            error = "The folder for that split set is missing.";
            return false;
        }

        var found = new List<(int Index, string Path)>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!SplitNames.TryParse(Path.GetFileName(file), out var otherBase, out var index, out var otherScheme))
                continue;
            if (!otherBase.Equals(baseName, StringComparison.OrdinalIgnoreCase) || otherScheme != scheme)
                continue;
            found.Add((index, file));
        }

        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        if (found.Count == 0)
        {
            error = "No split pieces were found.";
            return false;
        }

        var expected = scheme switch
        {
            SplitScheme.IsoDotNumber => 0,
            SplitScheme.PkgNumber => found[0].Index == 1 ? 1 : 0,
            _ => 66600,
        };
        if (scheme == SplitScheme.PkgNumber && found[0].Index is not 0 and not 1)
        {
            error = "The set does not start at .pkg.0 or .pkg.1.";
            return false;
        }

        if (found[0].Index != expected)
        {
            error = "The set does not start at " + scheme switch
            {
                SplitScheme.IsoDotNumber => ".iso.0.",
                SplitScheme.PkgNumber => ".pkg.0 or .pkg.1.",
                _ => ".66600.",
            };
            return false;
        }

        for (var i = 1; i < found.Count; i++)
        {
            if (found[i].Index != found[i - 1].Index + 1)
            {
                error = "Missing part " + (found[i - 1].Index + 1) + ".";
                return false;
            }
        }

        parts = found.Select(f => f.Path).ToList();
        error = null;
        return true;
    }

    public static async Task<TransferResult> JoinAsync(
        IReadOnlyList<string> parts,
        string output,
        string outputFileSystem,
        bool verify,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var part in parts)
        {
            if (!File.Exists(part))
                return new TransferResult(TransferOutcome.Failed, 0, "Missing " + Path.GetFileName(part) + ".", null);
            total += new FileInfo(part).Length;
        }

        if (Ps3Layout.IsFat(outputFileSystem) && total > Ps3Layout.SplitSize)
        {
            return new TransferResult(TransferOutcome.Failed, 0,
                "The joined file is " + ByteSize.Format(total) + ". FAT32 cannot store that. Join it onto an NTFS disk.", null);
        }

        var directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var session = 0L;
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        try
        {
            await using var dest = new FileStream(PathUtil.Win32(output), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            foreach (var part in parts)
            {
                await using var source = new FileStream(PathUtil.Win32(part), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                        break;
                    await dest.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    session += n;
                    progress?.Report(new TransferProgress(session, total, session, Path.GetFileName(part)));
                }
            }

            await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
            dest.Flush(true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            return new TransferResult(TransferOutcome.Canceled, session, null, null);
        }
        catch (IOException ex)
        {
            return new TransferResult(TransferOutcome.Failed, session, ex.Message, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (new FileInfo(output).Length != total)
        {
            TryDelete(output);
            return new TransferResult(TransferOutcome.Failed, session, "The joined file is the wrong size.", null);
        }

        if (verify)
        {
            progress?.Report(new TransferProgress(total, total, session, "Verifying"));
            uint sourceCrc = 0;
            foreach (var part in parts)
            {
                await using var stream = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
                sourceCrc = await CrcAsync(sourceCrc, stream, cancellationToken).ConfigureAwait(false);
            }

            uint destCrc;
            await using (var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
                destCrc = await CrcAsync(0, stream, cancellationToken).ConfigureAwait(false);
            if (sourceCrc != destCrc)
            {
                TryDelete(output);
                return new TransferResult(TransferOutcome.Failed, session, "The joined file does not match the parts.", null);
            }
        }

        return new TransferResult(TransferOutcome.Copied, session, null, null);
    }

    public static async Task<TransferResult> JoinToSplitAsync(
        IReadOnlyList<string> parts,
        IReadOnlyList<string> outputs,
        long splitSize,
        bool verify,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (splitSize <= 0)
            return new TransferResult(TransferOutcome.Failed, 0, "The split size is invalid.", null);
        if (outputs.Count == 0)
            return new TransferResult(TransferOutcome.Failed, 0, "The split layout is empty.", null);

        long total = 0;
        foreach (var part in parts)
        {
            if (!File.Exists(part))
                return new TransferResult(TransferOutcome.Failed, 0, "Missing " + Path.GetFileName(part) + ".", null);
            total += new FileInfo(part).Length;
        }

        if (outputs.Count != Ps3Layout.PartCount(total, splitSize))
            return new TransferResult(TransferOutcome.Failed, 0, "The split layout does not match the package size.", null);

        foreach (var output in outputs)
        {
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        var created = new List<string>();
        var session = 0L;
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        FileStream? dest = null;
        try
        {
            var outputIndex = 0;
            long inPart = 0;
            foreach (var part in parts)
            {
                await using var source = new FileStream(PathUtil.Win32(part), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                        break;

                    var span = buffer.AsMemory(0, n);
                    while (span.Length > 0)
                    {
                        if (dest is null || inPart >= splitSize)
                        {
                            if (dest is not null)
                            {
                                await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
                                await dest.DisposeAsync().ConfigureAwait(false);
                                dest = null;
                            }

                            if (outputIndex >= outputs.Count)
                            {
                                DeleteAll(created);
                                return new TransferResult(TransferOutcome.Failed, session, "The split layout ran out of files.", null);
                            }

                            var path = outputs[outputIndex];
                            outputIndex++;
                            dest = new FileStream(PathUtil.Win32(path), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20,
                                FileOptions.Asynchronous | FileOptions.SequentialScan);
                            created.Add(path);
                            inPart = 0;
                        }

                        var take = (int)Math.Min(span.Length, splitSize - inPart);
                        await dest.WriteAsync(span[..take], cancellationToken).ConfigureAwait(false);
                        inPart += take;
                        session += take;
                        span = span[take..];
                        progress?.Report(new TransferProgress(session, total, session, Path.GetFileName(part)));
                    }
                }
            }

            if (dest is not null)
            {
                await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
                dest.Flush(true);
                await dest.DisposeAsync().ConfigureAwait(false);
                dest = null;
            }
        }
        catch (OperationCanceledException)
        {
            if (dest is not null)
                await dest.DisposeAsync().ConfigureAwait(false);
            DeleteAll(created);
            return new TransferResult(TransferOutcome.Canceled, session, null, null);
        }
        catch (IOException ex)
        {
            if (dest is not null)
                await dest.DisposeAsync().ConfigureAwait(false);
            DeleteAll(created);
            return new TransferResult(TransferOutcome.Failed, session, ex.Message, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        long written = 0;
        foreach (var output in outputs)
        {
            if (!File.Exists(output))
            {
                DeleteAll(created);
                return new TransferResult(TransferOutcome.Failed, session, "A split piece was not written.", null);
            }

            written += new FileInfo(output).Length;
        }

        if (written != total)
        {
            DeleteAll(created);
            return new TransferResult(TransferOutcome.Failed, session, "The joined file is the wrong size.", null);
        }

        if (verify)
        {
            progress?.Report(new TransferProgress(total, total, session, "Verifying"));
            uint sourceCrc = 0;
            foreach (var part in parts)
            {
                await using var stream = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
                sourceCrc = await CrcAsync(sourceCrc, stream, cancellationToken).ConfigureAwait(false);
            }

            uint destCrc = 0;
            foreach (var output in outputs)
            {
                await using var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
                destCrc = await CrcAsync(destCrc, stream, cancellationToken).ConfigureAwait(false);
            }

            if (sourceCrc != destCrc)
            {
                DeleteAll(created);
                return new TransferResult(TransferOutcome.Failed, session, "The joined file does not match the parts.", null);
            }
        }

        return new TransferResult(TransferOutcome.Copied, session, null, null);
    }

    private static async Task<uint> CrcAsync(uint crc, Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                    return crc;
                crc = Crc32.Append(crc, buffer.AsSpan(0, n));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void DeleteAll(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            TryDelete(path);
    }
}
