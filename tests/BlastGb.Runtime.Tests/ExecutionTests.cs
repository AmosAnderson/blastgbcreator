using System.Text.Json;
using BlastGb.Runtime;
using static BlastGb.Runtime.Tests.EmulatorTests;

namespace BlastGb.Runtime.Tests;

public sealed class ExecutionTests
{
    [Fact]
    public void DebugStopsAtNewCurrentBreakpointThenResumesExactlyOnce()
    {
        var result = RuntimeRunner.Debug(Cartridge(), Json("""
          {"version":1,"commands":[
            {"op":"breakpoints","addresses":[256,336]},
            {"op":"run","frames":1,"maxInstructions":100},
            {"op":"assert","registers":{"pc":256}},
            {"op":"read","address":256},
            {"op":"run","frames":1,"maxInstructions":100},
            {"op":"assert","registers":{"pc":336}},
            {"op":"run","frames":1,"maxInstructions":1}]}
          """));
        Assert.True(result.AssertionsPassed);
        Assert.Equal(0, result.Results[1]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(0, result.Results[1]["elapsedTicks"]!.GetValue<double>());
        Assert.Equal(2, result.Results[4]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(20, result.Results[4]["elapsedTicks"]!.GetValue<double>());
        Assert.Equal("instruction-budget", result.StopReason);
        Assert.Equal(0x152, result.Registers.PC);
        Assert.Equal(42, result.Registers.A);
    }

    [Fact]
    public void ExplicitStepAndReplacementBreakpointsHaveSeparateSemantics()
    {
        var result = RuntimeRunner.Debug(Cartridge(), Json("""
          {"version":1,"commands":[{"op":"breakpoints","addresses":[256]},
            {"op":"step"},{"op":"breakpoints","addresses":[257]},
            {"op":"frame"},{"op":"breakpoints","addresses":[257]},
            {"op":"run","maxInstructions":1},{"op":"run","maxInstructions":1}]}
          """));
        Assert.Equal(4, result.Results[1]["elapsedTicks"]!.GetValue<double>());
        Assert.Equal(0, result.Results[3]["count"]!.GetValue<int>());
        Assert.Equal(0, result.Results[5]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(16, result.Results[6]["elapsedTicks"]!.GetValue<double>());
        Assert.Equal(0x150, result.Registers.PC);
    }

    [Fact]
    public void DebugReadsWritesAssertionsAndRomPreservation()
    {
        var rom = Cartridge();
        var original = rom.ToArray();
        var result = RuntimeRunner.Debug(rom, Json("""
          {"version":1,"commands":[{"op":"breakpoints","addresses":[338,338]},
            {"op":"run","frames":1,"maxInstructions":20},
            {"op":"assert","registers":{"PC":338,"A":42}},
            {"op":"step"},{"op":"write","address":57344,"bytes":[119,136]},
            {"op":"read","address":49152,"length":2},
            {"op":"write","address":336,"bytes":[0]},
            {"op":"assert","registers":{"pc":341},"memory":[
              {"address":336,"bytes":[62]},{"address":49152,"bytes":[119,136]}]}]}
          """));
        Assert.True(result.Completed);
        Assert.True(result.AssertionsPassed);
        Assert.Equal(44, result.Ticks);
        Assert.Equal(3, result.Results[1]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(new[] { 119, 136 }, result.Results[5]["bytes"]!.Deserialize<int[]>());
        Assert.Equal(original, rom);
    }

    [Fact]
    public void FailedAssertionStopsBeforeLaterMutation()
    {
        var result = RuntimeRunner.Debug(Cartridge(), Json("""
          {"version":1,"commands":[{"op":"write","address":49152,"bytes":[18]},
            {"op":"assert","memory":[{"address":49152,"bytes":[52]}]},
            {"op":"write","address":49152,"bytes":[86]}]}
          """));
        Assert.False(result.Completed);
        Assert.False(result.AssertionsPassed);
        Assert.Equal("assertion-failed", result.StopReason);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(new[] { 18 }, result.Results[1]["assertions"]![0]!["actual"]!.Deserialize<int[]>());
    }

    [Fact]
    public void FrameAndInstructionBudgetsAreReportedSeparately()
    {
        var rom = Cartridge([0x18, 0xfe]);
        var framed = RuntimeRunner.Debug(rom, Json("""{"version":1,"commands":[{"op":"frame","count":2}]}"""));
        Assert.True(framed.Ticks >= 2 * EmulatorSession.FrameTicks);
        Assert.Equal("target", framed.StopReason);
        var run = RuntimeRunner.Debug(rom, Json("""{"version":1,"commands":[{"op":"run","frames":1,"maxInstructions":10000}]}"""));
        Assert.Equal("frame-budget", run.StopReason);
        Assert.True(run.Ticks >= EmulatorSession.FrameTicks);
        var limited = RuntimeRunner.Debug(rom, Json("""{"version":1,"commands":[{"op":"run","frames":1,"maxInstructions":2}]}"""));
        Assert.Equal("instruction-budget", limited.StopReason);
        Assert.Equal(20, limited.Ticks);
    }

    [Fact]
    public void EntireScriptIsValidatedBeforeOpeningRom()
    {
        var error = Assert.Throws<ArgumentException>(() => RuntimeRunner.Debug([], Json("""
          {"version":1,"commands":[{"op":"write","address":49152,"bytes":[1]},{"op":"typo"}]}
          """)));
        Assert.Contains("Unknown debug operation typo at command 1", error.Message);
    }

    [Fact]
    public void RunReportsActualEvidenceAndUnreachedBreakpoint()
    {
        var result = RuntimeRunner.Run(Cartridge(), new()
        {
            Frames = 1,
            Breakpoint = 0x155,
            Memory = [new(0xc000, 1)],
            Assertions = Json("""{"memory":[{"address":49152,"bytes":[42]}],"registers":{"A":42}}""")
        });
        Assert.True(result.BreakpointReached);
        Assert.True(result.AssertionsPassed);
        Assert.Equal("breakpoint", result.StopReason);
        Assert.Equal(44, result.Ticks);
        Assert.Equal(new[] { 42 }, result.Memory[0].Bytes);
        var limited = RuntimeRunner.Run(Cartridge(), new() { Breakpoint = 0xdead, MaxInstructions = 2 });
        Assert.False(limited.BreakpointReached);
        Assert.Equal("instruction-budget", limited.StopReason);
        Assert.Equal(20, limited.Ticks);
        var initial = RuntimeRunner.Run(Cartridge(), new() { Breakpoint = 0x100 });
        Assert.True(initial.BreakpointReached);
        Assert.Equal(0, initial.Ticks);
        Assert.Equal(0, initial.InstructionSteps);
    }

    [Fact]
    public void RunWithoutAssertionsHasBoundedFrameExecution()
    {
        var result = RuntimeRunner.Run(Cartridge([0x18, 0xfe]), new() { Frames = 2, Buttons = ["A", "left"] });
        Assert.True(result.AssertionsPassed);
        Assert.Null(result.BreakpointReached);
        Assert.True(result.ElapsedTicks >= 2 * EmulatorSession.FrameTicks);
        Assert.Matches("^[0-9a-f]{64}$", result.FramebufferSha256);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"commands\":[{\"op\":\"step\"}]}")]
    [InlineData("{\"version\":1,\"commands\":[]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"step\",\"count\":null}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"step\",\"count\":0}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"run\",\"frames\":null}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"run\",\"maxInstructions\":1000001}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"frame\",\"count\":3601}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"read\",\"address\":65535,\"length\":2}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"write\",\"address\":65535,\"bytes\":[0,1]}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"write\",\"address\":0,\"bytes\":[256]}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"breakpoints\",\"addresses\":[-1]}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"assert\"}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"assert\",\"registers\":{\"IME\":1}}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"assert\",\"registers\":{\"a\":1,\"A\":1}}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"step\",\"unexpected\":true}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"step\",\"count\":500001},{\"op\":\"run\",\"maxInstructions\":500000}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"frame\",\"count\":1801},{\"op\":\"run\",\"frames\":1800}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"step\",\"count\":1,\"count\":2}]}")]
    public void MalformedScriptsAreRejected(string script) => Assert.Throws<ArgumentException>(() => RuntimeRunner.ValidateDebugScript(Json(script)));

    [Fact]
    public void AggregateMemoryAndBreakpointBudgetsAreEnforced()
    {
        var commands = Enumerable.Repeat(new { op = "read", address = 0, length = 65536 }, 17);
        Assert.Throws<ArgumentException>(() => RuntimeRunner.ValidateDebugScript(JsonSerializer.SerializeToElement(new { version = 1, commands })));
        var points = new { version = 1, commands = new[] { new { op = "breakpoints", addresses = Enumerable.Range(0, 257).ToArray() } } };
        Assert.Throws<ArgumentException>(() => RuntimeRunner.ValidateDebugScript(JsonSerializer.SerializeToElement(points)));
    }

    [Fact]
    public void MalformedRunOptionsAreRejectedBeforeRomStartup()
    {
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Run([], new() { Frames = 0 }));
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Run([], new() { Breakpoint = -1 }));
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Run([], new() { Buttons = ["wrong"] }));
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Run([], new() { MaxInstructions = 0 }));
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Run([], new() { Memory = [new(0, 65536), new(0, 1)] }));
        Assert.Throws<ArgumentException>(() => RuntimeRunner.Run([], new() { Assertions = Json("{}") }));
    }
}
