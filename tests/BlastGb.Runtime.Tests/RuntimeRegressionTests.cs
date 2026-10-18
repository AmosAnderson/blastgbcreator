using BlastGb.Runtime;
using static BlastGb.Runtime.Tests.EmulatorTests;

namespace BlastGb.Runtime.Tests;

public sealed class RuntimeRegressionTests
{
    [Fact]
    public void PendingAudioEventsDoNotConsumeInstructionBudgets()
    {
        // The entry NOP/JP cost 20 ticks. Each NOP/JR loop costs 16 ticks.
        // This crosses several audio-buffer boundaries, including event-only stops.
        var rom = Cartridge([0x00, 0x18, 0xfd]);
        var run = RuntimeRunner.Run(rom, new() { Frames = 60, Breakpoint = 0xdead, MaxInstructions = 100_000 });
        Assert.Equal("instruction-budget", run.StopReason);
        Assert.Equal(100_000, run.InstructionSteps);
        Assert.Equal(800_004, run.Ticks);
        Assert.Equal(0x150, run.Registers.PC);

        var stepped = RuntimeRunner.Debug(rom, Json("""
            {"version":1,"commands":[{"op":"step","count":100000}]}
            """));
        Assert.Equal(100_000, stepped.Results[0]["count"]!.GetValue<int>());
        Assert.Equal(run.Ticks, stepped.Ticks);
        Assert.Equal(run.Registers, stepped.Registers);

        var continued = RuntimeRunner.Debug(rom, Json("""
            {"version":1,"commands":[{"op":"run","frames":60,"maxInstructions":100000}]}
            """));
        Assert.Equal("instruction-budget", continued.StopReason);
        Assert.Equal(100_000, continued.Results[0]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(run.Ticks, continued.Ticks);
        Assert.Equal(run.Registers, continued.Registers);
        Assert.Equal(run.Frames, stepped.Results[0]["frames"]!.GetValue<int>());
        Assert.Equal(run.Frames, continued.Results[0]["frames"]!.GetValue<int>());
    }

    [Fact]
    public void DebugFramesWithBreakpointsMatchBatchedExecutionAcrossPendingEvents()
    {
        var rom = Cartridge([0x00, 0x18, 0xfd]);
        var debug = RuntimeRunner.Debug(rom, Json("""
            {"version":1,"commands":[{"op":"breakpoints","addresses":[57005]},
              {"op":"frame","count":10}]}
            """));
        using var session = new EmulatorSession(rom);
        int frames = 0, audio = 0;
        var batched = session.Advance(10 * EmulatorSession.FrameTicks, () => frames++, () => audio++);
        Assert.True(debug.Completed);
        Assert.Equal("target", debug.StopReason);
        Assert.Equal(10, debug.Results[1]["count"]!.GetValue<int>());
        Assert.Equal(batched.Ticks, debug.Ticks);
        Assert.Equal(batched.Registers, debug.Registers);
        Assert.Equal(frames, debug.Results[1]["frames"]!.GetValue<int>());
        Assert.True(audio >= 3);
        Assert.Equal(session.FramebufferSha256(), debug.FramebufferSha256);
    }

    [Fact]
    public void ResumingBreakpointWithPendingAudioExecutesTheNextInstruction()
    {
        var result = RuntimeRunner.Debug(Cartridge([0x00, 0x18, 0xfd]), Json("""
            {"version":1,"commands":[{"op":"step","count":22370},
              {"op":"breakpoints","addresses":[336]},
              {"op":"run","frames":1,"maxInstructions":1},
              {"op":"run","frames":1,"maxInstructions":1}]}
            """));
        Assert.Equal(178_964, result.Results[0]["ticks"]!.GetValue<double>());
        Assert.Equal("breakpoint", result.Results[2]["stopReason"]!.GetValue<string>());
        Assert.Equal(0, result.Results[2]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(1, result.Results[3]["instructionSteps"]!.GetValue<int>());
        Assert.Equal(4, result.Results[3]["elapsedTicks"]!.GetValue<double>());
        Assert.Equal("instruction-budget", result.StopReason);
        Assert.Equal(0x151, result.Registers.PC);
    }

    [Theory]
    [InlineData("gb")]
    [InlineData("gbc")]
    public void FullFrameBudgetCompletesOnOriginalHomebrew(string mode)
    {
        var rom = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{mode}.gb"));
        var result = RuntimeRunner.Run(rom, new() { Frames = 3600 });
        Assert.Equal("frame-budget", result.StopReason);
        Assert.InRange(result.ElapsedTicks, 3600 * EmulatorSession.FrameTicks, 3600 * EmulatorSession.FrameTicks + 24);
        Assert.True(result.AssertionsPassed);
    }

    [Fact]
    public void CancellationOnPendingAudioEventStopsBeforeAnotherCpuStep()
    {
        var session = new EmulatorSession(Cartridge([0x00, 0x18, 0xfd]));
        using var cancellation = new CancellationTokenSource();
        try
        {
            session.SetBreakpoints([0xdead]);
            int audio = 0;
            var error = Assert.Throws<OperationCanceledException>(() => session.Advance(10 * EmulatorSession.FrameTicks,
                audio: () => { audio++; cancellation.Cancel(); }, cancellationToken: cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(1, audio);
            Assert.InRange(session.Ticks, 2 * EmulatorSession.FrameTicks, 3 * EmulatorSession.FrameTicks);
            Assert.Equal(0x150, session.Registers.PC);
            Assert.Equal(new byte[] { 0, 0x18, 0xfd }, session.ReadMemory(0x150, 3));
        }
        finally
        {
            session.Dispose();
        }
        Assert.Throws<ObjectDisposedException>(() => session.Snapshot());
    }

    [Fact]
    public void FrameCallbackCancellationAndCancelledMemoryWritesPreserveSession()
    {
        using var session = new EmulatorSession(Cartridge());
        using var cancellation = new CancellationTokenSource();
        int frames = 0;
        Assert.Throws<OperationCanceledException>(() => session.Advance(EmulatorSession.ClockRate,
            frame: () => { frames++; cancellation.Cancel(); }, cancellationToken: cancellation.Token));
        Assert.Equal(1, frames);
        Assert.True(session.Ticks < EmulatorSession.ClockRate);
        var before = session.ReadMemory(0xc000);
        Assert.Throws<OperationCanceledException>(() => session.WriteMemory(0xc000, [99], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => session.ReadMemory(0xc000, cancellationToken: cancellation.Token));
        Assert.Equal(before, session.ReadMemory(0xc000));
    }

    [Fact]
    public void PreCancelledOperationsStopBeforeRomAllocationOrScriptValidation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RuntimeRunner.Run([], new(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => RuntimeRunner.Debug([], Json("null"), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedExecutionObservesCancellation(bool debug)
    {
        var rom = Cartridge([0x00, 0x18, 0xfd]);
        var original = rom.ToArray();
        using (var warmup = new EmulatorSession(rom)) { }
        using var cancellation = new CancellationTokenSource();
        var execution = Task.Run(() =>
        {
            if (debug)
                RuntimeRunner.Debug(rom, Json("""
                    {"version":1,"commands":[{"op":"breakpoints","addresses":[57005]},
                      {"op":"frame","count":3600}]}
                    """), cancellation.Token);
            else
                RuntimeRunner.Run(rom, new() { Frames = 3600 }, cancellation.Token);
        });
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(original, rom);
        Assert.Equal("frame-budget", RuntimeRunner.Run(rom, new() { Frames = 1 }).StopReason);
    }
}
