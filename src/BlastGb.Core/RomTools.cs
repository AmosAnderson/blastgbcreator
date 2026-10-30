using System.Text;

namespace BlastGb.Core;

/// <summary>Pure cartridge operations. Returned buffers never share storage with the input.</summary>
public static class RomTools
{
    public const int RomBankSize = 0x4000;
    public const int MaxRomSize = 8 * 1024 * 1024;

    internal static ReadOnlySpan<byte> Logo =>
    [
        0xce, 0xed, 0x66, 0x66, 0xcc, 0x0d, 0x00, 0x0b, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0c, 0x00, 0x0d,
        0x00, 0x08, 0x11, 0x1f, 0x88, 0x89, 0x00, 0x0e, 0xdc, 0xcc, 0x6e, 0xe6, 0xdd, 0xdd, 0xd9, 0x99,
        0xbb, 0xbb, 0x67, 0x63, 0x6e, 0x0e, 0xec, 0xcc, 0xdd, 0xdc, 0x99, 0x9f, 0xbb, 0xb9, 0x33, 0x3e
    ];

    private static readonly IReadOnlyDictionary<byte, string> CartridgeTypes = new Dictionary<byte, string>
    {
        [0x00] = "ROM ONLY",
        [0x01] = "MBC1",
        [0x02] = "MBC1+RAM",
        [0x03] = "MBC1+RAM+BATTERY",
        [0x05] = "MBC2",
        [0x06] = "MBC2+BATTERY",
        [0x08] = "ROM+RAM",
        [0x09] = "ROM+RAM+BATTERY",
        [0x0b] = "MMM01",
        [0x0c] = "MMM01+RAM",
        [0x0d] = "MMM01+RAM+BATTERY",
        [0x0f] = "MBC3+TIMER+BATTERY",
        [0x10] = "MBC3+TIMER+RAM+BATTERY",
        [0x11] = "MBC3",
        [0x12] = "MBC3+RAM",
        [0x13] = "MBC3+RAM+BATTERY",
        [0x19] = "MBC5",
        [0x1a] = "MBC5+RAM",
        [0x1b] = "MBC5+RAM+BATTERY",
        [0x1c] = "MBC5+RUMBLE",
        [0x1d] = "MBC5+RUMBLE+RAM",
        [0x1e] = "MBC5+RUMBLE+RAM+BATTERY",
        [0x20] = "MBC6",
        [0x22] = "MBC7+SENSOR+RUMBLE+RAM+BATTERY",
        [0xfc] = "POCKET CAMERA",
        [0xfd] = "BANDAI TAMA5",
        [0xfe] = "HuC3",
        [0xff] = "HuC1+RAM+BATTERY"
    };
    private static readonly int[] RamSizes = [0, 2048, 8192, 32768, 131072, 65536];
    private static readonly string[] R8 = ["b", "c", "d", "e", "h", "l", "[hl]", "a"];
    private static readonly string[] R16 = ["bc", "de", "hl", "sp"];
    private static readonly string[] Stack = ["bc", "de", "hl", "af"];
    private static readonly string[] Memory = ["bc", "de", "hl+", "hl-"];
    private static readonly string[] Conditions = ["nz", "z", "nc", "c"];
    private static readonly string[] Alu = ["add a, ", "adc a, ", "sub ", "sbc a, ", "and ", "xor ", "or ", "cp "];
    private static readonly string[] Rotate = ["rlc", "rrc", "rl", "rr", "sla", "sra", "swap", "srl"];
    private static bool Invalid(byte opcode) => opcode is 0xd3 or 0xdb or 0xdd or 0xe3 or 0xe4 or 0xeb or 0xec or 0xed or 0xf4 or 0xfc or 0xfd;
    private static string Hex(int value, int width = 2) => "$" + value.ToString($"x{width}", System.Globalization.CultureInfo.InvariantCulture);
    private static int Signed(byte value) => (sbyte)value;
    private static string SignedText(byte value) => (Signed(value) < 0 ? "-" : "+") + Hex(Math.Abs(Signed(value)));

    private static void CheckRom(ReadOnlySpan<byte> rom, bool requireHeader = true)
    {
        if (rom.Length > MaxRomSize) throw new ArgumentException("ROM exceeds the supported 8 MiB cartridge size.", nameof(rom));
        if (requireHeader && rom.Length < 0x150)
            throw new ArgumentException("ROM is too small to contain a complete Game Boy cartridge header (336 bytes).", nameof(rom));
    }

    private static byte HeaderChecksum(ReadOnlySpan<byte> rom)
    {
        var checksum = 0;
        for (var offset = 0x134; offset <= 0x14c; offset++) checksum = (checksum - rom[offset] - 1) & 0xff;
        return (byte)checksum;
    }

