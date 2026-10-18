using System.Text.Json.Serialization;

namespace BlastGb.Core;

/// <summary>Cartridge header metadata and validation evidence from the actual bytes.</summary>
public sealed record RomMetadata(
    string Title, string Mode, string CgbCompatibility, string CartridgeType,
    int Size, int RomBanks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DeclaredRomBanks,
    int RamBytes,
    bool HeaderChecksumValid, bool GlobalChecksumValid, bool LogoValid,
    IReadOnlyList<string> Warnings);

/// <summary>A candidate instruction at a physical ROM offset and its usual CPU bank window.</summary>
public sealed record Instruction(int Offset, int Address, int Bank, byte[] Bytes, string Text);

/// <summary>A nonempty replacement at a physical file offset, optionally guarded by original bytes.</summary>
public sealed record RomPatch(int Offset, byte[] Bytes, byte[]? Expected = null);
