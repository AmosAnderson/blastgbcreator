using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Directory.GetCurrentDirectory());
if (!File.Exists(Path.Combine(root, "BlastGb.slnx")))
    throw new ArgumentException("Run from the repository root or pass its directory.");
var temporary = Path.Combine(Path.GetTempPath(), $"blast gb package {Guid.NewGuid():N}");
Directory.CreateDirectory(temporary);
try
{
    var packages = Path.Combine(temporary, "packages");
    Console.WriteLine("VERIFY: pack the tool with locked dependencies.");
    await RunAsync("dotnet", ["pack", "src/BlastGb.Cli", "-c", "Release", "-p:RestoreLockedMode=true", "-o", packages], root);
    var package = Directory.GetFiles(packages, "BlastGb.Tools.*.nupkg").Single();
    var packagedExample = Path.Combine(temporary, "packaged hello");
    using (var archive = ZipFile.OpenRead(package))
    {
        Require(archive.Entries.Any(entry => entry.FullName.EndsWith("BlastGb.Runtime.dll", StringComparison.Ordinal)), "runtime assembly missing");
        Require(archive.Entries.Any(entry => entry.FullName.EndsWith("Wasmtime.Dotnet.dll", StringComparison.Ordinal)), "Wasmtime binding missing");
        Require(archive.GetEntry("third_party/binjgb/LICENSE") is not null, "binjgb license missing");
        Require(archive.GetEntry("third_party/wasmtime/LICENSE") is not null, "Wasmtime license missing");
        Require(archive.GetEntry("examples/hello/source/main.asm") is not null, "assembly example missing");
        Require(!archive.Entries.Any(entry => entry.FullName.EndsWith(".js", StringComparison.Ordinal) || entry.FullName.EndsWith(".ts", StringComparison.Ordinal)), "legacy JavaScript/TypeScript in package");
        foreach (string name in new[] { "gb-project.json", "checks.json", "debug.json", "tests.json", "input-sequence.json", "gb-tests.json", "source/main.asm" })
        {
            var destination = Path.Combine(packagedExample, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            (archive.GetEntry("examples/hello/" + name) ?? throw new InvalidOperationException($"Packaged example lacks {name}.")).ExtractToFile(destination);
        }
    }
    var tool = Path.Combine(temporary, "installed");
    Console.WriteLine("VERIFY: install into a temporary directory and exercise the installed commands.");
    await RunAsync("dotnet", ["tool", "install", "BlastGb.Tools", "--version", "0.4.1", "--tool-path", tool, "--add-source", packages, "--ignore-failed-sources"], root);
    var executable = Path.Combine(tool, OperatingSystem.IsWindows() ? "gb.exe" : "gb");
    async Task<JsonNode> CommandAsync(params string[] command)
    {
        var output = await RunAsync(executable, [.. command, "--json"], temporary);
        var envelope = JsonNode.Parse(output)!;
        Require(envelope["ok"]!.GetValue<bool>(), output);
        return envelope["data"]!;
    }
    Require((await CommandAsync("version"))["version"]!.GetValue<string>() == "0.4.1", "package version");
    Require((await RunAsync(executable, ["--help"], temporary)).StartsWith("Blast GB Tools 0.4.1\n", StringComparison.Ordinal), "help version");
    Require((await CommandAsync("capabilities"))["commands"]!.AsArray().Any(item => item!.GetValue<string>() == "debug"), "debug discovery");
    foreach (var schema in new[] { "project", "debug", "assertions", "game", "suite", "build-receipt" })
        await CommandAsync("schema", schema);
    var doctor = await CommandAsync("doctor");
    Require(doctor["runtimeHealthy"]!.GetValue<bool>(), "installed runtime health probe");
    Require(doctor["engine"]!["engine"]!.GetValue<string>() == "binjgb", "installed engine identity");
    Require(doctor["engine"]!["engineSha256"]!.GetValue<string>() == "52c165069441a09180b4bd63464318aa7b861b1d36e0d05e8047b48619fd2f0b", "installed immutable engine hash");
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
    var capturedScreenshot = Path.Combine(temporary, "frame.png");
    var captured = await CommandAsync("run", rom, "--frames", "10", "--screenshot", capturedScreenshot, "--provenance");
    string pixelHash = Convert.ToHexStringLower(SHA256.HashData(ReadScreenshotPixels(File.ReadAllBytes(capturedScreenshot))));
    Require(captured["framebufferSha256"]!.GetValue<string>() == pixelHash, "screenshot pixels match framebuffer hash");
    Require(captured["toolkitVersion"]!.GetValue<string>() == "0.4.1", "execution toolkit provenance");
    Require(JsonNode.DeepEquals(captured["provenance"], doctor["engine"]), "execution engine provenance");
    var framebufferAssertions = Path.Combine(temporary, "framebuffer-checks.json");
    await File.WriteAllTextAsync(framebufferAssertions, new JsonObject { ["framebufferSha256"] = pixelHash }.ToJsonString());
    Require((await CommandAsync("test", rom, "--frames", "10", "--assert", framebufferAssertions))["assertionsPassed"]!.GetValue<bool>(), "installed framebuffer assertion");
    var script = Path.Combine(temporary, "debug.json");
    await File.WriteAllTextAsync(script, """
        {"version":1e0,"commands":[{"op":"breakpoints","addresses":[336]},{"op":"run","frames":1,"maxInstructions":100},
          {"op":"assert","registers":{"pc":336.0}},{"op":"breakpoints","addresses":[57005]},{"op":"frame","count":3.0}]}
        """);
    Require((await CommandAsync("debug", rom, "--script", script))["assertionsPassed"]!.GetValue<bool>(), "installed debugger assertion");
    var buttonScript = Path.Combine(project, "buttons.json");
    await File.WriteAllTextAsync(buttonScript, """
        {"version":1,"commands":[{"op":"frame","count":10},
          {"op":"buttons","pressed":["right"]},{"op":"frame","count":1},
          {"op":"buttons","pressed":[]},{"op":"frame","count":8},
          {"op":"assert","memory":[{"address":49152,"bytes":[10,8]}]}]}
        """);
    var played = await CommandAsync("debug", rom, "--script", buttonScript);
    Require(played["assertionsPassed"]!.GetValue<bool>(), "scripted button input and release affect the installed runtime");
    Require(played["results"]![1]!["pressed"]!.AsArray().Single()!.GetValue<string>() == "right", "script records pressed buttons");
    Require(played["results"]![3]!["pressed"]!.AsArray().Count == 0, "script records button release");
    await File.WriteAllTextAsync(Path.Combine(project, "checks.json"), await File.ReadAllTextAsync(assertions));
    string suiteRom = Path.GetRelativePath(project, rom).Replace(Path.DirectorySeparatorChar, '/');
    var suiteJson = new JsonObject
    {
        ["version"] = 1,
        ["cases"] = new JsonArray(
            new JsonObject { ["name"] = "boot", ["rom"] = suiteRom, ["assert"] = "checks.json", ["frames"] = 10 },
            new JsonObject { ["name"] = "scripted movement", ["rom"] = suiteRom, ["script"] = "buttons.json" })
    };
    await File.WriteAllTextAsync(Path.Combine(project, "gb-tests.json"), suiteJson.ToJsonString());
    var suite = await CommandAsync("project", "test", project, "--build", "--overwrite");
    Require(suite["passed"]!.GetValue<int>() == 2 && suite["failed"]!.GetValue<int>() == 0 && suite["build"] is not null, "installed suite builds and executes every case");

    var modified = Path.Combine(temporary, "modified.gb");
    byte[] changed = File.ReadAllBytes(rom);
    for (int index = 0; index < 400; index++) changed[0x2000 + index] ^= 0xff;
    for (int index = 0; index < 100; index++) changed[0x3000 + index * 2] ^= 0xff;
    await File.WriteAllBytesAsync(modified, changed);
    var patch = Path.Combine(temporary, "generated-patches.json");
    var difference = await CommandAsync("rom", "diff", rom, modified, "--patch-output", patch);
    Require(difference["rangesTruncated"]!.GetValue<bool>() && difference["patchCount"]!.GetValue<int>() == 101, "patch export includes changes beyond preview limits");
    var patched = Path.Combine(temporary, "roundtrip.gb");
    await CommandAsync("rom", "patch", rom, patch, "--output", patched);
    Require(File.ReadAllBytes(patched).SequenceEqual(changed), "installed guarded patch export round-trip");
    await File.WriteAllTextAsync(script, """
        {"version":1,"commands":[{"op":"until","memory":[{"address":49152,"bytes":[9]}],"frames":10,"maxInstructions":100000},
          {"op":"buttons","pressed":["right"]},{"op":"frame","count":3},
          {"op":"buttons","pressed":[]},{"op":"capture","name":"ready"}]}
        """);
    await CommandAsync("debug", rom, "--script", script, "--capture-dir", Path.Combine(temporary, "captures"));
    var suitePath = Path.Combine(temporary, "suite.json");
    await File.WriteAllTextAsync(suitePath, """{"version":1,"cases":[{"name":"gameplay","script":"debug.json"},{"name":"boot","frames":10,"assert":"checks.json"}]}""");
    Require((await CommandAsync("project", "test", project, "--suite", suitePath, "--report", Path.Combine(temporary, "report.json")))["passed"]!.GetValue<int>() == 2, "installed project suite");
    var emptyPatch = Path.Combine(temporary, "empty-patch.json");
    await CommandAsync("rom", "patch-create", rom, rom, "--output", emptyPatch);
    await CommandAsync("rom", "patch", rom, emptyPatch, "--output", Path.Combine(temporary, "copy.gb"));
    var pixels = Path.Combine(temporary, "pixels.json");
    await File.WriteAllTextAsync(pixels, "[" + string.Join(',', Enumerable.Repeat(0, 64)) + "]");
    var tiles = Path.Combine(temporary, "tiles.2bpp");
    await CommandAsync("asset", "encode", pixels, "--output", tiles);
    await CommandAsync("asset", "decode", tiles, "--output", Path.Combine(temporary, "decoded.json"));
    var imported = Path.Combine(temporary, "mod project");
    await CommandAsync("project", "import", rom, imported);
    Require(File.ReadAllBytes(Path.Combine(imported, "original.gb")).SequenceEqual(File.ReadAllBytes(rom)), "import preserves original bytes");
    await CommandAsync("project", "resolve", imported);
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RGBDS_BIN")))
    {
        var exampleBuild = await CommandAsync("build", packagedExample, "--provenance");
        string exampleRom = exampleBuild["output"]!.GetValue<string>();
        Require(exampleBuild["engine"]!.GetValue<string>() == "rgbds", "installed native assembler build");
        Require((await CommandAsync("test", exampleRom, "--frames", "10", "--assert", Path.Combine(packagedExample, "checks.json")))["assertionsPassed"]!.GetValue<bool>(), "packaged assembly example executes after installation");
        Require((await CommandAsync("debug", exampleRom, "--script", Path.Combine(packagedExample, "debug.json")))["assertionsPassed"]!.GetValue<bool>(), "packaged assembly example debugger script");
        Console.WriteLine("PASS: installed tool compiles and runs the packaged hello example with native RGBDS.");
    }
    else Console.WriteLine("SKIP: native packaged-example compilation requires RGBDS_BIN.");
    Console.WriteLine("PASS: .NET tool installs temporarily and verifies build, input, captures, hashes, patches, suites, authoring, graphics, receipts, and debugger workflows.");
}
finally
{
    Directory.Delete(temporary, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static byte[] ReadScreenshotPixels(byte[] png)
{
    Require(png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "screenshot PNG signature");
    using var compressed = new MemoryStream();
    bool header = false, ended = false;
    for (int offset = 8; offset < png.Length;)
    {
        Require(png.Length - offset >= 12, "screenshot PNG chunk bounds");
        int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
        Require(length >= 0 && length <= png.Length - offset - 12, "screenshot PNG chunk length");
        string name = Encoding.ASCII.GetString(png, offset + 4, 4);
        if (name == "IHDR")
        {
            Require(!header && offset == 8 && length == 13, "screenshot PNG header");
            Require(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 8, 4)) == 160 && BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 12, 4)) == 144, "screenshot dimensions");
            Require(png.AsSpan(offset + 16, 5).SequenceEqual(new byte[] { 8, 6, 0, 0, 0 }), "screenshot RGBA encoding");
            header = true;
        }
        else if (name == "IDAT")
        {
            Require(header, "screenshot image data follows header");
            compressed.Write(png, offset + 8, length);
        }
        else if (name == "IEND")
        {
            Require(length == 0 && offset + 12 == png.Length, "screenshot PNG end");
            ended = true;
        }
        offset += length + 12;
    }
    Require(header && ended && compressed.Length > 0, "complete screenshot PNG");
    compressed.Position = 0;
    using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
    var pixels = new byte[160 * 144 * 4];
    for (int row = 0; row < 144; row++)
    {
        Require(zlib.ReadByte() == 0, "screenshot uses unfiltered RGBA rows");
        zlib.ReadExactly(pixels.AsSpan(row * 160 * 4, 160 * 4));
    }
    Require(zlib.ReadByte() == -1, "screenshot pixel count");
    return pixels;
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
