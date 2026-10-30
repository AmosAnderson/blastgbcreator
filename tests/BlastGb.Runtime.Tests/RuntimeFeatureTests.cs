using System.Security.Cryptography;
using System.Text.Json;
using BlastGb.Core;
using BlastGb.Runtime;
using static BlastGb.Runtime.Tests.EmulatorTests;

namespace BlastGb.Runtime.Tests;

public sealed class RuntimeFeatureTests
{
    [Fact]
    public void ScriptedButtonsPlayAndReleaseThroughRealJoypad()
    {
        var map = Enumerable.Range(0, 18).Select(y => y is 0 or 17 ? new string('#', 20) : $"#{new string('.', 18)}#").ToArray();
        map[1] = "#PC................#";
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec() with { Map = map }).Rom;
        var result = RuntimeRunner.Debug(rom, Json("""
            {"version":1,"commands":[{"op":"frame","count":10},
              {"op":"buttons","pressed":["right"]},{"op":"frame","count":8},
              {"op":"buttons","pressed":[]},
              {"op":"assert","memory":[{"address":49152,"bytes":[2,1]},{"address":49157,"bytes":[1]}]}]}
            """));
        Assert.True(result.AssertionsPassed);
        Assert.Equal(new[] { "right" }, result.Results[1]["pressed"]!.Deserialize<string[]>());
        Assert.Empty(result.Results[3]["pressed"]!.Deserialize<string[]>()!);
    }

    [Fact]
    public void FramebufferAssertionsCaptureAndProvenanceAreOptionalAndDeterministic()
    {
        var rom = Cartridge([0x18, 0xfe]);
        var baseline = RuntimeRunner.Run(rom, new() { Frames = 2, CaptureFramebuffer = true, IncludeProvenance = true });
        Assert.NotNull(baseline.Framebuffer);
        Assert.Equal(160 * 144 * 4, baseline.Framebuffer.Length);
        Assert.Equal(baseline.FramebufferSha256, Convert.ToHexStringLower(SHA256.HashData(baseline.Framebuffer)));
        Assert.Equal(EmulatorSession.Provenance, baseline.Provenance);
        var checks = JsonSerializer.SerializeToElement(new { framebufferSha256 = baseline.FramebufferSha256 });
        var passed = RuntimeRunner.Run(rom, new() { Frames = 2, Assertions = checks });
        Assert.True(passed.AssertionsPassed);
        Assert.Equal("framebuffer", Assert.Single(passed.Assertions).Kind);
        Assert.Null(passed.Framebuffer);
        Assert.Null(passed.Provenance);
        string json = JsonSerializer.Serialize(baseline, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("\"framebuffer\":", json, StringComparison.Ordinal);
        var failed = RuntimeRunner.Debug(rom, JsonSerializer.SerializeToElement(new
        {
            version = 1,
            commands = new object[] { new { op = "assert", framebufferSha256 = new string('0', 64) }, new { op = "write", address = 49152, bytes = new[] { 7 } } }
        }), new DebugOptions { CaptureFramebuffer = true, IncludeProvenance = true });
        Assert.False(failed.AssertionsPassed);
        Assert.False(failed.Completed);
        Assert.Single(failed.Results);
        Assert.NotNull(failed.Framebuffer);
        Assert.NotNull(failed.Provenance);
    }

    [Fact]
    public void ConditionsPollAfterStepsAndTraceRetainsOnlyRecentEvidence()
    {
        var result = RuntimeRunner.Run(Cartridge(), new()
        {
            Until = Json("""{"memory":[{"address":49152,"bytes":[42]}]}"""),
            TraceCapacity = 2,
            MaxInstructions = 10
        });
        Assert.True(result.UntilReached);
        Assert.True(result.AssertionsPassed);
        Assert.Equal("condition", result.StopReason);
        Assert.Equal(44, result.Ticks);
        Assert.Equal(new double[] { 28, 44 }, result.Trace!.Select(entry => entry.Ticks));
        Assert.Equal(new[] { 0x152, 0x155 }, result.Trace!.Select(entry => entry.Registers.PC));
        var initial = RuntimeRunner.Run(Cartridge(), new() { Until = Json("""{"registers":{"pc":256}}"""), TraceCapacity = 2 });
        Assert.True(initial.UntilReached);
        Assert.Equal(0, initial.Ticks);
        Assert.Empty(initial.Trace!);
    }

    [Fact]
    public void UnreachedScriptConditionsStopBeforeLaterCommands()
    {
        var result = RuntimeRunner.Debug(Cartridge(), Json("""
            {"version":1,"commands":[{"op":"run","maxInstructions":2,"traceCapacity":1,
              "until":{"registers":{"a":255}}},{"op":"write","address":49152,"bytes":[7]}]}
            """));
        Assert.False(result.ConditionsPassed);
        Assert.True(result.AssertionsPassed);
        Assert.False(result.Completed);
        Assert.Single(result.Results);
        Assert.False(result.Results[0]["untilReached"]!.GetValue<bool>());
        Assert.Single(result.Results[0]["trace"]!.AsArray());
        Assert.Equal("instruction-budget", result.StopReason);
        var passed = RuntimeRunner.Debug(Cartridge(), Json("""
            {"version":1,"commands":[{"op":"run","maxInstructions":10,
              "until":{"registers":{"a":42}}},{"op":"assert","registers":{"pc":338}}]}
            """));
        Assert.True(passed.ConditionsPassed);
        Assert.True(passed.Completed);
        Assert.True(passed.AssertionsPassed);
    }

    [Theory]
    [InlineData("{\"op\":\"buttons\",\"pressed\":[\"wrong\"]}")]
    [InlineData("{\"op\":\"buttons\",\"pressed\":[\"A\",\"A\"]}")]
    [InlineData("{\"op\":\"buttons\",\"pressed\":null}")]
    [InlineData("{\"op\":\"run\",\"until\":{}}")]
    [InlineData("{\"op\":\"run\",\"until\":{\"framebufferSha256\":\"00\"}}")]
    [InlineData("{\"op\":\"run\",\"traceCapacity\":1025}")]
    [InlineData("{\"op\":\"assert\",\"framebufferSha256\":\"00\"}")]
    public void NewScriptFieldsAreValidatedBeforeRomAllocation(string command) =>
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Debug([], Json($"{{\"version\":1,\"commands\":[{command}]}}")));

    [Fact]
    public void ConditionAndTraceAggregateBudgetsAreValidatedBeforeRomAllocation()
    {
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Run([], new() { TraceCapacity = 1025 }));
        var condition = JsonSerializer.SerializeToElement(new { memory = new[] { new { address = 49152, bytes = new int[32] } } });
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Run([], new() { Until = condition, MaxInstructions = 1_000_000 }));
        var commands = Enumerable.Repeat(new { op = "run", frames = 1, maxInstructions = 1, traceCapacity = 1024 }, 5);
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Debug([], JsonSerializer.SerializeToElement(new { version = 1, commands })));
    }

    [Fact]
    public void BatteryRamRoundTripsEveryBankWithoutMutatingRomOrMapperSelection()
    {
        var rom = Cartridge([0x18, 0xfe]);
        rom[0x147] = 0x03;
        rom[0x149] = 0x03;
        var original = rom.ToArray();
        byte[] save;
        using (var first = new EmulatorSession(rom))
        {
            first.WriteMemory(0x0000, [0x0a]);
            first.WriteMemory(0x6000, [1]);
            first.WriteMemory(0x4000, [0]);
            first.WriteMemory(0xa000, [17]);
            first.WriteMemory(0x4000, [2]);
            first.WriteMemory(0xa000, [42]);
            save = first.ExportBatterySave();
            Assert.Equal(32768, save.Length);
            Assert.Equal(42, first.ReadMemory(0xa000)[0]);
            Assert.Throws<ArgumentException>(() => first.ImportBatterySave(new byte[1]));
            Assert.Equal(42, first.ReadMemory(0xa000)[0]);
        }
        using (var second = new EmulatorSession(rom))
        {
            second.ImportBatterySave(save);
            second.WriteMemory(0x0000, [0x0a]);
            second.WriteMemory(0x6000, [1]);
            Assert.Equal(17, second.ReadMemory(0xa000)[0]);
            second.WriteMemory(0x4000, [2]);
            Assert.Equal(42, second.ReadMemory(0xa000)[0]);
            Assert.Equal(save, second.ExportBatterySave());
        }
        Assert.Equal(original, rom);
        var run = RuntimeRunner.Run(rom, new() { Frames = 1, BatterySave = save, CaptureBatterySave = true });
        Assert.Equal(save, run.BatterySave);
        var debug = RuntimeRunner.Debug(rom, Json("""{"version":1,"commands":[{"op":"read","address":256}]}"""),
            new DebugOptions { BatterySave = save, CaptureBatterySave = true });
        Assert.Equal(save, debug.BatterySave);
        Assert.DoesNotContain("\"batterySave\":", JsonSerializer.Serialize(run, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x03, 2, 8192)]
    [InlineData(0x06, 0, 512)]
    [InlineData(0x09, 2, 8192)]
    [InlineData(0x13, 2, 8192)]
    [InlineData(0x1b, 2, 8192)]
    [InlineData(0x1e, 2, 8192)]
    public void SupportedBatteryMappersUseExactRawSaveSizes(int type, int ramCode, int size)
    {
        var rom = Cartridge();
        rom[0x147] = (byte)type;
        rom[0x149] = (byte)ramCode;
        using var session = new EmulatorSession(rom);
        byte[] save = Enumerable.Range(0, size).Select(index => (byte)(index % 16)).ToArray();
        session.ImportBatterySave(save);
        Assert.Equal(save, session.ExportBatterySave());
        Assert.Throws<ArgumentException>(() => session.ImportBatterySave(new byte[size + 1]));
    }

    [Fact]
    public void EngineProvenanceMatchesEmbeddedImmutableBytes()
    {
        using var stream = typeof(EmulatorSession).Assembly.GetManifestResourceStream("BlastGb.Runtime.binjgb.wasm")!;
        Assert.Equal(EmulatorSession.EngineSha256, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x0f)]
    [InlineData(0x10)]
    [InlineData(0x22)]
    public void UnsupportedBatteryFormatsFailExplicitly(int type)
    {
        var rom = Cartridge();
        rom[0x147] = (byte)type;
        using var session = new EmulatorSession(rom);
        Assert.Throws<ArgumentException>(() => session.ExportBatterySave());
        Assert.Throws<ArgumentException>(() => session.ImportBatterySave([1]));
    }
}
