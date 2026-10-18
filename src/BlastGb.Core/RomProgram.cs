namespace BlastGb.Core;

/// <summary>Original game engine, tile graphics, and palette emission.</summary>
internal static class RomProgram
{
    private static int[] Tile(params string[] rows)
    {
        var bytes = new List<int>(16);
        foreach (var row in rows)
        {
            var low = 0;
            var high = 0;
            foreach (var pixel in row)
            {
                low = low * 2 + ((pixel - '0') & 1);
                high = high * 2 + (((pixel - '0') >> 1) & 1);
            }
            bytes.Add(low);
            bytes.Add(high);
        }
        return bytes.ToArray();
    }

    private static readonly string[][] Font =
    [
        ["111", "101", "101", "101", "111"],
        ["010", "110", "010", "010", "111"],
        ["111", "001", "111", "100", "111"],
        ["111", "001", "111", "001", "111"],
        ["101", "101", "111", "001", "001"],
        ["111", "100", "111", "001", "111"],
        ["111", "100", "111", "101", "111"],
        ["111", "001", "010", "010", "010"],
        ["111", "101", "111", "101", "111"],
        ["111", "101", "111", "001", "111"],
        ["101", "101", "010", "010", "010"],
        ["111", "101", "101", "101", "111"],
        ["101", "101", "101", "101", "111"],
        ["101", "101", "111", "111", "101"],
        ["111", "010", "010", "010", "111"],
        ["101", "111", "111", "111", "101"],
        ["000", "000", "000", "000", "000"]
    ];

