namespace BlastGb.Core;

/// <summary>Creates complete version-1 guarded patches without changing either input.</summary>
public static class RomPatches
{
    public const int MaxPatchBytes = 65536;
    public const int MaxPatchCount = 65536;

    /// <summary>
    /// Compare equal-sized cartridges in bounded windows. A patch may guard unchanged
    /// bytes between changes in its window, keeping even fragmented differences bounded.
    /// Applying the result without checksum repair reproduces the after image exactly.
    /// </summary>
    public static IReadOnlyList<RomPatch> Create(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        if (before.Length != after.Length)
            throw new ArgumentException("Guarded patch creation requires equal-sized ROMs; resizing is not supported.", nameof(after));
        if (before.Length is < 0x150 or > RomTools.MaxRomSize)
            throw new ArgumentException("ROMs must contain between 336 bytes and 8 MiB.", nameof(before));
        var patches = new List<RomPatch>();
        for (var offset = 0; offset < before.Length; offset += MaxPatchBytes)
        {
            var end = Math.Min(offset + MaxPatchBytes, before.Length);
            var first = offset;
            while (first < end && before[first] == after[first]) first++;
            if (first == end) continue;
            var last = end - 1;
            while (before[last] == after[last]) last--;
            if (patches.Count == MaxPatchCount)
                throw new ArgumentException("The changes exceed the version-1 patch record limit.", nameof(after));
            patches.Add(new RomPatch(first, after[first..(last + 1)].ToArray(), before[first..(last + 1)].ToArray()));
        }
        return patches.AsReadOnly();
    }
}
