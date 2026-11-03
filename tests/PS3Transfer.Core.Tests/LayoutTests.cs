using Xunit;

namespace PS3Transfer.Core.Tests;

public class LayoutTests
{
    [Fact]
    public void Exist_rules_cover_missing_partial_complete_and_conflict()
    {
        var expected = new long[] { 1000, 1000, 200 };
        Assert.Equal(ExistState.Missing, ExistRules.Evaluate(expected, [null, null, null], 0).State);
        Assert.Equal(ExistState.Partial, ExistRules.Evaluate(expected, [1000L, null, null], 0).State);
        var partial = ExistRules.Evaluate(expected, [1000L, 400L, null], 0);
        Assert.Equal(ExistState.Partial, partial.State);
        Assert.Equal(1000, partial.FullEnd);
        Assert.Equal(400, partial.PartialLength);
        Assert.Equal(ExistState.Complete, ExistRules.Evaluate(expected, [1000L, 1000L, 200L], 0).State);
        Assert.Equal(ExistState.Conflict, ExistRules.Evaluate(expected, [1000L, 1000L, 200L], 1).State);
        Assert.Equal(ExistState.Conflict, ExistRules.Evaluate(expected, [null, 1000L, null], 0).State);
        Assert.Equal(ExistState.Conflict, ExistRules.Evaluate(expected, [1000L, 50L, 200L], 0).State);
    }

    [Fact]
    public void Resume_trusts_a_manifest_inside_the_partial_part()
    {
        var point = ExistRules.Resolve(2500, fullEnd: 1000, partialLength: 400, manifestCommitted: 1200);
        Assert.Equal(1200, point.Start);
        Assert.True(point.Truncate);

        var rewrite = ExistRules.Resolve(2500, fullEnd: 1000, partialLength: 400, manifestCommitted: null);
        Assert.Equal(1000, rewrite.Start);
        Assert.True(rewrite.Truncate);

        var done = ExistRules.Resolve(2500, fullEnd: 2500, partialLength: null, manifestCommitted: 2500);
        Assert.Equal(2500, done.Start);
    }

    [Fact]
    public void Split_names_follow_webman_and_multiman()
    {
        Assert.True(SplitNames.TryParse("Game.iso.0", out var isoBase, out var isoIndex, out var isoScheme));
        Assert.Equal("Game.iso", isoBase);
        Assert.Equal(0, isoIndex);
        Assert.Equal(SplitScheme.IsoDotNumber, isoScheme);

        Assert.True(SplitNames.TryParse("Game.pkg.66601", out var pkgBase, out var pkgIndex, out var pkgScheme));
        Assert.Equal("Game.pkg", pkgBase);
        Assert.Equal(66601, pkgIndex);
        Assert.Equal(SplitScheme.Pkg666, pkgScheme);

        Assert.Equal(@"E:\PS3ISO\Game.iso.0", Ps3Layout.PartPath(@"E:\PS3ISO\Game.iso", SplitScheme.IsoDotNumber, 0));
        Assert.Equal(@"E:\packages\Game.pkg.66600", Ps3Layout.PartPath(@"E:\packages\Game.pkg", SplitScheme.Pkg666, 0));
        Assert.Equal(2, Ps3Layout.PartCount(Ps3Layout.SplitSize + 1, Ps3Layout.SplitSize));
    }
}
