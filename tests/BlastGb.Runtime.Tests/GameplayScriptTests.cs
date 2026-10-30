using System.Security.Cryptography;
using System.Text.Json;
using BlastGb.Core;
using BlastGb.Runtime;
using static BlastGb.Runtime.Tests.EmulatorTests;

namespace BlastGb.Runtime.Tests;

public sealed class GameplayScriptTests
{
    [Theory]
    [InlineData("gb")]
    [InlineData("gbc")]
    public void ButtonSequencesExerciseCollisionVictoryAndRestart(string mode)
    {
        var map = Enumerable.Range(0, 18).Select(y => y is 0 or 17 ? new string('#', 20) : $"#{new string('.', 18)}#").ToArray();
        map[1] = "#PC................#";
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec() with { Mode = mode, Map = map }).Rom;
        var original = rom.ToArray();
        var result = RuntimeRunner.Debug(rom, Json("""
            {"version":1,"commands":[
              {"op":"frame","count":10},
              {"op":"buttons","pressed":["left"]},{"op":"frame","count":8},
              {"op":"assert","memory":[{"address":49152,"bytes":[1,1]}]},
              {"op":"buttons","pressed":[]},{"op":"frame","count":8},
              {"op":"buttons","pressed":["right"]},{"op":"frame","count":8},
              {"op":"assert","memory":[{"address":49152,"bytes":[2,1]},{"address":49155,"bytes":[0]},{"address":49157,"bytes":[1]}]},
              {"op":"buttons","pressed":["down"]},{"op":"frame","count":8},
              {"op":"assert","memory":[{"address":49152,"bytes":[2,1]}]},
              {"op":"buttons","pressed":["start"]},{"op":"frame"},
              {"op":"buttons","pressed":[]},{"op":"frame","count":10},
              {"op":"assert","memory":[{"address":49152,"bytes":[1,1]},{"address":49155,"bytes":[1]},{"address":49157,"bytes":[0]}]}
            ]}
            """));

