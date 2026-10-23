using BlastGb.Cli;

namespace BlastGb.Cli.Tests;

public sealed class AssetOutputValidationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast asset bounds " + Guid.NewGuid().ToString("N"));
    public AssetOutputValidationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData("tiles", 0)]
    [InlineData("tiles", 15)]
    [InlineData("tiles", 65552)]
    [InlineData("palette", 0)]
    [InlineData("palette", 7)]
    [InlineData("palette", 72)]
    [InlineData("tilemap", 0)]
    [InlineData("tilemap", 4097)]
    [InlineData("attrmap", 0)]
    [InlineData("attrmap", 4097)]
    public void GeneratedOutputsRejectEmptyTruncatedAndOversizedData(string role, int length)
    {
        var path = Path.Combine(directory, role);
        File.WriteAllBytes(path, new byte[length]);
        var error = Assert.Throws<CliException>(() => CliApplication.ReadAssetOutput(path, role));
        Assert.Equal("INVALID_ASSET", error.Code);
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("tiles", 65536)]
    [InlineData("palette", 64)]
    [InlineData("tilemap", 4096)]
    [InlineData("attrmap", 4096)]
    public void CompleteMaximumSizedOutputsPreserveEveryByte(string role, int length)
    {
        var path = Path.Combine(directory, role);
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index & 255)).ToArray();
        File.WriteAllBytes(path, bytes);
        Assert.Equal(bytes, CliApplication.ReadAssetOutput(path, role));
    }
}
