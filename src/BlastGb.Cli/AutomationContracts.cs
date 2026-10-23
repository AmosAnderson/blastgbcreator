using System.Reflection;
using System.Text.Json.Nodes;

namespace BlastGb.Cli;

/// <summary>Versioned command discovery and JSON Schemas embedded in the executable.</summary>
public static class AutomationContracts
{
    public const string Version = "0.4.0";
    private static readonly JsonObject Capabilities = ReadResource("capabilities");
    private static readonly JsonObject Help = ReadResource("help");
    private static readonly string[] Names = ["project", "envelope", "patches", "assertions", "debug", "game", "suite", "build-receipt"];
    private static readonly IReadOnlyDictionary<string, JsonObject> Schemas =
        Names.ToDictionary(name => name, ReadResource, StringComparer.Ordinal);

    public static string[] SchemaNames => [.. Names];
    public static string[] CommandNames => Capabilities["commands"]!.AsArray()
        .Select(node => node!.GetValue<string>()).ToArray();

    public static object GetCapabilities() => Capabilities.DeepClone();
    public static object? GetSchema(string name) => Schemas.TryGetValue(name, out var schema)
        ? schema.DeepClone() : null;

    public static string GetHelp(string name) => $"Blast GB Tools {Version}\n\n" + (Help[name] ?? Help[""])!.GetValue<string>();

    private static JsonObject ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"BlastGb.Cli.Contracts.{name}.json")
            ?? throw new InvalidOperationException($"Missing embedded command contract: {name}.");
        return JsonNode.Parse(stream)!.AsObject();
    }
}