        Assert.True(result.Completed);
        Assert.True(result.AssertionsPassed);
        Assert.Empty(result.Results[14]["pressed"]!.Deserialize<string[]>()!);
        Assert.Equal(original, rom);
    }

    [Fact]
    public void UntilChecksInitialStateAndStopsAtFirstMatchingInstruction()
    {
        var result = RuntimeRunner.Debug(Cartridge(), Json("""
            {"version":1,"commands":[
              {"op":"until","registers":{"pc":256},"maxInstructions":1},
              {"op":"write","address":49152,"bytes":[0]},
              {"op":"until","memory":[{"address":49152,"bytes":[42]}],"registers":{"a":42},"maxInstructions":10},
              {"op":"assert","registers":{"pc":341}}
            ]}
            """));

        Assert.True(result.AssertionsPassed);
        Assert.True(result.Completed);
        Assert.Equal(0, result.Results[0]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(0, result.Results[0]["elapsedTicks"]!.GetValue<double>());
        Assert.Equal(4, result.Results[2]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(44, result.Ticks);
        Assert.Equal("condition", result.Results[2]["stopReason"]!.GetValue<string>());
        Assert.True(result.Results[2]["conditionReached"]!.GetValue<bool>());
    }

    [Fact]
    public void UntilInstructionTimeoutReportsActualStateAndStopsLaterWrites()
    {
        var result = RuntimeRunner.Debug(Cartridge(), Json("""
            {"version":1,"commands":[
              {"op":"write","address":49152,"bytes":[18]},
              {"op":"until","memory":[{"address":49152,"bytes":[42]}],"maxInstructions":3},
              {"op":"write","address":49152,"bytes":[99]}
            ]}
            """));

        Assert.False(result.AssertionsPassed);
        Assert.False(result.Completed);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal("instruction-budget", result.StopReason);
        Assert.Equal(28, result.Ticks);
        Assert.False(result.Results[1]["conditionReached"]!.GetValue<bool>());
        Assert.Equal(new[] { 18 }, result.Results[1]["assertions"]![0]!["actual"]!.Deserialize<int[]>());
    }

    [Fact]
    public void UntilFrameTimeoutAndBreakpointBothFailUnmetConditions()
    {
        var rom = Cartridge([0x00, 0x18, 0xfd]);
        var timeout = RuntimeRunner.Debug(rom, Json("""
            {"version":1,"commands":[{"op":"until","registers":{"pc":57005},"frames":1,"maxInstructions":10000}]}
            """));
        Assert.False(timeout.AssertionsPassed);
        Assert.Equal("frame-budget", timeout.StopReason);
        Assert.InRange(timeout.Ticks, EmulatorSession.FrameTicks, EmulatorSession.FrameTicks + 24);

        var breakpoint = RuntimeRunner.Debug(rom, Json("""
            {"version":1,"commands":[{"op":"breakpoints","addresses":[256]},
              {"op":"until","registers":{"pc":57005},"maxInstructions":10},{"op":"step"}]}
            """));
        Assert.False(breakpoint.AssertionsPassed);
        Assert.False(breakpoint.Completed);
        Assert.Equal("breakpoint", breakpoint.StopReason);
        Assert.Equal(0, breakpoint.Ticks);
    }

    [Fact]
    public void UntilMatchesAtBreakpointAndPreservesResumeSemantics()
    {
        var result = RuntimeRunner.Debug(Cartridge(), Json("""
            {"version":1,"commands":[{"op":"breakpoints","addresses":[338]},
              {"op":"until","registers":{"a":42},"maxInstructions":10},
              {"op":"run","maxInstructions":1}]}
            """));
        Assert.True(result.AssertionsPassed);
        Assert.Equal("condition", result.Results[1]["stopReason"]!.GetValue<string>());
        Assert.Equal(3, result.Results[1]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(1, result.Results[2]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(0x155, result.Registers.PC);
    }

    [Fact]
    public void UntilPendingAudioDoesNotConsumeItsInstructionBudget()
    {
        var result = RuntimeRunner.Debug(Cartridge([0x00, 0x18, 0xfd]), Json("""
            {"version":1,"commands":[{"op":"step","count":22370},
              {"op":"until","registers":{"pc":337},"maxInstructions":1}]}
            """));
        Assert.True(result.AssertionsPassed);
        Assert.Equal(1, result.Results[1]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(4, result.Results[1]["elapsedTicks"]!.GetValue<double>());
        Assert.Equal(178_968, result.Ticks);
    }

    [Fact]
    public void FramebufferAssertionsNormalizeHashAndExposePixelsWithoutChangingJson()
    {
        var rom = Cartridge();
        var baseline = RuntimeRunner.Run(rom, new() { Frames = 1 });
        var verified = RuntimeRunner.Run(rom, new()
        {
            Frames = 1,
            Assertions = JsonSerializer.SerializeToElement(new { framebufferSha256 = baseline.FramebufferSha256.ToUpperInvariant() })
        });
        Assert.True(verified.AssertionsPassed);
        Assert.Equal(160 * 144 * 4, verified.FramebufferRgba.Length);
        Assert.Equal(verified.FramebufferSha256, Convert.ToHexStringLower(SHA256.HashData(verified.FramebufferRgba)));
        var check = Assert.Single(verified.Assertions);
        Assert.Equal("framebuffer", check.Kind);
        Assert.Equal(baseline.FramebufferSha256, check.Expected);
        Assert.Equal(baseline.FramebufferSha256, check.Actual);
        var json = JsonSerializer.SerializeToElement(verified, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.False(json.TryGetProperty("framebufferRgba", out _));
    }

    [Fact]
    public void ExplicitCapturesAndFailedVisualAssertionsRetainNamedEvidence()
    {
        var result = RuntimeRunner.Debug(Cartridge(), JsonSerializer.SerializeToElement(new
        {
            version = 1,
            commands = new object[]
            {
                new { op = "capture", name = "startup" },
                new { op = "frame", count = 2 },
                new { op = "capture", name = "running" },
                new { op = "assert", framebufferSha256 = new string('0', 64) },
                new { op = "write", address = 49152, bytes = new[] { 99 } }
            }
        }));
        Assert.False(result.AssertionsPassed);
        Assert.False(result.Completed);
        Assert.Equal("assertion-failed", result.StopReason);
        Assert.Equal(new[] { "startup", "running", "failure-3" }, result.Captures.Select(capture => capture.Name));
        Assert.Equal("failure-3", result.Results[3]["capture"]!.GetValue<string>());
        foreach (var capture in result.Captures)
        {
            Assert.Equal(160 * 144 * 4, capture.Rgba.Length);
            Assert.Equal(capture.Sha256, Convert.ToHexStringLower(SHA256.HashData(capture.Rgba)));
        }
        Assert.Equal(result.FramebufferRgba, result.Captures[2].Rgba);
        Assert.Equal(result.FramebufferSha256, result.Captures[2].Sha256);
        Assert.Equal(result.Captures[0].Sha256, result.Results[0]["framebufferSha256"]!.GetValue<string>());
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.False(json.TryGetProperty("captures", out _));
        Assert.False(json.TryGetProperty("framebufferRgba", out _));
    }

    [Fact]
    public void SuccessfulVisualAssertionNeedsNoFailureCapture()
    {
        using var session = new EmulatorSession(Cartridge());
        var expected = session.FramebufferSha256();
        var result = RuntimeRunner.Debug(Cartridge(), JsonSerializer.SerializeToElement(new
        {
            version = 1,
            commands = new[] { new { op = "assert", framebufferSha256 = expected } }
        }));
        Assert.True(result.AssertionsPassed);
        Assert.Empty(result.Captures);
    }

    [Fact]
    public void FailedVisualAssertionNeverExceedsCaptureLimit()
    {
        var commands = Enumerable.Range(0, DebugLimits.Captures).Select(index => (object)new { op = "capture", name = $"frame-{index}" }).ToList();
        commands.Add(new { op = "assert", framebufferSha256 = new string('0', 64) });
        var result = RuntimeRunner.Debug(Cartridge(), JsonSerializer.SerializeToElement(new { version = 1, commands }));
        Assert.False(result.AssertionsPassed);
        Assert.Equal(DebugLimits.Captures, result.Captures.Count);
        Assert.Null(result.Results[^1]["capture"]);
    }

    [Theory]
    [InlineData("{\"op\":\"buttons\",\"pressed\":[\"a\"]}")]
    [InlineData("{\"op\":\"buttons\",\"pressed\":[\"A\",\"A\"]}")]
    [InlineData("{\"op\":\"buttons\",\"pressed\":null}")]
    [InlineData("{\"op\":\"buttons\",\"pressed\":[null]}")]
    [InlineData("{\"op\":\"until\",\"frames\":1}")]
    [InlineData("{\"op\":\"until\",\"registers\":{\"pc\":256},\"maxInstructions\":0}")]
    [InlineData("{\"op\":\"until\",\"framebufferSha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}")]
    [InlineData("{\"op\":\"capture\",\"name\":\"../outside\"}")]
    [InlineData("{\"op\":\"capture\",\"name\":\"CON\"}")]
    [InlineData("{\"op\":\"capture\",\"name\":\"lpt9\"}")]
    [InlineData("{\"op\":\"capture\",\"name\":\"failure-1\"}")]
    [InlineData("{\"op\":\"capture\",\"name\":\"\"}")]
    [InlineData("{\"op\":\"assert\",\"framebufferSha256\":null}")]
    [InlineData("{\"op\":\"assert\",\"framebufferSha256\":\"abcd\"}")]
    [InlineData("{\"op\":\"assert\",\"framebufferSha256\":\"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"}")]
    public void NewOperationsAreValidatedBeforeOpeningRom(string operation)
    {
        var script = Json("{\"version\":1,\"commands\":[{\"op\":\"write\",\"address\":49152,\"bytes\":[1]}," + operation + "]}");
        var error = Assert.Throws<ArgumentException>(() => RuntimeRunner.Debug([], script));
        Assert.DoesNotContain("Choose a Game Boy ROM", error.Message);
    }

    [Fact]
    public void AggregateUntilWorkAndCaptureNamesAreValidated()
    {
        Assert.Throws<ArgumentException>(() => RuntimeRunner.ValidateDebugScript(Json("""
            {"version":1,"commands":[{"op":"until","registers":{"pc":256},"maxInstructions":1000000},{"op":"step"}]}
            """)));
        Assert.Throws<ArgumentException>(() => RuntimeRunner.ValidateDebugScript(Json("""
            {"version":1,"commands":[{"op":"until","registers":{"pc":256},"frames":3600},{"op":"frame"}]}
            """)));
        Assert.Throws<ArgumentException>(() => RuntimeRunner.ValidateDebugScript(Json("""
            {"version":1,"commands":[{"op":"until","memory":[{"address":49152,"bytes":[0,0]}],"maxInstructions":1000000}]}
            """)));
        Assert.Throws<ArgumentException>(() => RuntimeRunner.ValidateDebugScript(Json("""
            {"version":1,"commands":[{"op":"capture","name":"Screen"},{"op":"capture","name":"screen"}]}
            """)));
        var commands = Enumerable.Range(0, DebugLimits.Captures + 1).Select(index => new { op = "capture", name = $"frame-{index}" });
        Assert.Throws<ArgumentException>(() => RuntimeRunner.ValidateDebugScript(JsonSerializer.SerializeToElement(new { version = 1, commands })));
    }
}
