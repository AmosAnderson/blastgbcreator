using System.Diagnostics;

namespace BlastGb.Core.Tests;

public sealed class RgbdsFactAttribute : FactAttribute
{
    public RgbdsFactAttribute()
    {
        if (!RgbdsFixture.IsAvailable) Skip = "Set RGBDS_BIN to run the real RGBDS toolchain check.";
    }
}

public sealed class RgbdsTheoryAttribute : TheoryAttribute
{
    public RgbdsTheoryAttribute()
    {
        if (!RgbdsFixture.IsAvailable) Skip = "Set RGBDS_BIN to run the real RGBDS toolchain check.";
    }
}

internal sealed class RgbdsFixture : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("blast-core-rgbds-").FullName;
    public static bool IsAvailable => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RGBDS_BIN"));

    public byte[] Assemble(string source)
    {
        var sourcePath = Path.Combine(_directory, "main.asm");
        var objectPath = Path.Combine(_directory, "game.o");
        var romPath = Path.Combine(_directory, "game.gb");
        File.WriteAllText(sourcePath, source);
        Run("rgbasm", "-o", objectPath, sourcePath);
        Run("rgblink", "-o", romPath, objectPath);
        return File.ReadAllBytes(romPath);
    }

    private static void Run(string name, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("RGBDS_BIN")!, name)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException($"{name} timed out."); }
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
