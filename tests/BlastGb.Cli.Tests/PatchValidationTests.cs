using System.Text.Json;
using BlastGb.Cli;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class PatchValidationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast-patch-validation-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] rom;
    private readonly byte[] previousOutput = [7, 8, 9];
    private string Input => Path.Combine(directory, "original.gb");
    private string Output => Path.Combine(directory, "patched.gb");
    private string Patches => Path.Combine(directory, "patches.json");

    public PatchValidationTests()
    {
        Directory.CreateDirectory(directory);
        rom = new byte[128 * 1024];
        GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom.CopyTo(rom, 0);
        rom[0x148] = 2;
        rom = RomTools.RepairChecksums(rom);
        File.WriteAllBytes(Input, rom);
        File.WriteAllBytes(Output, previousOutput);
    }

    public void Dispose() => Directory.Delete(directory, true);

    private async Task AssertRejectedAsync(string json, string message)
    {
        File.WriteAllText(Patches, json);
        var result = await CliTests.Invoke("rom", "patch", Input, Patches, "--output", Output, "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.Equal("INVALID_PATCH", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.Contains(message, result.Json["error"]!["message"]!.GetValue<string>());
        Assert.Equal(rom, File.ReadAllBytes(Input));
        Assert.Equal(previousOutput, File.ReadAllBytes(Output));
        Assert.Equal(json, File.ReadAllText(Patches));
    }

    [Theory]
    [InlineData("offset", "65536")]
    [InlineData("bytes", "[42]")]
    [InlineData("expected", "[0]")]
    public async Task DuplicateRecordPropertiesPreserveAllFiles(string name, string value)
    {
        await AssertRejectedAsync($"[{{\"offset\":65536,\"bytes\":[42],\"expected\":[0],\"{name}\":{value}}}]", "Duplicate patch property");
    }

    [Theory]
    [InlineData("unexpected")]
    [InlineData("Offset")]
    public async Task UnknownRecordPropertiesPreserveAllFiles(string name)
    {
        await AssertRejectedAsync($"[{{\"offset\":65536,\"bytes\":[42],\"expected\":[0],\"{name}\":0}}]", "Unknown patch property");
    }

    [Theory]
    [InlineData("bytes", 0)]
    [InlineData("expected", 0)]
    [InlineData("bytes", 65537)]
    [InlineData("expected", 65537)]
    public async Task ByteArrayLimitsPreserveAllFiles(string name, int count)
    {
        var patch = new Dictionary<string, object>
        {
            ["offset"] = 65536,
            ["bytes"] = new byte[] { 42 },
            ["expected"] = new byte[] { 0 }
        };
        patch[name] = new byte[count];
        await AssertRejectedAsync(JsonSerializer.Serialize(new[] { patch }, JsonDefaults.Options), $"Patch {name} must contain between 1 and 65536 bytes");
    }

    [Fact]
    public async Task TooManyRecordsPreserveAllFiles()
    {
        var patches = Enumerable.Range(0, 65537).Select(index => new RomPatch(index, [42], [rom[index]]));
        await AssertRejectedAsync(JsonSerializer.Serialize(patches, JsonDefaults.Options), "at most 65536 records");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedByteAndRecordLimitsRemainAccepted(bool separateRecords)
    {
        IEnumerable<RomPatch> patches = separateRecords
            ? Enumerable.Range(65536, 65536).Select(offset => new RomPatch(offset, [42], [rom[offset]]))
            : [new RomPatch(65536, Enumerable.Repeat((byte)42, 65536).ToArray(), rom[65536..])];
        var json = JsonSerializer.Serialize(patches, JsonDefaults.Options);
        File.WriteAllText(Patches, json);
        var result = await CliTests.Invoke("rom", "patch", Input, Patches, "--output", Output, "--overwrite");
        Assert.True(result.Status == 0, result.Json.ToJsonString());
        Assert.Equal(separateRecords ? 65536 : 1, result.Json["data"]!["patches"]!.GetValue<int>());
        var expected = (byte[])rom.Clone();
        Array.Fill(expected, (byte)42, 65536, 65536);
        Assert.Equal(expected, File.ReadAllBytes(Output));
        Assert.Equal(rom, File.ReadAllBytes(Input));
        Assert.Equal(json, File.ReadAllText(Patches));
    }
}
