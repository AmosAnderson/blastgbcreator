using System.Text.Json.Nodes;
using BlastGb.Cli;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class ProjectPathTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast project paths " + Guid.NewGuid().ToString("N"));
    public ProjectPathTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private static string WithSeparator(string path) => path + Path.DirectorySeparatorChar;
    private static string Error((int Status, JsonNode Json) result) => result.Json["error"]!["code"]!.GetValue<string>();

    private static Dictionary<string, byte[]> Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(path, file), File.ReadAllBytes);

    private static void AssertUnchanged(string path, IReadOnlyDictionary<string, byte[]> before)
    {
        var after = Snapshot(path);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, bytes) in before) Assert.Equal(bytes, after[name]);
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("game", false)]
    [InlineData("game", true)]
    public async Task InitializationAcceptsTrailingSeparatorAndRepeatedCreationPreservesProject(string kind, bool explicitName)
    {
        var target = Path.Combine(directory, "new-game");
        string[] arguments = ["project", "init", WithSeparator(target), "--kind", kind, .. explicitName ? new[] { "--name", "Named Game" } : []];
        var created = await CliTests.Invoke(arguments);
        Assert.True(created.Status == 0, created.Json.ToJsonString());
        Assert.Equal(target, created.Json["data"]!["workspace"]!.GetValue<string>());
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "gb-project.json")))!;
        Assert.Equal(explicitName ? "Named Game" : "new-game", manifest["name"]!.GetValue<string>());
        Assert.Equal(kind, manifest["kind"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(target, kind == "source" ? "source/main.asm" : "game.json")));
        var before = Snapshot(target);
        var repeated = await CliTests.Invoke(arguments);
        Assert.Equal(1, repeated.Status);
        Assert.Equal("OUTPUT_EXISTS", Error(repeated));
        AssertUnchanged(target, before);
        Assert.Equal([target], Directory.GetDirectories(directory));
    }

    [Fact]
    public async Task ImportAcceptsTrailingSeparatorAndPreservesBothOriginalsOnRepeat()
    {
        var input = Path.Combine(directory, "original.gb");
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        File.WriteAllBytes(input, rom);
        var target = Path.Combine(directory, "my-mod");
        var imported = await CliTests.Invoke("project", "import", input, WithSeparator(target));
        Assert.True(imported.Status == 0, imported.Json.ToJsonString());
        Assert.Equal(target, imported.Json["data"]!["workspace"]!.GetValue<string>());
        Assert.Equal(rom, File.ReadAllBytes(Path.Combine(target, "original.gb")));
        var before = Snapshot(target);
        var repeated = await CliTests.Invoke("project", "import", input, WithSeparator(target));
        Assert.Equal(1, repeated.Status);
        Assert.Equal("OUTPUT_EXISTS", Error(repeated));
        Assert.Equal(rom, File.ReadAllBytes(input));
        AssertUnchanged(target, before);
        Assert.Equal([target], Directory.GetDirectories(directory));
    }

    [Fact]
    public void WorkspaceWriterNormalizesDirectoryArgumentsAndCleansFailedStaging()
    {
        var target = Path.Combine(directory, "direct");
        FileOperations.WriteWorkspace(WithSeparator(target), new Dictionary<string, byte[]> { ["saved.bin"] = [1, 2, 3] });
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(target, "saved.bin")));
        var failed = Path.Combine(directory, "failed");
        Assert.Throws<IOException>(() => FileOperations.WriteWorkspace(WithSeparator(failed), new Dictionary<string, byte[]>
        {
            ["collision"] = [1],
            ["collision/child"] = [2]
        }));
        Assert.False(Directory.Exists(failed));
        Assert.Equal([target], Directory.GetDirectories(directory));
    }

    [Fact]
    public void WorkspaceWriterRetainsRootAndExistingDestinations()
    {
        var root = Path.GetPathRoot(directory)!;
        Assert.Equal("OUTPUT_EXISTS", Assert.Throws<CliException>(() => FileOperations.WriteWorkspace(root, new Dictionary<string, byte[]>())).Code);
        var existing = Path.Combine(directory, "existing");
        Directory.CreateDirectory(existing);
        File.WriteAllBytes(Path.Combine(existing, "saved.bin"), [7]);
        Assert.Equal("OUTPUT_EXISTS", Assert.Throws<CliException>(() => FileOperations.WriteWorkspace(WithSeparator(existing), new Dictionary<string, byte[]>())).Code);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(existing, "saved.bin")));
        Assert.Equal([existing], Directory.GetDirectories(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrailingSeparatorDoesNotBypassDirectorySymlinkRejection(bool dangling)
    {
        if (OperatingSystem.IsWindows()) return; // Creating links may require privileges.
        var destination = Path.Combine(directory, "destination");
        if (!dangling)
        {
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(Path.Combine(destination, "saved.bin"), [9]);
        }
        var link = Path.Combine(directory, "linked-game");
        Directory.CreateSymbolicLink(link, destination);
        var rejected = await CliTests.Invoke("project", "init", WithSeparator(link));
        Assert.Equal(1, rejected.Status);
        Assert.Equal("UNSAFE_PATH", Error(rejected));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        if (dangling) Assert.False(Directory.Exists(destination));
        else Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(destination, "saved.bin")));
        Assert.DoesNotContain(Directory.GetDirectories(directory), path => Path.GetFileName(path).StartsWith(".gb-workspace-", StringComparison.Ordinal));
    }
}