    private static ushort GlobalChecksum(ReadOnlySpan<byte> rom)
    {
        var checksum = 0;
        for (var offset = 0; offset < rom.Length; offset++)
            if (offset is not (0x14e or 0x14f)) checksum = (checksum + rom[offset]) & 0xffff;
        return (ushort)checksum;
    }

    public static RomMetadata Inspect(ReadOnlySpan<byte> rom)
    {
        CheckRom(rom);
        var cgb = (rom[0x143] & 0x80) != 0;
        // Preserve the full title field; the manufacturer's four-byte split is ambiguous.
        var titleBytes = rom[0x134..(cgb ? 0x143 : 0x144)];
        var titleEnd = titleBytes.IndexOf((byte)0);
        if (titleEnd >= 0) titleBytes = titleBytes[..titleEnd];
        var title = new string(titleBytes.ToArray().Select(b => b is >= 0x20 and <= 0x7e ? (char)b : '?').ToArray()).Trim();
        var code = rom[0x148];
        int? declaredBanks = code <= 8 ? 2 << code : code switch { 0x52 => 72, 0x53 => 80, 0x54 => 96, _ => null };
        var knownCartridge = CartridgeTypes.TryGetValue(rom[0x147], out var cartridgeType);
        cartridgeType ??= $"UNKNOWN ({Hex(rom[0x147])})";
        var isMbc2 = rom[0x147] is 5 or 6;
        var knownRam = rom[0x149] < RamSizes.Length;
        var ramBytes = isMbc2 ? 512 : knownRam ? RamSizes[rom[0x149]] : 0;
        var headerValid = HeaderChecksum(rom) == rom[0x14d];
        var globalValid = GlobalChecksum(rom) == ((rom[0x14e] << 8) | rom[0x14f]);
        var logoValid = rom.Slice(0x104, Logo.Length).SequenceEqual(Logo);
        var warnings = new List<string>();
        if (title.Length == 0) warnings.Add("The cartridge title is empty.");
        if (rom.Length % RomBankSize != 0) warnings.Add("The ROM ends with an incomplete 16 KiB bank.");
        if (declaredBanks is null) warnings.Add($"Unknown ROM size code {Hex(code)}.");
        else if (declaredBanks * RomBankSize != rom.Length)
            warnings.Add($"Header declares {declaredBanks} ROM banks ({declaredBanks * RomBankSize} bytes), but the file contains {rom.Length} bytes.");
        if (code is >= 0x52 and <= 0x54) warnings.Add("The header uses an unofficial legacy ROM size code.");
        if (!knownCartridge) warnings.Add($"Unknown cartridge type {Hex(rom[0x147])}.");
        if (!knownRam) warnings.Add($"Unknown RAM size code {Hex(rom[0x149])}.");
        if (rom[0x149] == 1) warnings.Add("RAM size code $01 is unofficial; displayed as a legacy 2 KiB size.");
        if (isMbc2) warnings.Add("MBC2 contains 512 four-bit RAM cells; RAM bytes show addressable cells.");
        if (!headerValid) warnings.Add("The header checksum does not match.");
        if (!globalValid) warnings.Add("The global checksum does not match.");
        if (!logoValid) warnings.Add("The Nintendo logo differs from the standard boot logo.");
        if (cgb && rom[0x143] is not (0x80 or 0xc0)) warnings.Add($"Nonstandard CGB flag {Hex(rom[0x143])}; color mode inferred from bit 7.");
        return new RomMetadata(title.Length == 0 ? "Untitled ROM" : title, cgb ? "gbc" : "gb",
            !cgb ? "none" : (rom[0x143] & 0x40) != 0 ? "required" : "compatible", cartridgeType,
            rom.Length, (rom.Length + RomBankSize - 1) / RomBankSize, declaredBanks, ramBytes,
            headerValid, globalValid, logoValid, warnings.AsReadOnly());
    }

    private static int InstructionLength(byte opcode)
    {
        if (opcode is 0xcb or 0x10) return 2;
        if (opcode < 0x40)
        {
            if ((opcode & 0x0f) == 1 || opcode == 8) return 3;
            return (opcode & 7) == 6 || ((opcode & 7) == 0 && opcode >= 0x18) ? 2 : 1;
        }
        if (opcode < 0xc0 || Invalid(opcode)) return 1;
        if ((opcode & 0xe7) is 0xc2 or 0xc4 || opcode is 0xc3 or 0xcd or 0xea or 0xfa) return 3;
        return (opcode & 7) == 6 || opcode is 0xe0 or 0xe8 or 0xf0 or 0xf8 ? 2 : 1;
    }

