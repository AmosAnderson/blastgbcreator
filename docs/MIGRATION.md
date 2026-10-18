# C# migration: 0.3 to 0.4

All maintained application code, tests, and package verification now use C# on .NET 10. The TypeScript implementation, npm packaging, JavaScript emulator loader, and Node dependencies have been removed.

Use `dotnet build`, `dotnet test`, and `dotnet pack` in place of npm scripts. The installable package is `BlastGb.Tools` and still provides the `gb` command. Source-checkout execution is `dotnet run --project src/BlastGb.Cli -- COMMAND`; compiled invocation is `dotnet PATH/gb.dll COMMAND`.

Preserved contracts:

- Grouped utilities and legacy flat aliases.
- Version-1 workspace manifests and project layout.
- Version-1 JSON envelopes, numeric byte arrays, schema IDs, and exit statuses.
- Guarded byte patches, exact imported-ROM rebuilds, checksum opt-in, input protection, atomic build artifacts, and filesystem-alias detection.
- Debugger scripts, aggregate budgets, mapped memory/register semantics, breakpoints, and held-button runs.
- MCP tools `gb_cli`, `gb_capabilities`, `gb_schema`, negotiated 2025-06-18/2025-11-25 protocols, and bounded stdio messages.

The original GameSpec compiler produces identical GB/GBC ROMs and assembly. The emulator engine remains the same pinned MIT-licensed WebAssembly binary; C# hosts it directly with Wasmtime. Golden homebrew fixtures verify identical runtime registers, memory, ticks, framebuffer hashes, and debug results against the previous implementation.

C# consumers reference `BlastGb.Core` and/or `BlastGb.Runtime`. The former npm library export is replaced by typed .NET APIs. CLI error messages may gain .NET exception detail; error categories and structured validation evidence remain the automation interface. Duplicate-property and malformed-Unicode MCP messages are rejected as malformed without closing the connection.
