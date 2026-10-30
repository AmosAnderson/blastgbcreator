using System.Text;
using BlastGb.Core;

namespace BlastGb.Core.Tests;

public sealed class RomToolsTests
{
    private static byte[] Cartridge() => GameCompiler.Build(GameCompiler.CreateStarterSpec() with { Title = "TEST CART" }).Rom;

    [Fact]
    public void InspectsActualHeaderAndChecksums()
    {
        var metadata = RomTools.Inspect(Cartridge());
        Assert.Equal("TEST CART", metadata.Title);
        Assert.Equal("gbc", metadata.Mode);
        Assert.Equal("compatible", metadata.CgbCompatibility);
        Assert.Equal("ROM ONLY", metadata.CartridgeType);
        Assert.Equal(32768, metadata.Size);
        Assert.Equal(2, metadata.RomBanks);
        Assert.Equal(2, metadata.DeclaredRomBanks);
        Assert.Equal(0, metadata.RamBytes);
        Assert.True(metadata.HeaderChecksumValid && metadata.GlobalChecksumValid && metadata.LogoValid);
        Assert.Empty(metadata.Warnings);
    }

    [Fact]
    public void TitlesRespectDmgAndCgbHeaderFields()
    {
        var rom = Cartridge();
        Encoding.ASCII.GetBytes("123456789ABCDEFG").CopyTo(rom, 0x134);
        Assert.Equal("123456789ABCDEFG", RomTools.Inspect(rom).Title);
        Assert.Equal("gb", RomTools.Inspect(rom).Mode);
        rom[0x143] = 0xc0;
        Assert.Equal("123456789ABCDEF", RomTools.Inspect(rom).Title);
        Assert.Equal("required", RomTools.Inspect(rom).CgbCompatibility);
        rom[0x143] = 0x81;
        Assert.Contains(RomTools.Inspect(rom).Warnings, warning => warning.Contains("Nonstandard CGB", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportsFullMbc5CapacityAndRam()
    {
        var rom = new byte[RomTools.MaxRomSize];
        Cartridge().CopyTo(rom, 0);
        rom[0x147] = 0x1b;
        rom[0x148] = 8;
        rom[0x149] = 4;
        var metadata = RomTools.Inspect(RomTools.RepairChecksums(rom));
        Assert.Equal(512, metadata.RomBanks);
        Assert.Equal(512, metadata.DeclaredRomBanks);
        Assert.Equal("MBC5+RAM+BATTERY", metadata.CartridgeType);
        Assert.Equal(131072, metadata.RamBytes);
        Assert.Empty(metadata.Warnings);
    }

    [Fact]
    public void ReportsCorruptionAndUnknownOrHistoricalHeaders()
    {
        var rom = Cartridge()[..32000];
        rom[0x104] ^= 1;
        rom[0x147] = 0xab;
        rom[0x148] = 0x55;
        rom[0x149] = 0xff;
        var metadata = RomTools.Inspect(rom);
        Assert.Equal(2, metadata.RomBanks);
        Assert.Null(metadata.DeclaredRomBanks);
        Assert.Equal("UNKNOWN ($ab)", metadata.CartridgeType);
        Assert.False(metadata.HeaderChecksumValid || metadata.GlobalChecksumValid || metadata.LogoValid);
        Assert.Matches("incomplete.*Unknown ROM.*Unknown cartridge.*Unknown RAM.*header checksum.*global checksum.*logo", string.Join(' ', metadata.Warnings));
        rom[0x148] = 0x52;
        Assert.Equal(72, RomTools.Inspect(rom).DeclaredRomBanks);
        Assert.Contains("unofficial", string.Join(' ', RomTools.Inspect(rom).Warnings));
        rom[0x147] = 6;
        rom[0x149] = 0;
        Assert.Equal(512, RomTools.Inspect(rom).RamBytes);
        Assert.Contains("four-bit", string.Join(' ', RomTools.Inspect(rom).Warnings));
    }

    [Fact]
    public void EnforcesRomSizeLimits()
    {
        Assert.Throws<ArgumentException>(() => RomTools.Inspect(new byte[0x14f]));
        Assert.Throws<ArgumentException>(() => RomTools.Inspect(new byte[RomTools.MaxRomSize + 1]));
        Assert.Throws<ArgumentException>(() => RomTools.RepairChecksums(new byte[0x14f]));
        Assert.Throws<ArgumentException>(() => RomTools.ExportDisassembly(new byte[0x14f]));
    }

    [Fact]
    public void DecodesEveryBaseOpcodeWithIndependentWidths()
    {
        var widths = "1311112131111121" + "2311112121111121" + "2311112121111121" + "2311112121111121" +
            new string('1', 8 * 16) + "1133312111323321" + "1131312111313121" + "2111112121311121" + "2111112121311121";
        var invalid = new[] { 0xd3, 0xdb, 0xdd, 0xe3, 0xe4, 0xeb, 0xec, 0xed, 0xf4, 0xfc, 0xfd };
        Assert.Equal(256, widths.Length);
        for (var opcode = 0; opcode < 256; opcode++)
        {
            var instruction = Assert.Single(RomTools.Disassemble([(byte)opcode, 0x12, 0x34], count: 1));
            Assert.Equal(widths[opcode] - '0', instruction.Bytes.Length);
            Assert.Equal(invalid.Contains(opcode), instruction.Text.Contains("invalid opcode", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DecodesAll256CbInstructions()
    {
        var registers = new[] { "b", "c", "d", "e", "h", "l", "[hl]", "a" };
        var operations = new[] { "rlc", "rrc", "rl", "rr", "sla", "sra", "swap", "srl" };
        for (var opcode = 0; opcode < 256; opcode++)
        {
            var bit = (opcode >> 3) & 7;
            var register = registers[opcode & 7];
            var expected = opcode < 0x40 ? $"{operations[bit]} {register}" : $"{(opcode < 0x80 ? "bit" : opcode < 0xc0 ? "res" : "set")} {bit}, {register}";
            var instruction = Assert.Single(RomTools.Disassemble([0xcb, (byte)opcode], count: 1));
            Assert.Equal(expected, instruction.Text);
            Assert.Equal(new byte[] { 0xcb, (byte)opcode }, instruction.Bytes);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x34, 0x12 }, "ld bc, $1234")]
    [InlineData(new byte[] { 0x08, 0x34, 0x12 }, "ld [$1234], sp")]
    [InlineData(new byte[] { 0x09 }, "add hl, bc")]
    [InlineData(new byte[] { 0x10, 0 }, "stop")]
    [InlineData(new byte[] { 0x10, 1 }, "stop ; padding $01 (nonzero)")]
    [InlineData(new byte[] { 0x22 }, "ld [hl+], a")]
    [InlineData(new byte[] { 0x3a }, "ld a, [hl-]")]
    [InlineData(new byte[] { 0x3e, 0x12 }, "ld a, $12")]
    [InlineData(new byte[] { 0x36, 0x12 }, "ld [hl], $12")]
    [InlineData(new byte[] { 0x76 }, "halt")]
    [InlineData(new byte[] { 0x7e }, "ld a, [hl]")]
    [InlineData(new byte[] { 0x86 }, "add a, [hl]")]
    [InlineData(new byte[] { 0x97 }, "sub a")]
    [InlineData(new byte[] { 0xae }, "xor [hl]")]
    [InlineData(new byte[] { 0xc2, 0x34, 0x12 }, "jp nz, $1234")]
    [InlineData(new byte[] { 0xdc, 0x34, 0x12 }, "call c, $1234")]
    [InlineData(new byte[] { 0xd9 }, "reti")]
    [InlineData(new byte[] { 0xe0, 0x42 }, "ldh [$ff42], a")]
    [InlineData(new byte[] { 0xe2 }, "ldh [c], a")]
    [InlineData(new byte[] { 0xe8, 0x80 }, "add sp, -$80")]
    [InlineData(new byte[] { 0xe9 }, "jp hl")]
    [InlineData(new byte[] { 0xea, 0x34, 0x12 }, "ld [$1234], a")]
    [InlineData(new byte[] { 0xf0, 0x40 }, "ldh a, [$ff40]")]
    [InlineData(new byte[] { 0xf1 }, "pop af")]
    [InlineData(new byte[] { 0xf3 }, "di")]
    [InlineData(new byte[] { 0xf8, 0xfe }, "ld hl, sp-$02")]
    [InlineData(new byte[] { 0xfb }, "ei")]
    [InlineData(new byte[] { 0xff }, "rst $38")]
    public void DecodesDocumentedExamples(byte[] bytes, string text) => Assert.Equal(text, RomTools.Disassemble(bytes, count: 1)[0].Text);

    [Fact]
    public void RelativeTargetsRespectMappedBankWindowAndSixteenBitWrapping()
    {
        var rom = new byte[3 * RomTools.RomBankSize];
        new byte[] { 0x18, 0xfe, 0x20, 0x80 }.CopyTo(rom, 0x8000);
        var instructions = RomTools.Disassemble(rom, 0x8000, 2);
        Assert.Equal(2, instructions[0].Bank);
        Assert.Equal(0x4000, instructions[0].Address);
        Assert.Equal("jr $4000", instructions[0].Text);
        Assert.Equal("jr nz, $3f84", instructions[1].Text);
        new byte[] { 0x18, 0x7f }.CopyTo(rom, 0xbffc);
        Assert.Equal("jr $807d", RomTools.Disassemble(rom, 0xbffc, 1)[0].Text);
        Assert.Equal("jr $ff82", RomTools.Disassemble([0x18, 0x80], count: 1)[0].Text);
    }

    [Fact]
    public void NeverConsumesOperandsAcrossBankOrFileBoundaries()
    {
        var rom = new byte[3 * RomTools.RomBankSize];
        rom[0x7fff] = 0xc3;
        new byte[] { 0x3e, 0x42 }.CopyTo(rom, 0x8000);
        var decoded = RomTools.Disassemble(rom, 0x7fff, 2);
        Assert.Equal(new byte[] { 0xc3 }, decoded[0].Bytes);
        Assert.Contains("bank boundary", decoded[0].Text);
        Assert.Equal("ld a, $42", decoded[1].Text);
        Assert.Equal(2, decoded[1].Bank);
        Assert.Contains("end of ROM", RomTools.Disassemble([0x01, 0x12])[0].Text);
        Assert.Equal(new byte[] { 0xcb }, RomTools.Disassemble([0xcb])[0].Bytes);
    }

    [Fact]
    public void BoundsDecodeWindow()
    {
        var rom = new byte[100];
        Assert.Equal(32, RomTools.Disassemble(rom).Count);
        Assert.Single(RomTools.Disassemble(rom, 99, 10));
        Assert.Empty(RomTools.Disassemble(rom, 100));
        Assert.Empty(RomTools.Disassemble(rom, count: 0));
        Assert.Empty(RomTools.Disassemble([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RomTools.Disassemble(rom, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RomTools.Disassemble(rom, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => RomTools.Disassemble(rom, count: -1));
    }

    [Fact]
    public void ChecksumRepairUsesHeaderThenGlobalAndPreservesInput()
    {
        var rom = new byte[32768];
        rom[0x14e] = 0xaa;
        rom[0x14f] = 0xbb;
        var original = (byte[])rom.Clone();
        var repaired = RomTools.RepairChecksums(rom);
        Assert.Equal(new byte[] { 0xe7, 0, 0xe7 }, repaired[0x14d..0x150]);
        Assert.Equal(original, rom);
    }

    [Fact]
    public void GuardedPatchesPreserveInputAndOnlyRepairWhenRequested()
    {
        var rom = Cartridge();
        var original = (byte[])rom.Clone();
        var patched = RomTools.Patch(rom, [new RomPatch(0x4000, [0x42], [rom[0x4000]])]);
        Assert.Equal(0x42, patched[0x4000]);
        Assert.Equal(original, rom);
        Assert.Equal(rom[0x14d..0x150], patched[0x14d..0x150]);
        Assert.False(RomTools.Inspect(patched).GlobalChecksumValid);
        var repaired = RomTools.Patch(rom, [new RomPatch(0x134, [0x42])], fixChecksums: true);
        Assert.True(RomTools.Inspect(repaired).HeaderChecksumValid);
        Assert.True(RomTools.Inspect(repaired).GlobalChecksumValid);
    }

    [Fact]
    public void InvalidPatchesFailAtomically()
    {
        var rom = Cartridge();
        var original = (byte[])rom.Clone();
        var guardError = Assert.Throws<ArgumentException>(() => RomTools.Patch(rom, [new(0x200, [0x42]), new(0x300, [0], [(byte)(rom[0x300] ^ 1)])]));
        Assert.Contains("expected bytes do not match", guardError.Message);
        Assert.Contains("overlap", Assert.Throws<ArgumentException>(() => RomTools.Patch(rom, [new(0x200, [1, 2]), new(0x201, [3])])).Message);
        foreach (var patch in new[] { new RomPatch(-1, [1]), new RomPatch(int.MaxValue, [1]), new RomPatch(rom.Length, [1]), new RomPatch(0, []), new RomPatch(0, [1], []) })
            Assert.Throws<ArgumentException>(() => RomTools.Patch(rom, [patch]));
        Assert.Equal(original, rom);
    }

    [Fact]
    public void ExportPreservesEveryByteInMultipleAndPartialBanks()
    {
        var rom = Enumerable.Range(0, 8 * RomTools.RomBankSize + 3).Select(index => (byte)((index * 7 + index / RomTools.RomBankSize) & 255)).ToArray();
        var source = RomTools.ExportDisassembly(rom);
        Assert.Contains("ROM0[$0000]", source);
        Assert.Contains("ROMX[$4000], BANK[8]", source);
        Assert.Contains("code/data and mapper state are unknown", source);
        Assert.Contains("not recovered source code", source);
        Assert.Contains($"original {rom.Length}-byte length", source);
        Assert.Contains("linker map proves removed bytes are padding", source);
        Assert.Contains("gb project import", source);
        Assert.DoesNotContain("p.read_bytes()", source);
        var bytes = source.Split('\n').Where(line => line.StartsWith("    db ", StringComparison.Ordinal))
            .SelectMany(line => line[7..line.IndexOf(';', StringComparison.Ordinal)].Split(',').Select(value => Convert.ToByte(value.Trim()[1..], 16))).ToArray();
        Assert.Equal(rom, bytes);
        Assert.True(source.Length < rom.Length * 8);
    }

    [RgbdsFact]
    public void RgbdsRebuildsAllEightExportedBanks()
    {
        using var rgbds = new RgbdsFixture();
        var rom = Enumerable.Range(0, 8 * RomTools.RomBankSize).Select(index => (byte)((index * 13) & 255)).ToArray();
        Assert.Equal(rom, rgbds.Assemble(RomTools.ExportDisassembly(rom)));
    }

    [RgbdsFact]
    public void RgbdsIndependentlyReassemblesAll512DecodedOpcodes()
    {
        using var rgbds = new RgbdsFixture();
        var rom = new byte[32768];
        var source = new StringBuilder("SECTION \"Instructions\", ROM0[$0000]\n");
        var offset = 0;
        for (var opcode = 0; opcode < 512; opcode++)
        {
            rom[offset] = opcode < 256 ? (byte)opcode : (byte)0xcb;
            rom[offset + 1] = opcode < 256 ? (byte)0 : (byte)(opcode - 256);
            rom[offset + 2] = 0;
            var instruction = RomTools.Disassemble(rom, offset, 1)[0];
            source.Append("    ").Append(instruction.Text).Append('\n');
            offset += instruction.Bytes.Length;
        }
        source.Append("    ds $4000 - @, $00\nSECTION \"Second bank\", ROMX[$4000], BANK[1]\n    ds $4000, $00\n");
        Assert.Equal(rom, rgbds.Assemble(source.ToString()));
    }
}
