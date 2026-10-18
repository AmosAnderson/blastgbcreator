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
    await CommandAsync("schema", "project");
    var project = Path.Combine(temporary, "sample game");
    await CommandAsync("project", "init", project, "--kind", "game");
    var build = await CommandAsync("build", project);
    var rom = build["output"]!.GetValue<string>();
    await CommandAsync("rom", "verify", rom);
    var assertions = Path.Combine(temporary, "checks.json");
    await File.WriteAllTextAsync(assertions, """{"memory":[{"address":49152,"bytes":[9]}]}""");
    var tested = await CommandAsync("test", rom, "--frames", "10", "--assert", assertions);
    Require(tested["assertionsPassed"]!.GetValue<bool>(), "installed runtime assertion");
    var script = Path.Combine(temporary, "debug.json");
    await File.WriteAllTextAsync(script, """
        {"version":1e0,"commands":[{"op":"breakpoints","addresses":[336]},{"op":"run","frames":1,"maxInstructions":100},
          {"op":"assert","registers":{"pc":336.0}},{"op":"breakpoints","addresses":[57005]},{"op":"frame","count":3.0}]}
        """);
    Require((await CommandAsync("debug", rom, "--script", script))["assertionsPassed"]!.GetValue<bool>(), "installed debugger assertion");
    var imported = Path.Combine(temporary, "mod project");
    await CommandAsync("project", "import", rom, imported);
    Require(File.ReadAllBytes(Path.Combine(imported, "original.gb")).SequenceEqual(File.ReadAllBytes(rom)), "import preserves original bytes");
    await CommandAsync("project", "resolve", imported);
    Console.WriteLine("PASS: .NET tool package installs locally and executes ROM, build, test, and debugger utilities without Node.");
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
