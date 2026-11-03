namespace PS3Transfer.Core;

public readonly record struct ExistResult(ExistState State, long FullEnd, long? PartialLength);

public readonly record struct ResumePoint(long Start, bool Truncate);

public static class ExistRules
{
    public static ExistResult Evaluate(IReadOnlyList<long> expectedLengths, IReadOnlyList<long?> actualLengths, int extraCount)
    {
        if (expectedLengths.Count != actualLengths.Count)
            throw new ArgumentException("Expected and actual part lists must line up.");

        var any = false;
        long full = 0;
        for (var i = 0; i < expectedLengths.Count; i++)
        {
            var actual = actualLengths[i];
            if (actual is null)
            {
                for (var j = i + 1; j < actualLengths.Count; j++)
                {
                    if (actualLengths[j] is not null)
                        return new ExistResult(ExistState.Conflict, full, null);
                }

                if (!any && extraCount == 0)
                    return new ExistResult(ExistState.Missing, 0, null);
                if (!any && extraCount > 0)
                    return new ExistResult(ExistState.Conflict, 0, null);
                return new ExistResult(ExistState.Partial, full, null);
            }

            any = true;
            if (actual == expectedLengths[i])
            {
                full += actual.Value;
                continue;
            }

            if (actual < expectedLengths[i])
            {
                for (var j = i + 1; j < actualLengths.Count; j++)
                {
                    if (actualLengths[j] is not null)
                        return new ExistResult(ExistState.Conflict, full, null);
                }

                if (extraCount > 0)
                    return new ExistResult(ExistState.Conflict, full, null);
                return new ExistResult(ExistState.Partial, full, actual);
            }

            return new ExistResult(ExistState.Conflict, full, null);
        }

        if (extraCount > 0)
            return new ExistResult(ExistState.Conflict, full, null);
        if (!any)
            return new ExistResult(ExistState.Missing, 0, null);
        return new ExistResult(ExistState.Complete, full, null);
    }

    public static ExistState Combine(IEnumerable<ExistState> states)
    {
        var any = false;
        var allComplete = true;
        var allMissing = true;
        var sawProgress = false;
        foreach (var state in states)
        {
            any = true;
            if (state == ExistState.Conflict)
                return ExistState.Conflict;
            if (state != ExistState.Complete)
                allComplete = false;
            if (state != ExistState.Missing)
                allMissing = false;
            if (state is ExistState.Partial or ExistState.Complete)
                sawProgress = true;
        }

        if (!any || allMissing)
            return ExistState.Missing;
        if (allComplete)
            return ExistState.Complete;
        return sawProgress || !allMissing ? ExistState.Partial : ExistState.Missing;
    }

    /// <summary>
    /// Picks a resume offset inside a partial copy. A manifest offset is trusted only
    /// when it falls inside bytes that are already on disk.
    /// </summary>
    public static ResumePoint Resolve(long total, long fullEnd, long? partialLength, long? manifestCommitted)
    {
        if (fullEnd >= total)
            return new ResumePoint(total, false);

        var diskEnd = fullEnd + (partialLength ?? 0);
        if (manifestCommitted is long committed && committed >= fullEnd && committed <= diskEnd && committed <= total)
            return new ResumePoint(committed, Truncate: partialLength is not null && committed != diskEnd);

        return new ResumePoint(fullEnd, Truncate: partialLength is > 0);
    }
}
