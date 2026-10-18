using System.Text.Json;
using BlastGb.Runtime;

namespace BlastGb.Runtime.Tests;

public sealed class EmulatorTests
{
    internal static byte[] Cartridge(byte[]? program = null)
    {
        var rom = new byte[32768];
        new byte[] { 0, 0xc3, 0x50, 0x01 }.CopyTo(rom, 0x100);
        Convert.FromHexString("CEED6666CC0D000B03730083000C000D0008111F8889000EDCCC6EE6DDDDD999BBBB67636E0EECCC DD DC999FBBB9333E".Replace(" ", "")).CopyTo(rom, 0x104);
        System.Text.Encoding.ASCII.GetBytes("RUNTIME TEST").CopyTo(rom, 0x134);
        rom[0x147] = 0;
        rom[0x148] = 0;
        rom[0x149] = 0;
        (program ?? [0x3e, 0x2a, 0xea, 0x00, 0xc0, 0x3c, 0x18, 0xfd]).CopyTo(rom, 0x150);
        byte header = 0;
        for (int index = 0x134; index <= 0x14c; index++) header = unchecked((byte)(header - rom[index] - 1));
        rom[0x14d] = header;
        int sum = rom.Sum(value => value);
        rom[0x14e] = (byte)(sum >> 8);
        rom[0x14f] = (byte)sum;
        return rom;
    }
    internal static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    [Fact]
    public void ActualCpuStepsExposeRegistersAndClock()
    {
        using var session = new EmulatorSession(Cartridge());
        Assert.Equal(new CpuRegisters(1, 0xb0, 0x1b0, 0x13, 0xd8, 0x14d, 0xfffe, 0x100), session.Registers);
        Assert.Equal(0x101, session.Advance(1).Registers.PC);
        Assert.Equal(4, session.Ticks);
        Assert.Equal(0x150, session.Advance(1).Registers.PC);
        Assert.Equal(20, session.Ticks);
        var loaded = session.Advance(1);
        Assert.Equal(0x152, loaded.Registers.PC);
        Assert.Equal(42, loaded.Registers.A);
        Assert.Equal(28, loaded.Ticks);
        session.Advance(1);
        Assert.Equal(new byte[] { 42 }, session.ReadMemory(0xc000));
    }

    [Fact]
    public void MappedBusWritesPreserveCartridgeAndResolveEchoRam()
    {
        var rom = Cartridge();
        var before = rom.ToArray();
        using var session = new EmulatorSession(rom);
        session.WriteMemory(0xc000, [0x12, 0x34]);
        Assert.Equal(new byte[] { 0x12, 0x34 }, session.ReadMemory(0xe000, 2));
        session.WriteMemory(0xff80, [0x77]);
        Assert.Equal(0x77, session.ReadMemory(0xff80)[0]);
        session.WriteMemory(0x150, [0]);
        Assert.Equal(0x3e, session.ReadMemory(0x150)[0]);
        Assert.Equal(before, rom);
        Assert.Throws<ArgumentException>(() => session.ReadMemory(-1));
        Assert.Throws<ArgumentException>(() => session.ReadMemory(0xffff, 2));
        Assert.Throws<ArgumentException>(() => session.WriteMemory(0xffff, [1, 2]));
        Assert.Throws<ArgumentException>(() => session.SetBreakpoints([65536]));
        Assert.Throws<ArgumentException>(() => session.Advance(double.PositiveInfinity));
    }

    [Fact]
    public void BreakpointsStopBeforeExecutionAndContinueOnce()
    {
        using var session = new EmulatorSession(Cartridge());
        session.SetBreakpoints([0x152, 0x152]);
        var stopped = session.Advance(EmulatorSession.FrameTicks);
        Assert.Equal("breakpoint", stopped.StopReason);
        Assert.Equal(28, stopped.Ticks);
        Assert.Equal(new[] { 0x152 }, stopped.Breakpoints);
        Assert.Equal(0x155, session.Advance(1).Registers.PC);
        Assert.Equal(42, session.ReadMemory(0xc000)[0]);
        session.SetBreakpoints([]);
        Assert.Equal("target", session.Advance(EmulatorSession.FrameTicks).StopReason);
    }

    [Fact]
    public void FractionalTickBudgetRoundsUp()
    {
        using var session = new EmulatorSession(Cartridge());
        session.SetBreakpoints([0x4000]);
        Assert.Equal(20, session.Advance(4.5).Ticks);
    }

    [Fact]
    public void RealVideoAndAudioEventsAndInvalidOpcode()
    {
        using var session = new EmulatorSession(Cartridge([0x3e, 0, 0xe0, 0x40, 0x3e, 0x91, 0xe0, 0x40, 0x18, 0xfe]));
        int frames = 0, audio = 0;
        var state = session.Advance(3 * EmulatorSession.FrameTicks, () => frames++, () => audio++);
        Assert.InRange(state.Ticks, 3 * EmulatorSession.FrameTicks, 3 * EmulatorSession.FrameTicks + 23);
        Assert.True(frames > 0);
        Assert.True(audio > 0);
        Assert.Equal(160 * 144 * 4, session.Framebuffer().Length);
        Assert.Matches("^[0-9a-f]{64}$", session.FramebufferSha256());
        using var invalid = new EmulatorSession(Cartridge([0xd3]));
        Assert.Contains("invalid CPU instruction", Assert.Throws<InvalidOperationException>(() => invalid.Advance(100)).Message);
    }

    [Fact]
    public void DisposableSessionsAreIsolated()
    {
        var first = new EmulatorSession(Cartridge());
        using var second = new EmulatorSession(Cartridge());
        first.WriteMemory(0xc000, [0x12]);
        second.WriteMemory(0xc000, [0x34]);
        Assert.Equal(0x12, first.ReadMemory(0xc000)[0]);
        Assert.Equal(0x34, second.ReadMemory(0xc000)[0]);
        first.Dispose();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Snapshot());
        Assert.Equal(0x34, second.ReadMemory(0xc000)[0]);
    }
}
