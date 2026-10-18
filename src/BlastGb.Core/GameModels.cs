namespace BlastGb.Core;

/// <summary>The five editable colors used by the built-in collection game.</summary>
public sealed record GamePalette(string Background, string Ground, string Wall, string Player, string Collectible);

/// <summary>Validated data for the original collection-game engine; it is not executable source.</summary>
public sealed record GameSpec(string Title, string Mode, GamePalette Palette, string[] Map, string Objective);

public sealed record RomBuild(byte[] Rom, string Source, string[] Warnings, string Title, string Filename);
