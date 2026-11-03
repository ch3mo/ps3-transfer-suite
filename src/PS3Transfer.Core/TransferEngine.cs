using System.Buffers;

namespace PS3Transfer.Core;

public enum TransferOutcome
{
    Copied = 0,
    Skipped = 1,
    Failed = 2,
    Canceled = 3,
}

public sealed record TransferResult(TransferOutcome Outcome, long BytesWritten, string? Error, string? Warning);

public readonly record struct TransferProgress(long FilePosition, long FileLength, long SessionBytes, string Detail);

public sealed class TransferOptions
{
    public bool VerifyCrc32 { get; init; }
    public bool HideExtraParts { get; init; } = true;
    public ManifestStore? Manifests { get; init; }
}

public static class TransferEngine
{
    private const int BufferBytes = 4 * 1024 * 1024;
    private const long ManifestInterval = 128L * 1024 * 1024;

    public static async Task<TransferResult> CopyAsync(
        TransferPlan plan,
        TransferOptions options,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (plan.Action is TransferAction.Skip)
            return new TransferResult(TransferOutcome.Skipped, 0, null, plan.Warning);
        if (plan.Action is TransferAction.Blocked)
            return new TransferResult(TransferOutcome.Failed, 0, plan.Warning ?? "This item cannot be copied.", plan.Warning);

        if (plan.FatDestination)
        {
            foreach (var copy in plan.Copies)
            {
                if (copy.Scheme == SplitScheme.None && copy.Length > plan.SplitSize)
                {
                    return new TransferResult(TransferOutcome.Failed, 0,
                        "A file is larger than FAT32 allows and was not split.", null);
                }
            }
        }

        try
        {
            if (plan.SourceFiles.Count > 0 && DiscInspector.StampOf(plan.SourceFiles) != plan.SourceStamp)
            {
                return new TransferResult(TransferOutcome.Failed, 0,
                    "The source changed after it was added. Remove it and add it again.", null);
            }
        }
        catch (IOException ex)
        {
            return new TransferResult(TransferOutcome.Failed, 0, ex.Message, null);
        }

        var session = 0L;
        var position = 0L;
        var gate = new ReportGate();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Action == TransferAction.Replace)
            {
                foreach (var copy in plan.Copies)
                    DeleteRelated(copy.DestinationBase);
                options.Manifests?.Delete(plan.ManifestKey);
            }

            var manifest = plan.Action == TransferAction.Resume
                ? LoadManifest(options.Manifests, plan)
                : null;
            manifest ??= new ResumeManifest { Signature = plan.Signature, SourceStamp = plan.SourceStamp };

            foreach (var copy in plan.Copies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wrote = await CopyOneAsync(plan, copy, manifest, options, position, session, progress, gate, cancellationToken)
                    .ConfigureAwait(false);
                session += wrote;
                position += copy.Length;
            }

            var sizeError = CheckSizes(plan);
            if (sizeError is not null)
                return new TransferResult(TransferOutcome.Failed, session, sizeError, null);

            if (options.VerifyCrc32)
            {
                var mismatch = await VerifyAsync(plan, progress, position, session, gate, cancellationToken).ConfigureAwait(false);
                if (mismatch is not null)
                {
                    foreach (var copy in plan.Copies)
                        DeleteRelated(copy.DestinationBase);
                    options.Manifests?.Delete(plan.ManifestKey);
                    return new TransferResult(TransferOutcome.Failed, session, mismatch, null);
                }
            }