    private static Instruction Decode(ReadOnlySpan<byte> rom, int offset)
    {
        var bank = offset / RomBankSize;
        var address = offset % RomBankSize + (bank == 0 ? 0 : 0x4000);
        var opcode = rom[offset];
        var length = InstructionLength(opcode);
        var available = Math.Min(rom.Length - offset, RomBankSize - offset % RomBankSize);
        var bytes = rom.Slice(offset, Math.Min(length, available)).ToArray();
        Instruction Result(string text) => new(offset, address, bank, bytes, text);
        if (available < length)
            return Result($"db {string.Join(", ", bytes.Select(b => Hex(b)))} ; truncated instruction at {(offset + available == rom.Length ? "end of ROM" : "bank boundary")}");
        if (Invalid(opcode)) return Result($"db {Hex(opcode)} ; invalid opcode (CPU lock)");
        var n = bytes.Length > 1 ? bytes[1] : (byte)0;
        var word = n | ((bytes.Length > 2 ? bytes[2] : 0) << 8);
        var x = opcode >> 6;
        var y = (opcode >> 3) & 7;
        var z = opcode & 7;
        var p = y >> 1;
        var q = y & 1;
        var relative = Hex((address + 2 + Signed(n)) & 0xffff, 4);
        if (opcode == 0xcb)
        {
            var group = n >> 6;
            var bit = (n >> 3) & 7;
            var register = R8[n & 7];
            return Result(group == 0 ? $"{Rotate[bit]} {register}" : $"{new[] { "", "bit", "res", "set" }[group]} {bit}, {register}");
        }
        if (x == 0)
            return Result(z switch
            {
                0 => y switch
                {
                    0 => "nop",
                    1 => $"ld [{Hex(word, 4)}], sp",
                    2 => n == 0 ? "stop" : $"stop ; padding {Hex(n)} (nonzero)",
                    3 => $"jr {relative}",
                    _ => $"jr {Conditions[y - 4]}, {relative}"
                },
                1 => q == 0 ? $"ld {R16[p]}, {Hex(word, 4)}" : $"add hl, {R16[p]}",
                2 => q == 0 ? $"ld [{Memory[p]}], a" : $"ld a, [{Memory[p]}]",
                3 => $"{(q == 0 ? "inc" : "dec")} {R16[p]}",
                4 => $"inc {R8[y]}",
                5 => $"dec {R8[y]}",
                6 => $"ld {R8[y]}, {Hex(n)}",
                _ => new[] { "rlca", "rrca", "rla", "rra", "daa", "cpl", "scf", "ccf" }[y]
            });
        if (x == 1) return Result(opcode == 0x76 ? "halt" : $"ld {R8[y]}, {R8[z]}");
        if (x == 2) return Result(Alu[y] + R8[z]);
        return Result(z switch
        {
            0 => y switch
            {
                < 4 => $"ret {Conditions[y]}",
                4 => $"ldh [{Hex(0xff00 + n, 4)}], a",
                5 => $"add sp, {SignedText(n)}",
                6 => $"ldh a, [{Hex(0xff00 + n, 4)}]",
                _ => $"ld hl, sp{SignedText(n)}"
            },
            1 => q == 0 ? $"pop {Stack[p]}" : new[] { "ret", "reti", "jp hl", "ld sp, hl" }[p],
            2 => y < 4 ? $"jp {Conditions[y]}, {Hex(word, 4)}" : new[] { "ldh [c], a", $"ld [{Hex(word, 4)}], a", "ldh a, [c]", $"ld a, [{Hex(word, 4)}]" }[y - 4],
            3 => y == 0 ? $"jp {Hex(word, 4)}" : y == 6 ? "di" : "ei",
            4 => $"call {Conditions[y]}, {Hex(word, 4)}",
            5 => q == 0 ? $"push {Stack[p]}" : $"call {Hex(word, 4)}",
            6 => Alu[y] + Hex(n),
            _ => $"rst {Hex(y * 8)}"
        });
    }

    /// <summary>Linearly decode a ROM window. Count is instructions, not bytes; code/data is unknown.</summary>
    public static IReadOnlyList<Instruction> Disassemble(ReadOnlySpan<byte> rom, int offset = 0, int count = 32)
    {
        CheckRom(rom, false);
        if (offset < 0 || offset > rom.Length) throw new ArgumentOutOfRangeException(nameof(offset), "Disassembly offset must be within the ROM.");
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var instructions = new List<Instruction>();
        for (var cursor = offset; cursor < rom.Length && instructions.Count < count;)
        {
            var instruction = Decode(rom, cursor);
            instructions.Add(instruction);
            cursor += instruction.Bytes.Length;
        }
        return instructions.AsReadOnly();
    }

    public static byte[] RepairChecksums(ReadOnlySpan<byte> rom)
    {
        CheckRom(rom);
        var repaired = rom.ToArray();
        repaired[0x14d] = HeaderChecksum(repaired);
        var checksum = GlobalChecksum(repaired);
        repaired[0x14e] = (byte)(checksum >> 8);
        repaired[0x14f] = (byte)checksum;
        return repaired;
    }

