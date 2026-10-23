using BlastGb.Core;

namespace BlastGb.Core.Tests;

public sealed class RomPatchesTests
{
    [Fact]
    public void ReproducesFragmentedChangesWithoutTruncatingOrSharingBuffers()
    {
        var before = Enumerable.Range(0, 3 * RomPatches.MaxPatchBytes + 17).Select(index => (byte)index).ToArray();
        var after = (byte[])before.Clone();
        for (var index = 7; index < after.Length; index += 37) after[index] ^= 0xff;
        after[^1] ^= 0xff;
        var originalBefore = (byte[])before.Clone();
        var originalAfter = (byte[])after.Clone();
        var patches = RomPatches.Create(before, after);
        Assert.Equal(4, patches.Count);
        Assert.All(patches, patch =>
        {
            Assert.InRange(patch.Bytes.Length, 1, RomPatches.MaxPatchBytes);
            Assert.Equal(patch.Bytes.Length, patch.Expected!.Length);
            Assert.Equal(before[patch.Offset..(patch.Offset + patch.Bytes.Length)], patch.Expected);
        });
        Assert.Equal(after, RomTools.Patch(before, patches));
        patches[0].Bytes[0] ^= 1;
        patches[0].Expected![0] ^= 1;
        Assert.Equal(originalBefore, before);
        Assert.Equal(originalAfter, after);
    }

    [Fact]
    public void MaximumRomAndPatchWindowSizesRoundTrip()
    {
        var before = new byte[RomTools.MaxRomSize];
        var after = Enumerable.Repeat((byte)255, before.Length).ToArray();
        var patches = RomPatches.Create(before, after);
        Assert.Equal(128, patches.Count);
        Assert.True(patches.Count <= RomPatches.MaxPatchCount);
        Assert.All(patches, patch => Assert.Equal(RomPatches.MaxPatchBytes, patch.Bytes.Length));
        Assert.Equal(after, RomTools.Patch(before, patches));
    }

    [Fact]
    public void IdenticalCartridgesProduceAnEmptyPatch()
    {
        var rom = new byte[0x150];
        Assert.Empty(RomPatches.Create(rom, rom));
    }

    [Fact]
    public void ResizingAndUnsupportedCartridgeSizesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => RomPatches.Create(new byte[32768], new byte[32769]));
        Assert.Throws<ArgumentException>(() => RomPatches.Create(new byte[0x14f], new byte[0x14f]));
        var oversized = new byte[RomTools.MaxRomSize + 1];
        Assert.Throws<ArgumentException>(() => RomPatches.Create(oversized, oversized));
    }

    [Fact]
    public void GeneratedGuardsRejectTheWrongOriginalWithoutChangingIt()
    {
        var before = new byte[131072];
        var after = (byte[])before.Clone();
        after[0x150] = 42;
        after[100000] = 7;
        var patches = RomPatches.Create(before, after);
        before[100000] = 8;
        var wrongOriginal = (byte[])before.Clone();
        Assert.Contains("expected bytes", Assert.Throws<ArgumentException>(() => RomTools.Patch(before, patches)).Message);
        Assert.Equal(wrongOriginal, before);
    }
}
