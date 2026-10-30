using BlastGb.Core;

namespace BlastGb.Core.Tests;

public sealed class GuardedPatchTests
{
    [Fact]
    public void GeneratedPatchesRoundTripEveryChangeBeyondDiffPreviewLimits()
    {
        var before = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        var original = before.ToArray();
        var after = before.ToArray();
        for (var index = 0; index < 100; index++) after[0x1000 + index * 2] ^= 0xff;
        for (var index = 0; index < 400; index++) after[0x2000 + index] ^= 0xff;
        after[^1] ^= 0xff;
        var patches = RomTools.CreatePatches(before, after);
        Assert.Equal(102, patches.Count);
        Assert.Equal(501, patches.Sum(patch => patch.Bytes.Length));
        Assert.Equal(400, patches[100].Bytes.Length);
        Assert.All(patches, patch => Assert.Equal(patch.Bytes.Length, patch.Expected!.Length));
        Assert.Equal(after, RomTools.Patch(before, patches));
        Assert.Equal(original, before);
        var wrongRevision = before.ToArray();
        wrongRevision[0x2000] ^= 0xff;
        Assert.Throws<ArgumentException>(() => RomTools.Patch(wrongRevision, patches));
    }

    [Fact]
    public void GeneratedPatchesAreIndependentAndRejectResizing()
    {
        var before = new byte[336];
        var after = before.ToArray();
        after[0] = 1;
        after[^1] = 2;
        var patches = RomTools.CreatePatches(before, after);
        before[0] = 9;
        after[0] = 9;
        Assert.Equal(new byte[] { 0 }, patches[0].Expected);
        Assert.Equal(new byte[] { 1 }, patches[0].Bytes);
        Assert.Equal(335, patches[1].Offset);
        Assert.Empty(RomTools.CreatePatches(before, before));
        Assert.Throws<ArgumentException>(() => RomTools.CreatePatches(before, new byte[337]));
    }
}
