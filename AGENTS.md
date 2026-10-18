# Blast GB Tools

This is a C#/.NET 10 command-line Game Boy toolkit modeled on a2utils. Keep reusable cartridge and execution libraries independent of console output and host-file conventions. The repository has no website, Node, or JavaScript runtime workflow.

## Build and verify

- Use the stable .NET 10 SDK selected by global.json.
- `dotnet build BlastGb.slnx` builds with nullable analysis and warnings as errors.
- `dotnet test BlastGb.slnx` runs xUnit tests. Set `RGBDS_BIN` to a native toolchain directory for actual assembler/linker checks; distinguish them from tests that require no external tools.
- `dotnet run --project eng/BlastGb.Verify` packs and temporarily installs the .NET tool, then exercises the installed runtime. Do not publish or install globally unless requested.
- `dotnet format BlastGb.slnx --verify-no-changes` checks C# formatting.
- NuGet packages restore into ignored `.packages/`. Commit package lock files and use `dotnet restore --locked-mode` when checking reproducibility.
- Sandboxed development may need `DOTNET_CLI_HOME` in a writable temporary directory; VSTest uses a local socket.

## Architecture

- `src/BlastGb.Core`: typed cartridge metadata, complete SM83 decoding, checksum repair, guarded patches, byte-preserving RGBDS export, original GameSpec compiler.
- `src/BlastGb.Runtime`: disposable C# emulator sessions hosting the immutable binjgb WASM through Wasmtime; bounded run/debug services and assertions.
- `src/BlastGb.Cli`: parsing, file/project orchestration, subprocess builds, embedded JSON contracts, stdio MCP. MCP delegates to ordinary CLI commands.
- `src/Shared`: internal source shared by CLI and runtime for exact JSON integer validation without floating-point rounding.
- `tests/BlastGb.*.Tests`: unit/integration tests plus original homebrew migration fixtures. Golden fixtures must not contain commercial game data.
- `eng/BlastGb.Verify`: package verification written in C#.
- `third_party/binjgb`: immutable engine, provenance, and license. No boot ROM or commercial game assets.

Use records and explicit models for domain data, spans for byte operations, System.Text.Json at boundaries, ordinal comparisons for command names, invariant numeric formatting, ProcessStartInfo.ArgumentList for subprocesses, and IDisposable for native resources. Keep entrypoints small. Preserve the stable JSON wire format, including numeric byte arrays and lowercase register names.

## Behavior to preserve

Discover commands with `dotnet run --project src/BlastGb.Cli -- capabilities --json`. CLI spellings, version-1 manifests, JSON Schemas, aliases, and exit codes are compatibility contracts. Reflect command changes in help, capabilities, schemas, and tests.

Preserve original ROMs and every consumed source file. Patches use physical offsets, expected-byte guards, and separate outputs. Source builds do not reapply patches.json. Protect ROM, symbol, and map destinations against all RGBDS INCLUDE/INCBIN/READFILE inputs; fail closed on ambiguous dependency reports. Detect filesystem aliases before replacing outputs. Stage multi-output writes and roll back failed commits. A fixed romSize may remove only linker padding proven by section bounds.

Debugger addresses are mapped 16-bit CPU addresses; breakpoints match the current bank. Bus writes modify temporary RAM/IO/mapper state, never cartridge files. Validate complete scripts and aggregate budgets before allocating a session. Failed assertions stop later operations. Pending audio/video events can arrive without advancing ticks; deliver them without consuming instruction budgets, and bound consecutive event-only stops. Honor cancellation throughout runtime operations. Execution is bounded emulator evidence, not hardware certification.

Disassembly cannot recover original symbols, comments, high-level source, or code/data boundaries. The GameSpec engine supports only its implemented collection-game mechanics. Keep known limitations explicit. Add meaningful regression tests for file safety and behavior changes, especially failure preservation.