    /// <summary>Create complete, guarded contiguous changes between equal-length cartridges.</summary>
    public static IReadOnlyList<RomPatch> CreatePatches(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        CheckRom(before);
        CheckRom(after);
        if (before.Length != after.Length)
            throw new ArgumentException("Guarded byte patches require equal-length ROMs; resizing must be handled explicitly.", nameof(after));
        var patches = new List<RomPatch>();
        var offset = 0;
        while (offset < before.Length)
        {
            if (before[offset] == after[offset]) { offset++; continue; }
            var start = offset;
            while (offset < before.Length && before[offset] != after[offset]) offset++;
            patches.Add(new RomPatch(start, after[start..offset].ToArray(), before[start..offset].ToArray()));
        }
        return patches.AsReadOnly();
    }

    /// <summary>Validate every range against the original before applying any change to a copy.</summary>
    public static byte[] Patch(ReadOnlySpan<byte> rom, IReadOnlyList<RomPatch> patches, bool fixChecksums = false)
    {
        CheckRom(rom);
        ArgumentNullException.ThrowIfNull(patches);
        foreach (var patch in patches)
        {
            if (patch is null || patch.Offset < 0) throw new ArgumentException("Patch offset must be a nonnegative integer.", nameof(patches));
            if (patch.Bytes is null || patch.Bytes.Length == 0) throw new ArgumentException("Patch bytes must be a nonempty array of bytes.", nameof(patches));
            if (patch.Offset > rom.Length - patch.Bytes.Length) throw new ArgumentException("Patch extends beyond the ROM.", nameof(patches));
            if (patch.Expected is not null)
            {
                if (patch.Expected.Length != patch.Bytes.Length) throw new ArgumentException("Expected bytes must have the same length as patch bytes.", nameof(patches));
                if (!rom.Slice(patch.Offset, patch.Bytes.Length).SequenceEqual(patch.Expected))
                    throw new ArgumentException($"Patch expected bytes do not match at ROM offset {Hex(patch.Offset, 6)}.", nameof(patches));
            }
        }
        var sorted = patches.OrderBy(patch => patch.Offset).ToArray();
        for (var index = 1; index < sorted.Length; index++)
            if (sorted[index].Offset < sorted[index - 1].Offset + sorted[index - 1].Bytes.Length)
                throw new ArgumentException("Patch ranges overlap; combine them into a single unambiguous patch.", nameof(patches));
        var patched = rom.ToArray();
        foreach (var patch in sorted) patch.Bytes.CopyTo(patched, patch.Offset);
        return fixChecksums ? RepairChecksums(patched) : patched;
    }

    /// <summary>Export authoritative bytes with candidate decodes in comments; never recovered source.</summary>
    public static string ExportDisassembly(ReadOnlySpan<byte> rom)
    {
        CheckRom(rom);
        var source = new StringBuilder();
        source.Append("; Game Boy cartridge: byte-preserving RGBDS data export\n")
            .Append("; Rebuild: rgbasm -o game.o main.asm\n;          rgblink -o rebuilt.gb game.o\n")
            .Append("; Byte data is authoritative. Comments are candidate decodes at each row start.\n")
            .Append("; Row boundaries may bisect real instructions; code/data and mapper state are unknown.\n")
            .Append("; This is not recovered source code. Header and checksum bytes are preserved.\n");
        if (rom.Length < 2 * RomBankSize || rom.Length % RomBankSize != 0)
            source.Append($"; To preserve the original {rom.Length}-byte length, use gb project import ORIGINAL_ROM PROJECT,\n")
                .Append("; replace PROJECT/source/main.asm with this source, then run gb build PROJECT.\n")
                .Append("; The project's romSize is enforced only after the linker map proves removed bytes are padding.\n")
                .Append("; Do not truncate rebuilt ROMs without checking section bounds.\n");
        for (var bank = 0; bank * RomBankSize < rom.Length; bank++)
        {
            source.Append('\n').Append(bank == 0 ? "SECTION \"ROM bank 0\", ROM0[$0000]\n" : $"SECTION \"ROM bank {bank}\", ROMX[$4000], BANK[{bank}]\n");
            var end = Math.Min((bank + 1) * RomBankSize, rom.Length);
            for (var offset = bank * RomBankSize; offset < end; offset += 16)
            {
                var instruction = Decode(rom, offset);
                source.Append("    db ");
                for (var cursor = offset; cursor < Math.Min(offset + 16, end); cursor++)
                {
                    if (cursor != offset) source.Append(',');
                    source.Append(Hex(rom[cursor]));
                }
                source.Append($" ; {Hex(instruction.Address, 4)}: {instruction.Text}\n");
            }
        }
        return source.ToString();
    }
}
