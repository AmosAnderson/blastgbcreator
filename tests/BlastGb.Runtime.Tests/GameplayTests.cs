using BlastGb.Core;
using BlastGb.Runtime;

namespace BlastGb.Runtime.Tests;

public sealed class GameplayTests
{
    private static void Frames(EmulatorSession session, int count) => session.Advance(count * EmulatorSession.FrameTicks);
    private static void Press(EmulatorSession session, string button, int count)
    {
        session.SetButton(button, true);
        Frames(session, count);
        session.SetButton(button, false);
    }

    [Theory]
    [InlineData("gb")]
    [InlineData("gbc")]
    public void CompiledGameSupportsCollisionCollectionVictoryAndRestart(string mode)
    {
        var map = Enumerable.Range(0, 18).Select(y => y is 0 or 17 ? new string('#', 20) : $"#{new string('.', 18)}#").ToArray();
        map[1] = "#PC................#";
        var spec = GameCompiler.CreateStarterSpec() with { Mode = mode, Map = map };
        using var session = new EmulatorSession(GameCompiler.Build(spec).Rom);
        Frames(session, 10);
        Assert.Equal(1, session.ReadMemory(0xc000)[0]);
        Assert.Equal(1, session.ReadMemory(0xc001)[0]);
        Assert.Equal(1, session.ReadMemory(0xc003)[0]);
        Assert.Equal(mode == "gbc" ? 1 : 0, session.ReadMemory(0xc004)[0]);
        Assert.True(session.Framebuffer().Distinct().Count() > 3);
        Press(session, "left", 8);
        Assert.Equal(1, session.ReadMemory(0xc000)[0]);
        Frames(session, 8);
        Press(session, "right", 8);
        Assert.Equal(2, session.ReadMemory(0xc000)[0]);
        Assert.Equal(0, session.ReadMemory(0xc003)[0]);
        Assert.Equal(1, session.ReadMemory(0xc005)[0]);
        Assert.Equal(0, session.ReadMemory(0xc122)[0]);
        Press(session, "down", 8);
        Assert.Equal(1, session.ReadMemory(0xc001)[0]);
        Press(session, "start", 1);
        Frames(session, 10);
        Assert.Equal(1, session.ReadMemory(0xc000)[0]);
        Assert.Equal(1, session.ReadMemory(0xc003)[0]);
        Assert.Equal(0, session.ReadMemory(0xc005)[0]);
    }

    [Fact]
    public void OpenEdgesAndTwoDigitCoinCountsExecuteCorrectly()
    {
        var map = Enumerable.Repeat(new string('.', 20), 18).ToArray();
        map[0] = "P...................";
        map[1] = "CCCCCCCCCCCC........";
        using var session = new EmulatorSession(GameCompiler.Build(GameCompiler.CreateStarterSpec() with { Map = map }).Rom);
        Frames(session, 10);
        Assert.Equal(0, session.ReadMemory(0xffff)[0]);
        Assert.Equal(12, session.ReadMemory(0xc003)[0]);
        Assert.Equal(5, session.ReadMemory(0xfe26)[0]);
        Assert.Equal(6, session.ReadMemory(0xfe2a)[0]);
        Press(session, "left", 8);
        Press(session, "up", 8);
        Assert.Equal(0, session.ReadMemory(0xc000)[0]);
        Assert.Equal(0, session.ReadMemory(0xc001)[0]);
        Frames(session, 8);
        Press(session, "down", 1);
        Frames(session, 1);
        Assert.Equal(11, session.ReadMemory(0xc003)[0]);
        Assert.Equal(5, session.ReadMemory(0xfe26)[0]);
        Assert.Equal(5, session.ReadMemory(0xfe2a)[0]);
    }
}