            if (options.HideExtraParts)
                HideTailParts(plan);
            options.Manifests?.Delete(plan.ManifestKey);
            var warning = plan.Warning;
            return new TransferResult(
                plan.Action == TransferAction.Resume ? TransferOutcome.Copied : TransferOutcome.Copied,
                session,
                null,
                warning);
        }
        catch (OperationCanceledException)
        {
            return new TransferResult(TransferOutcome.Canceled, session, null, null);
        }
        catch (IOException ex)
        {
            return new TransferResult(TransferOutcome.Failed, session, ex.Message, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new TransferResult(TransferOutcome.Failed, session, ex.Message, null);
        }
    }

    private static async Task<long> CopyOneAsync(
        TransferPlan plan,
        PlannedCopy copy,
        ResumeManifest manifest,
        TransferOptions options,
        long positionBase,
        long sessionBase,
        IProgress<TransferProgress>? progress,
        ReportGate gate,
        CancellationToken cancellationToken)
    {
        var entry = manifest.Copies.FirstOrDefault(c => c.Index == copy.Index);
        long? committed = plan.Action == TransferAction.Resume ? entry?.Committed : null;
        if (plan.Action is TransferAction.Copy or TransferAction.Replace)
            committed = null;

        var measured = DestinationState.Inspect(copy.DestinationBase, copy.Scheme, copy.Length, plan.SplitSize);
        var point = ExistRules.Resolve(
            copy.Length,
            plan.Action is TransferAction.Copy or TransferAction.Replace ? 0 : measured.FullEnd,
            plan.Action is TransferAction.Copy or TransferAction.Replace ? null : measured.PartialLength,
            committed);

        if (point.Start >= copy.Length)
        {
            Report(gate, progress, positionBase + copy.Length, plan.TotalBytes, sessionBase, Path.GetFileName(copy.DestinationBase), force: true);
            return 0;
        }

        if (point.Truncate)
            Truncate(copy, plan.SplitSize, point.Start);

        var session = 0L;
        var sinceSave = 0L;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        PartStream? reader = null;
        FileStream? dest = null;
        var partIndex = -1;
        try
        {
            reader = new PartStream(copy.Sources);
            reader.Position = point.Start;
            var pos = point.Start;
            while (pos < copy.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var grain = copy.Scheme == SplitScheme.None ? long.MaxValue : plan.SplitSize;
                var index = grain == long.MaxValue ? 0 : (int)(pos / grain);
                var partStart = grain == long.MaxValue ? 0L : (long)index * grain;
                var partLength = Math.Min(grain, copy.Length - partStart);
                var inPart = pos - partStart;
                if (dest is null || index != partIndex)
                {
                    if (dest is not null)
                    {
                        await FinishPartAsync(dest, copy, plan, partIndex, options, cancellationToken).ConfigureAwait(false);
                        dest = null;
                        Save(options, plan, manifest, copy.Index, pos, done: false);
                        sinceSave = 0;
                    }

                    dest = OpenPart(plan.DriveRoot, copy, index, inPart);
                    partIndex = index;
                }

                var want = (int)Math.Min(buffer.Length, Math.Min(partLength - inPart, copy.Length - pos));
                var got = await ReadExactAsync(reader, buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (got != want)
                    throw new EndOfStreamException("The source ended early. It may have changed during the copy.");
                await dest.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
                pos += got;
                session += got;
                sinceSave += got;
                var detail = Detail(copy, plan.SplitSize, index);
                var force = pos == copy.Length || pos == partStart + partLength;
                Report(gate, progress, positionBase + pos, plan.TotalBytes, sessionBase + session, detail, force);
                if (force || sinceSave >= ManifestInterval)
                {
                    await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
                    dest.Flush(true);
                    Save(options, plan, manifest, copy.Index, pos, done: pos == copy.Length);
                    sinceSave = 0;
                }
            }

            if (dest is not null)
            {
                await FinishPartAsync(dest, copy, plan, partIndex, options, cancellationToken).ConfigureAwait(false);
                dest = null;
                Save(options, plan, manifest, copy.Index, copy.Length, done: true);
            }

            return session;
        }
        finally
        {
            if (dest is not null)
            {
                try
                {
                    dest.Flush(true);
                    var pos = reader?.Position ?? point.Start;
                    Save(options, plan, manifest, copy.Index, pos, done: false);
                }
                catch (IOException)
                {
                    // The resume manifest is a convenience. The copy result still reports the failure.
                }

                await dest.DisposeAsync().ConfigureAwait(false);
            }

            reader?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileStream OpenPart(string driveRoot, PlannedCopy copy, int index, long offsetInPart)
    {
        var path = Ps3Layout.PartPath(copy.DestinationBase, copy.Scheme, index);
        if (!PathUtil.IsUnder(driveRoot, path))
            throw new IOException("Refusing to write outside the destination drive.");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (offsetInPart <= 0)
        {
            return new FileStream(PathUtil.Win32(path), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        var stream = new FileStream(PathUtil.Win32(path), FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.SetLength(offsetInPart);
        stream.Position = offsetInPart;
        return stream;
    }

    private static async Task FinishPartAsync(
        FileStream dest, PlannedCopy copy, TransferPlan plan, int index, TransferOptions options, CancellationToken cancellationToken)
    {
        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        dest.Flush(true);
        var path = dest.Name;
        await dest.DisposeAsync().ConfigureAwait(false);
        if (options.HideExtraParts && copy.Scheme != SplitScheme.None && index > 0)
            Hide(Ps3Layout.PartPath(copy.DestinationBase, copy.Scheme, index));
    }

    private static void Truncate(PlannedCopy copy, long splitSize, long start)
    {
        var grain = copy.Scheme == SplitScheme.None ? long.MaxValue : splitSize;
        var index = grain == long.MaxValue ? 0 : (int)(start / grain);
        var partStart = grain == long.MaxValue ? 0L : (long)index * grain;
        var inPart = start - partStart;
        var path = Ps3Layout.PartPath(copy.DestinationBase, copy.Scheme, index);
        if (!File.Exists(path))
            return;
        if (inPart <= 0)
        {
            ClearAndDelete(path);
            return;
        }

        using var stream = new FileStream(PathUtil.Win32(path), FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(inPart);
    }

    private static string? CheckSizes(TransferPlan plan)
    {
        foreach (var copy in plan.Copies)
        {
            foreach (var part in Ps3Layout.ExpectedParts(copy.DestinationBase, copy.Scheme, copy.Length, plan.SplitSize))
            {
                if (!File.Exists(part.Path))
                    return "Missing " + Path.GetFileName(part.Path) + " after the copy.";
                var length = new FileInfo(part.Path).Length;
                if (length != part.Length)
                    return Path.GetFileName(part.Path) + " is " + ByteSize.Format(length) + " and should be " + ByteSize.Format(part.Length) + ".";
            }
        }

        return null;
    }

    private static async Task<string?> VerifyAsync(
        TransferPlan plan,
        IProgress<TransferProgress>? progress,
        long position,
        long session,
        ReportGate gate,
        CancellationToken cancellationToken)
    {
        Report(gate, progress, position, plan.TotalBytes, session, "Verifying the copy", force: true);
        foreach (var copy in plan.Copies)
        {
            uint sourceCrc;
            await using (var source = new PartStream(copy.Sources))
                sourceCrc = await CrcStreamAsync(source, cancellationToken).ConfigureAwait(false);

            uint destCrc = 0;
            foreach (var part in Ps3Layout.ExpectedParts(copy.DestinationBase, copy.Scheme, copy.Length, plan.SplitSize))
            {
                await using var stream = new FileStream(PathUtil.Win32(part.Path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                destCrc = await CrcContinueAsync(destCrc, stream, cancellationToken).ConfigureAwait(false);
            }

            if (sourceCrc != destCrc)
                return "The copy of " + Path.GetFileName(copy.DestinationBase) + " does not match the source. It was removed from the drive.";
        }

        return null;
    }

    private static async Task<uint> CrcStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        return await CrcContinueAsync(0, stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<uint> CrcContinueAsync(uint crc, Stream stream, CancellationToken cancellationToken)
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

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[got..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
                break;
            got += n;
        }

        return got;
    }

    private static void HideTailParts(TransferPlan plan)
    {
        foreach (var copy in plan.Copies)
        {
            if (copy.Scheme == SplitScheme.None)
                continue;
            var count = Ps3Layout.PartCount(copy.Length, plan.SplitSize);
            for (var i = 1; i < count; i++)
                Hide(Ps3Layout.PartPath(copy.DestinationBase, copy.Scheme, i));
        }
    }

    private static void Hide(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes | FileAttributes.Hidden);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void DeleteRelated(string basePath)
    {
        var directory = Path.GetDirectoryName(basePath);
        var name = Path.GetFileName(basePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory).ToList())
        {
            if (!SplitNames.IsRelatedName(Path.GetFileName(file), name))
                continue;
            ClearAndDelete(file);
        }
    }

    private static void ClearAndDelete(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static ResumeManifest? LoadManifest(ManifestStore? store, TransferPlan plan)
    {
        var loaded = store?.Load(plan.ManifestKey);
        if (loaded is null)
            return null;
        if (loaded.Signature != plan.Signature || loaded.SourceStamp != plan.SourceStamp)
            return null;
        return loaded;
    }

    private static void Save(TransferOptions options, TransferPlan plan, ResumeManifest manifest, int index, long committed, bool done)
    {
        if (options.Manifests is null || string.IsNullOrEmpty(plan.ManifestKey) || plan.ManifestKey.All(c => c == '0'))
            return;
        manifest.Signature = plan.Signature;
        manifest.SourceStamp = plan.SourceStamp;
        var entry = manifest.Copies.FirstOrDefault(c => c.Index == index);
        if (entry is null)
        {
            entry = new CopyProgress { Index = index };
            manifest.Copies.Add(entry);
        }

        entry.Committed = committed;
        entry.Done = done;
        options.Manifests.Save(plan.ManifestKey, manifest);
    }

    private static string Detail(PlannedCopy copy, long splitSize, int index)
    {
        var name = Path.GetFileName(Ps3Layout.PartPath(copy.DestinationBase, copy.Scheme, index));
        if (copy.Scheme == SplitScheme.None)
            return name;
        var count = Ps3Layout.PartCount(copy.Length, splitSize);
        return "Part " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " of " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " · " + name;
    }

    private sealed class ReportGate
    {
        public long Last;
    }

    private static void Report(ReportGate gate, IProgress<TransferProgress>? progress, long position, long total, long session, string detail, bool force)
    {
        if (progress is null)
            return;
        var now = Environment.TickCount64;
        if (!force && now - gate.Last < 100)
            return;
        gate.Last = now;
        progress.Report(new TransferProgress(position, total, session, detail));
    }
}
