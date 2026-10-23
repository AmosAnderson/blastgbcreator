using System.Text;
using BlastGb.Cli;

namespace BlastGb.Cli.Tests;

public sealed class AssemblyDependencyEncodingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast-gb-encoding-" + Guid.NewGuid().ToString("N"));
    private readonly string target = AssemblyDependencies.CreateTarget();
    private static Encoding Windows1252 => CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    public AssemblyDependencyEncodingTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string FileAt(string name) => Path.Combine(directory, name);
    private string Report(params string[] names) => string.Concat(names.Select(name => $"{target}: {name}\r\n"));
    private string CreateFile(string name, string text = "")
    {
        var path = FileAt(name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void WindowsCodePageSourceIsAcceptedWithoutUtf8ReplacementCharacters()
    {
        var source = CreateFile("café.asm");
        var input = CreateFile("entrée.inc");
        var report = Windows1252.GetBytes(Report(source, input));

        Assert.Equal(new[] { source, input }, AssemblyDependencies.Parse(report, target, directory, source, Windows1252));
    }

    [Fact]
    public void WindowsUtf8CodePageRemainsSupported()
    {
        var source = CreateFile("café.asm");
        var input = CreateFile("entrée.inc");
        var report = Encoding.UTF8.GetBytes(Report(source, input));

        Assert.Equal(new[] { source, input }, AssemblyDependencies.Parse(report, target, directory, source, AssemblyDependencies.WindowsEncoding(65001)));
    }

    [Fact]
    public void UnavailableWindowsCodePageFailsClosed()
    {
        var error = Assert.Throws<CliException>(() => AssemblyDependencies.WindowsEncoding(65535));
        Assert.Contains("active Windows code page is unavailable", error.Message);
    }

    [Fact]
    public void Utf8SourceMustMatchExactlyBeforeChoosingAnInterpretation()
    {
        var source = CreateFile("café.asm");
        CreateFile("cafÃ©.asm");
        var input = CreateFile("input.inc");
        var report = Encoding.UTF8.GetBytes(Report(source, input));

        Assert.Equal(new[] { source, input }, AssemblyDependencies.Parse(report, target, directory, source, Windows1252));
    }

    [Fact]
    public void DifferentExistingUtf8AndWindowsDependenciesAreAmbiguous()
    {
        var source = CreateFile("main.asm");
        CreateFile("café.inc");
        CreateFile("cafÃ©.inc");
        var report = Encoding.UTF8.GetBytes(Report(source, "café.inc"));

        var error = Assert.Throws<CliException>(() => AssemblyDependencies.Parse(report, target, directory, source, Windows1252));
        Assert.Equal("BUILD_FAILED", error.Code);
        Assert.Contains("ambiguous UTF-8 and Windows code-page paths", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyACompleteExistingInterpretationCanBeUsed(bool utf8)
    {
        var source = CreateFile("main.asm");
        var input = CreateFile(utf8 ? "café.inc" : "cafÃ©.inc");
        var report = Encoding.UTF8.GetBytes(Report(source, "café.inc"));

        Assert.Equal(new[] { source, input }, AssemblyDependencies.Parse(report, target, directory, source, Windows1252));
    }

    [Fact]
    public void MixedEncodingsCannotBeCombinedIntoAnApparentlyValidReport()
    {
        var source = CreateFile("café.asm");
        CreateFile("entrée.inc");
        var report = Windows1252.GetBytes(Report(source)).Concat(Encoding.UTF8.GetBytes(Report("entrée.inc"))).ToArray();

        Assert.Throws<CliException>(() => AssemblyDependencies.Parse(report, target, directory, source, Windows1252));
    }

    [Fact]
    public void IdenticalAsciiInterpretationsAreAccepted()
    {
        var source = CreateFile("main.asm");
        var input = CreateFile("input.inc");
        var report = Encoding.ASCII.GetBytes(Report(source, input, input));

        Assert.Equal(new[] { source, input }, AssemblyDependencies.Parse(report, target, directory, source, Windows1252));
    }

    [Fact]
    public void NoncanonicalWindowsBytesCannotFallBackToAnUnrelatedUtf8Filename()
    {
        var source = CreateFile("main.asm");
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
        byte[] name = [0xed, 0x80, 0xa1];
        // Windows accepts these CP932 bytes for U+FA10 U+FF61, whose canonical
        // encoding is FA 9C A1. The same report bytes are valid UTF-8 for U+D021.
        Assert.Equal("\ufa10\uff61", encoding.GetString(name));
        Assert.False(name.SequenceEqual(encoding.GetBytes(encoding.GetString(name))));
        CreateFile("\ufa10\uff61.inc");
        CreateFile("\ud021.inc");
        var report = Encoding.ASCII.GetBytes(Report(source) + target + ": ")
            .Concat(name).Concat(Encoding.ASCII.GetBytes(".inc\r\n")).ToArray();

        var error = Assert.Throws<CliException>(() => AssemblyDependencies.Parse(report, target, directory, source, encoding));
        Assert.Contains("active Windows code page", error.Message);
    }

    [NativeRgbdsFact]
    public async Task NativeAmbiguousDependencyNamesCannotReplaceConsumedInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8;
        const string utf8Name = "café.inc";
        var windowsName = encoding.GetString(Encoding.UTF8.GetBytes(utf8Name));
        if (windowsName == utf8Name) return; // A UTF-8 Windows ACP has no second interpretation.
        const string assembly = "SECTION \"ROM\", ROM0[0]\nds $4000, 0\n";
        var source = CreateFile("main.asm", $"INCLUDE \"{utf8Name}\"\n");
        var utf8Input = CreateFile(utf8Name, assembly);
        var windowsInput = CreateFile(windowsName, assembly);
        var symbols = Path.ChangeExtension(windowsInput, ".sym");
        var map = Path.ChangeExtension(windowsInput, ".map");
        File.WriteAllText(symbols, "previous symbols");
        File.WriteAllText(map, "previous map");
        var paths = new[] { source, utf8Input, windowsInput, symbols, map };
        var original = paths.Select(File.ReadAllBytes).ToArray();

        var built = await CliTests.Invoke("asm", "compile", source, "--output", windowsInput, "--overwrite");

        Assert.Equal(1, built.Status);
        Assert.Equal("BUILD_FAILED", built.Json["error"]!["code"]!.GetValue<string>());
        Assert.Contains("ambiguous", built.Json["error"]!["message"]!.GetValue<string>());
        for (var index = 0; index < paths.Length; index++) Assert.Equal(original[index], File.ReadAllBytes(paths[index]));
    }
}
