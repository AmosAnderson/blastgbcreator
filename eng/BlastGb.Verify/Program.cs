using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Directory.GetCurrentDirectory());
if (!File.Exists(Path.Combine(root, "BlastGb.slnx")))
    throw new ArgumentException("Run from the repository root or pass its directory.");
var temporary = Path.Combine(Path.GetTempPath(), $"blast gb package {Guid.NewGuid():N}");
Directory.CreateDirectory(temporary);
try
{
    var packages = Path.Combine(temporary, "packages");
    await RunAsync("dotnet", ["pack", "src/BlastGb.Cli", "-c", "Release", "-o", packages], root);
    var package = Directory.GetFiles(packages, "BlastGb.Tools.*.nupkg").Single();
    using (var archive = ZipFile.OpenRead(package))
    {
        Require(archive.Entries.Any(entry => entry.FullName.EndsWith("BlastGb.Runtime.dll", StringComparison.Ordinal)), "runtime assembly missing");
        Require(archive.Entries.Any(entry => entry.FullName.EndsWith("Wasmtime.Dotnet.dll", StringComparison.Ordinal)), "Wasmtime binding missing");
        Require(archive.GetEntry("third_party/binjgb/LICENSE") is not null, "binjgb license missing");
        Require(archive.GetEntry("third_party/wasmtime/LICENSE") is not null, "Wasmtime license missing");
        Require(archive.GetEntry("examples/hello/source/main.asm") is not null, "assembly example missing");
        Require(!archive.Entries.Any(entry => entry.FullName.EndsWith(".js", StringComparison.Ordinal) || entry.FullName.EndsWith(".ts", StringComparison.Ordinal)), "legacy JavaScript/TypeScript in package");
    }
    var tool = Path.Combine(temporary, "installed");
    await RunAsync("dotnet", ["tool", "install", "BlastGb.Tools", "--version", "0.4.0", "--tool-path", tool, "--add-source", packages, "--ignore-failed-sources"], root);
    var executable = Path.Combine(tool, OperatingSystem.IsWindows() ? "gb.exe" : "gb");
    async Task<JsonNode> CommandAsync(params string[] command)
    {
        var output = await RunAsync(executable, [.. command, "--json"], temporary);
        var envelope = JsonNode.Parse(output)!;
        Require(envelope["ok"]!.GetValue<bool>(), output);
        return envelope["data"]!;
    }
    Require((await CommandAsync("version"))["version"]!.GetValue<string>() == "0.4.0", "package version");
    Require((await RunAsync(executable, ["--help"], temporary)).StartsWith("Blast GB Tools 0.4.0\n", StringComparison.Ordinal), "help version");
    Require((await CommandAsync("capabilities"))["commands"]!.AsArray().Any(item => item!.GetValue<string>() == "debug"), "debug discovery");
    foreach (var schema in new[] { "project", "debug", "assertions", "game", "suite", "build-receipt" })
        await CommandAsync("schema", schema);
    var project = Path.Combine(temporary, "sample game");
    await CommandAsync("project", "init", project, "--kind", "game");
    await CommandAsync("game", "validate", Path.Combine(project, "game.json"));
    await CommandAsync("game", "export", Path.Combine(project, "game.json"), Path.Combine(temporary, "source export"));
    var receipt = Path.Combine(project, "build", "receipt.json");
    var build = await CommandAsync("build", project, "--receipt", receipt);
    Require((await CommandAsync("project", "status", project, "--receipt", receipt))["valid"]!.GetValue<bool>(), "installed build receipt");
    var rom = build["output"]!.GetValue<string>();
    await CommandAsync("rom", "verify", rom);
    var assertions = Path.Combine(temporary, "checks.json");
    await File.WriteAllTextAsync(assertions, """{"memory":[{"address":49152,"bytes":[9]}]}""");
    var screenshot = Path.Combine(temporary, "final.png");
    var tested = await CommandAsync("test", rom, "--frames", "10", "--assert", assertions, "--screenshot", screenshot);
    Require(tested["assertionsPassed"]!.GetValue<bool>(), "installed runtime assertion");
    Require(File.ReadAllBytes(screenshot).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "installed PNG encoder");
    var script = Path.Combine(temporary, "debug.json");
    await File.WriteAllTextAsync(script, """
        {"version":1e0,"commands":[{"op":"breakpoints","addresses":[336]},{"op":"run","frames":1,"maxInstructions":100},
          {"op":"assert","registers":{"pc":336.0}},{"op":"breakpoints","addresses":[57005]},{"op":"frame","count":3.0}]}
        """);
    Require((await CommandAsync("debug", rom, "--script", script))["assertionsPassed"]!.GetValue<bool>(), "installed debugger assertion");
    await File.WriteAllTextAsync(script, """
        {"version":1,"commands":[{"op":"until","memory":[{"address":49152,"bytes":[9]}],"frames":10,"maxInstructions":100000},
          {"op":"buttons","pressed":["right"]},{"op":"frame","count":3},
          {"op":"buttons","pressed":[]},{"op":"capture","name":"ready"}]}
        """);
    await CommandAsync("debug", rom, "--script", script, "--capture-dir", Path.Combine(temporary, "captures"));
    var suite = Path.Combine(temporary, "suite.json");
    await File.WriteAllTextAsync(suite, """{"version":1,"cases":[{"name":"gameplay","script":"debug.json"},{"name":"boot","frames":10,"assert":"checks.json"}]}""");
    Require((await CommandAsync("project", "test", project, "--suite", suite, "--report", Path.Combine(temporary, "report.json")))["passed"]!.GetValue<int>() == 2, "installed project suite");
    var patch = Path.Combine(temporary, "empty-patch.json");
    await CommandAsync("rom", "patch-create", rom, rom, "--output", patch);
    await CommandAsync("rom", "patch", rom, patch, "--output", Path.Combine(temporary, "copy.gb"));
    var pixels = Path.Combine(temporary, "pixels.json");
    await File.WriteAllTextAsync(pixels, "[" + string.Join(',', Enumerable.Repeat(0, 64)) + "]");
    var tiles = Path.Combine(temporary, "tiles.2bpp");
    await CommandAsync("asset", "encode", pixels, "--output", tiles);
    await CommandAsync("asset", "decode", tiles, "--output", Path.Combine(temporary, "decoded.json"));
    var imported = Path.Combine(temporary, "mod project");
    await CommandAsync("project", "import", rom, imported);
    Require(File.ReadAllBytes(Path.Combine(imported, "original.gb")).SequenceEqual(File.ReadAllBytes(rom)), "import preserves original bytes");
    await CommandAsync("project", "resolve", imported);
    Console.WriteLine("PASS: .NET tool package installs locally and executes authoring, graphics, receipts, suites, screenshots, and scripted runtime utilities without Node.");
}
finally
{
    Directory.Delete(temporary, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<string> RunAsync(string executable, string[] arguments, string workingDirectory)
{
    var start = new ProcessStartInfo(executable)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException($"{executable} exceeded the verification timeout.");
    }
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{executable} exited {process.ExitCode}:\n{await error}\n{await output}");
    return await output;
}