    private static int[] FontTile(string[] rows)
    {
        var pixels = Enumerable.Range(0, 8).Select(_ => Enumerable.Repeat('0', 8).ToArray()).ToArray();
        for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < rows[y].Length; x++)
                if (rows[y][x] == '1')
                    for (var dy = 0; dy < 3; dy++)
                        for (var dx = 0; dx < 3; dx++) pixels[y + dy][x + dx + 1] = '3';
        for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < rows[y].Length; x++)
                if (rows[y][x] == '1') pixels[y + 1][x + 2] = '1';
        return Tile(pixels.Select(row => new string(row)).ToArray());
    }

    private static int[] ColorBytes(string color)
    {
        var rgb = int.Parse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var value = ((rgb >> 19) & 31) | (((rgb >> 11) & 31) << 5) | (((rgb >> 3) & 31) << 10);
        return [value & 255, value >> 8];
    }

    // Stable WRAM: C000 x, C001 y, C002 repeat delay, C003 remaining,
    // C004 CGB flag, C005 won; C100..C4ff collision map.
    public static (byte[] Bytes, string Source) Create(GameSpec spec)
    {
        var asm = new EngineAssembler();
        var map = Enumerable.Repeat(1, 1024).ToArray();
        var attrs = new int[1024];
        var playerX = 1;
        var playerY = 1;
        var coins = 0;
        for (var y = 0; y < spec.Map.Length; y++)
            for (var x = 0; x < spec.Map[y].Length; x++)
            {
                var cell = spec.Map[y][x];
                map[y * 32 + x] = cell == '#' ? 1 : cell == 'C' ? 2 : 0;
                attrs[y * 32 + x] = cell == 'C' ? 1 : 0;
                if (cell == 'P') { playerX = x; playerY = y; }
                if (cell == 'C') coins++;
            }
        var tiles = new[]
        {
            Tile("00000000", "00000000", "00000000", "00000000", "00100000", "00000000", "00000000", "00000000"),
            Tile("00022000", "00212200", "02111220", "22111222", "02121220", "22212222", "00033000", "00333300"),
            Tile("00000000", "00011000", "00112100", "01122210", "01222210", "00122100", "00011000", "00000000"),
            Tile("00333300", "03111130", "03131130", "00311300", "03322330", "03122130", "00333300", "00300300")
        }.Concat(Font.Select(FontTile)).SelectMany(value => value).ToArray();
        var palette = spec.Palette;
        var palettes = new[] { palette.Background, palette.Ground, palette.Wall, "#19382d", palette.Background, palette.Collectible, "#b37d38", "#493820" }.SelectMany(ColorBytes).ToArray();
        var objectPalettes = new[] { "#000000", palette.Player, palette.Wall, "#19382d", "#000000", palette.Collectible, "#b37d38", "#493820" }.SelectMany(ColorBytes).ToArray();

        asm.Comment("Entry preserves the boot ROM hardware identity before touching A.");
        asm.Label("Entry");
        asm.Op("di", 0xf3);
        asm.Word("sp", 0xdfff);
        if (spec.Mode == "gbc")
        {
            asm.Op("cp $11", 0xfe, 0x11);
            asm.A(0);
            asm.Jump("StoreHardware", "nz");
            asm.A(1);
            asm.Label("StoreHardware");
        }
        else asm.A(0);
        asm.Store(0xc004);

        asm.Label("Restart");
        asm.ReadIo(0x40);
        asm.Op("bit 7, a", 0xcb, 0x7f);
        asm.Jump("LcdOff", "z");
        asm.Label("WaitSafeDisable");
        asm.ReadIo(0x44);
        asm.Op("cp $90", 0xfe, 144);
        asm.Jump("WaitSafeDisable", "c");
        asm.Label("LcdOff");
        asm.Op("xor a", 0xaf);
        foreach (var io in new[] { 0x40, 0xff, 0x0f, 0x42, 0x43, 0x26, 0x4f }) asm.WriteIo(io);
        asm.Store(0xc002);
        asm.Store(0xc005);
        asm.Word("hl", 0x8000);
        asm.Word("bc", 0x2000);
        asm.Call("ClearMemory");
        asm.Word("hl", 0xfe00);
        asm.Word("bc", 160);
        asm.Call("ClearMemory");
        asm.Word("hl", "Tiles");
        asm.Word("de", 0x8000);
        asm.Word("bc", tiles.Length);
        asm.Call("CopyMemory");
        asm.Word("hl", "Level");
        asm.Word("de", 0xc100);
        asm.Word("bc", 1024);
        asm.Call("CopyMemory");
        asm.Word("hl", "Level");
        asm.Word("de", 0x9800);
        asm.Word("bc", 1024);
        asm.Call("CopyMemory");
        asm.A(0xe4);
        asm.WriteIo(0x47);
        asm.WriteIo(0x48);
        asm.Load(0xc004);
        asm.Op("and a", 0xa7);
        asm.Jump("PalettesReady", "z");
        asm.A(1);
        asm.WriteIo(0x4f);
        asm.Word("hl", "Attributes");
        asm.Word("de", 0x9800);
        asm.Word("bc", 1024);
        asm.Call("CopyMemory");
        asm.A(0);
        asm.WriteIo(0x4f);
        asm.A(0x80);
        asm.WriteIo(0x68);
        asm.Word("hl", "BackgroundPalettes");
        asm.Op("ld b, $10", 0x06, 16);
        asm.Label("CopyBackgroundPalette");
        asm.Op("ld a, [hli]", 0x2a);
        asm.WriteIo(0x69);
        asm.Op("dec b", 0x05);
        asm.Jump("CopyBackgroundPalette", "nz");
        asm.A(0x80);
        asm.WriteIo(0x6a);
        asm.Word("hl", "ObjectPalettes");
        asm.Op("ld b, $10", 0x06, 16);
        asm.Label("CopyObjectPalette");
        asm.Op("ld a, [hli]", 0x2a);
        asm.WriteIo(0x6b);
        asm.Op("dec b", 0x05);
        asm.Jump("CopyObjectPalette", "nz");
        asm.Label("PalettesReady");
        asm.A(playerX);
        asm.Store(0xc000);
        asm.A(playerY);
        asm.Store(0xc001);
        asm.A(coins);
        asm.Store(0xc003);
        // Sound channel two provides a small chime when collecting a moonstone.
        asm.A(0x80);
        asm.WriteIo(0x26);
        asm.A(0x77);
        asm.WriteIo(0x24);
        asm.A(0x22);
        asm.WriteIo(0x25);
        asm.Call("RenderSprites");
        asm.A(0x93);
        asm.WriteIo(0x40);

        asm.Comment("Poll one VBlank per frame. All live VRAM/OAM writes finish within VBlank.");
        asm.Label("Frame");
        asm.ReadIo(0x44);
        asm.Op("cp $90", 0xfe, 144);
        asm.Jump("Frame", "nc");
        asm.Label("WaitVBlank");
        asm.ReadIo(0x44);
        asm.Op("cp $90", 0xfe, 144);
        asm.Jump("WaitVBlank", "c");
        asm.A(0x10);
        asm.WriteIo(0);
        for (var i = 0; i < 4; i++) asm.ReadIo(0);
        asm.Op("bit 3, a", 0xcb, 0x5f);
        asm.Jump("Restart", "z");
        asm.Load(0xc005);
        asm.Op("and a", 0xa7);
        asm.Jump("DrawFrame", "nz");
        asm.Load(0xc002);
        asm.Op("and a", 0xa7);
        asm.Jump("ReadDirection", "z");
        asm.Op("dec a", 0x3d);
        asm.Store(0xc002);
        asm.Jump("DrawFrame");

        asm.Label("ReadDirection");
        asm.A(0x20);
        asm.WriteIo(0);
        for (var i = 0; i < 4; i++) asm.ReadIo(0);
        asm.Op("cpl", 0x2f);
        asm.Op("and $0f", 0xe6, 15);
        asm.Op("ld b, a", 0x47);
        asm.Load(0xc000);
        asm.Op("ld d, a", 0x57);
        asm.Load(0xc001);
        asm.Op("ld e, a", 0x5f);
        asm.Op("bit 0, b", 0xcb, 0x40);
        asm.Jump("MoveRight", "nz");
        asm.Op("bit 1, b", 0xcb, 0x48);
        asm.Jump("MoveLeft", "nz");
        asm.Op("bit 2, b", 0xcb, 0x50);
        asm.Jump("MoveUp", "nz");
        asm.Op("bit 3, b", 0xcb, 0x58);
        asm.Jump("MoveDown", "nz");
        asm.Jump("DrawFrame");
        asm.Label("MoveRight");
        asm.Op("inc d", 0x14);
        asm.Jump("TryMove");
        asm.Label("MoveLeft");
        asm.Op("dec d", 0x15);
        asm.Jump("TryMove");
        asm.Label("MoveUp");
        asm.Op("dec e", 0x1d);
        asm.Jump("TryMove");
        asm.Label("MoveDown");
        asm.Op("inc e", 0x1c);

        asm.Label("TryMove");
        asm.A(6);
        asm.Store(0xc002);
        asm.Op("ld a, d", 0x7a);
        asm.Op("cp $14", 0xfe, 20);
        asm.Jump("DrawFrame", "nc");
        asm.Op("ld a, e", 0x7b);
        asm.Op("cp $12", 0xfe, 18);
        asm.Jump("DrawFrame", "nc");
        asm.Word("hl", 0);
        asm.Op("ld l, e", 0x6b);
        for (var i = 0; i < 5; i++) asm.Op("add hl, hl", 0x29);
        asm.Word("bc", 0xc100);
        asm.Op("add hl, bc", 0x09);
        asm.Op("ld c, d", 0x4a);
        asm.Op("ld b, $00", 0x06, 0);
        asm.Op("add hl, bc", 0x09);
        asm.Op("ld a, [hl]", 0x7e);
        asm.Op("cp $01", 0xfe, 1);
        asm.Jump("DrawFrame", "z");
        asm.Op("ld b, a", 0x47);
        asm.Op("ld a, d", 0x7a);
        asm.Store(0xc000);
        asm.Op("ld a, e", 0x7b);
        asm.Store(0xc001);
        asm.Op("ld a, b", 0x78);
        asm.Op("cp $02", 0xfe, 2);
        asm.Jump("DrawFrame", "nz");
        asm.Op("xor a", 0xaf);
        asm.Op("ld [hl], a", 0x77);
        // C100 + offset -> 9800 + offset = subtract 2900 from HL.
        asm.Op("ld a, h", 0x7c);
        asm.Op("sub $29", 0xd6, 0x29);
        asm.Op("ld h, a", 0x67);
        asm.Op("xor a", 0xaf);
        asm.Op("ld [hl], a", 0x77);
        asm.Load(0xc004);
        asm.Op("and a", 0xa7);
        asm.Jump("CollectPaletteReady", "z");
        asm.A(1);
        asm.WriteIo(0x4f);
        asm.Op("xor a", 0xaf);
        asm.Op("ld [hl], a", 0x77);
        asm.WriteIo(0x4f);
        asm.Label("CollectPaletteReady");
        asm.A(0x80);
        asm.WriteIo(0x16);
        asm.A(0x82);
        asm.WriteIo(0x17);
        asm.A(0xc0);
        asm.WriteIo(0x18);
        asm.A(0x87);
        asm.WriteIo(0x19);
        asm.Load(0xc003);
        asm.Op("dec a", 0x3d);
        asm.Store(0xc003);
        asm.Jump("DrawFrame", "nz");
        asm.A(1);
        asm.Store(0xc005);

        asm.Label("DrawFrame");
        asm.Call("RenderSprites");
        asm.A(0x30);
        asm.WriteIo(0);
        asm.Jump("Frame");

        asm.Comment("OAM: player; optional YOU WIN banner; moonstone count in top-left.");
        asm.Label("RenderSprites");
        asm.Word("hl", 0xfe00);
        asm.Load(0xc001);
        for (var i = 0; i < 3; i++) asm.Op("add a, a", 0x87);
        asm.Op("add a, $10", 0xc6, 16);
        asm.Op("ld [hli], a", 0x22);
        asm.Load(0xc000);
        for (var i = 0; i < 3; i++) asm.Op("add a, a", 0x87);
        asm.Op("add a, $08", 0xc6, 8);
        asm.Op("ld [hli], a", 0x22);
        asm.A(3);
        asm.Op("ld [hli], a", 0x22);
        asm.A(0);
        asm.Op("ld [hli], a", 0x22);
        asm.Load(0xc005);
        asm.Op("and a", 0xa7);
        asm.Jump("DrawCount", "z");
        asm.Word("de", "VictorySprites");
        asm.Op("ld b, $1c", 0x06, 28);
        asm.Label("CopyVictorySprites");
        asm.Op("ld a, [de]", 0x1a);
        asm.Op("inc de", 0x13);
        asm.Op("ld [hli], a", 0x22);
        asm.Op("dec b", 0x05);
        asm.Jump("CopyVictorySprites", "nz");
        asm.Label("DrawCount");
        asm.Word("hl", 0xfe20);
        foreach (var value in new[] { 23, 14, 2, 1, 23, 23 })
        {
            asm.A(value);
            asm.Op("ld [hli], a", 0x22);
        }
        asm.Load(0xc003);
        asm.Op("ld b, $00", 0x06, 0);
        asm.Label("CountTens");
        asm.Op("cp $0a", 0xfe, 10);
        asm.Jump("CountDigits", "c");
        asm.Op("sub $0a", 0xd6, 10);
        asm.Op("inc b", 0x04);
        asm.Jump("CountTens");
        asm.Label("CountDigits");
        asm.Op("ld c, a", 0x4f);
        asm.Op("ld a, b", 0x78);
        asm.Op("add a, $04", 0xc6, 4);
        asm.Op("ld [hli], a", 0x22);
        foreach (var value in new[] { 0, 23, 29 })
        {
            asm.A(value);
            asm.Op("ld [hli], a", 0x22);
        }
        asm.Op("ld a, c", 0x79);
        asm.Op("add a, $04", 0xc6, 4);
        asm.Op("ld [hli], a", 0x22);
        asm.A(0);
        asm.Op("ld [hli], a", 0x22);
        asm.Op("ret", 0xc9);

        asm.Label("ClearMemory");
        asm.Op("xor a", 0xaf);
        asm.Op("ld [hli], a", 0x22);
        asm.Op("dec bc", 0x0b);
        asm.Op("ld a, b", 0x78);
        asm.Op("or c", 0xb1);
        asm.Jump("ClearMemory", "nz");
        asm.Op("ret", 0xc9);
        asm.Label("CopyMemory");
        asm.Op("ld a, [hli]", 0x2a);
        asm.Op("ld [de], a", 0x12);
        asm.Op("inc de", 0x13);
        asm.Op("dec bc", 0x0b);
        asm.Op("ld a, b", 0x78);
        asm.Op("or c", 0xb1);
        asm.Jump("CopyMemory", "nz");
        asm.Op("ret", 0xc9);
        asm.Comment("Original 8x8 graphics, level, palettes, and victory banner.");
        asm.Data("Tiles", tiles);
        asm.Data("Level", map);
        asm.Data("Attributes", attrs);
        asm.Data("BackgroundPalettes", palettes);
        asm.Data("ObjectPalettes", objectPalettes);
        asm.Data(
          "VictorySprites",
          new[] { 14, 15, 16, 20, 17, 18, 19 }.SelectMany((id, index) => new[] { 80, 60 + index * 8, id, 0 }).ToArray()
        );
        return asm.Finish();
    }
}
