# Blast GB Tools

`gb` is a local Game Boy and Game Boy Color toolkit written in C# on .NET 10. Inspect, compare, modify, assemble, and debug cartridges through command-line utilities. AI agents use the same commands through versioned JSON results or a local MCP stdio server.

The architecture follows [a2utils](https://github.com/AmosAnderson/a2utils): reusable libraries, command-line orchestration, editable projects, explicit output destinations, and bounded execution evidence.

## Build and install

Install the .NET 10 SDK, then run from this checkout:

```sh
dotnet build BlastGb.slnx
dotnet run --project src/BlastGb.Cli -- --help
```

Build and install the .NET tool into a local directory:

```sh
dotnet pack src/BlastGb.Cli -c Release -o artifacts/packages
dotnet tool install BlastGb.Tools --version 0.4.0 --tool-path ./artifacts/tools --add-source ./artifacts/packages
./artifacts/tools/gb --help
```

The examples below assume `gb` is on PATH. On Windows, the local executable is `artifacts/tools/gb.exe`. Direct execution of a compiled CLI is also supported: `dotnet src/BlastGb.Cli/bin/Debug/net10.0/gb.dll version`.

The toolkit requires no Node.js, npm, JavaScript engine, browser, or web server. Its C# runtime hosts the bundled binjgb WebAssembly engine directly through Wasmtime. The dependency supplies native libraries for macOS, Linux, and Windows on x64 and ARM64; this checkout has been exercised on macOS ARM64. See [third-party components](THIRD_PARTY.md).

Install [RGBDS](https://rgbds.gbdev.io/) with `rgbasm` and `rgblink` on PATH, or set `RGBDS_BIN` to their directory, to build arbitrary assembly. Built-in GameSpec projects, ROM utilities, and emulation do not require RGBDS.

## Utilities

| Command | Purpose |
| --- | --- |
| `gb rom info ROM` | Header, mapper, ROM/RAM sizes, checksums, and SHA-256 |
| `gb rom verify ROM` | Validate cartridge logo, size, and checksums |
| `gb rom diff BEFORE AFTER` | Compare bytes with bounded previews |
| `gb rom patch ROM PATCHES --output NEW_ROM` | Apply guarded modifications |
| `gb asm compile SOURCE.asm --output ROM` | Assemble with RGBDS; emit symbols and a map |
| `gb asm decompile ROM` | Inspect candidate instructions or export rebuildable data source |
| `gb project init DIRECTORY` | Create editable original assembly source |
| `gb project import ROM DIRECTORY` | Preserve an original cartridge and create a mod project |
| `gb project resolve DIRECTORY` | Inspect a manifest and its paths without writing |
| `gb build [PROJECT]` | Build a project, assembly file, or GameSpec JSON |
| `gb run ROM` | Run bounded emulation and capture execution evidence |
| `gb test ROM --assert CHECKS.json` | Check expected memory and registers |
| `gb debug ROM --script DEBUG.json` | Execute ordered debugger operations |
| `gb capabilities`, `gb schema NAME` | Discover commands and JSON contracts |
| `gb mcp serve` | Serve the same utilities to agents over stdio |

Use `--help` at any command level and `--json` for structured results. Earlier flat `init`, `import`, `inspect`, `verify`, `disassemble`, and `patch` spellings remain aliases. File paths resolve from the working directory.

## Create a game

```sh
gb project init ./my-game
gb build ./my-game --json
# Edit my-game/source/main.asm.
gb build ./my-game --overwrite --json
gb run ./my-game/build/game.gb --frames 120 --memory 0xc000:16 --json
```

The starter contains original collection-game source, graphics, movement, and input handling. Replace or extend its assembly with arbitrary RGBDS code. To use the fixed collection engine without an assembler, create `gb project init ./map-game --kind game`, edit `game.json`, and build it. GameSpec controls the title, palette, and 20×18 map; its objective text does not add new game mechanics.

The [minimal assembly example](examples/hello/README.md) writes `$2A` to work RAM:

```sh
gb build examples/hello --json
gb test examples/hello/build/hello.gb --frames 10 --assert examples/hello/checks.json --json
gb debug examples/hello/build/hello.gb --script examples/hello/debug.json --json
```

Use `--overwrite` when rebuilding existing artifacts.

## Modify a cartridge

```sh
gb rom info ./game.gb --json
gb asm decompile ./game.gb --offset 0x150 --count 32 --json
gb project import ./game.gb ./my-mod --json
# Edit my-mod/source/main.asm.
gb build ./my-mod --fix-checksums --json
gb rom diff ./my-mod/original.gb ./my-mod/build/game.gb --json
```

Imports preserve `original.gb` and export all bytes as banked RGBDS `db` directives with candidate instruction comments. Decompilation cannot recover original high-level source, symbols, comments, or code/data boundaries. The decoder covers all base and CB-prefixed SM83 opcodes and separates physical file offsets from mapped CPU addresses.

Imported builds retain original checksum fields unless `--fix-checksums` is supplied. A manifest `romSize` preserves the original length; the linker map must prove that any trimmed bytes are unused padding. Cartridge expansion requires updating both the assembly/header and `romSize`.

A patch file uses physical offsets and expected-byte guards:

```json
[{ "offset": 336, "expected": [243], "bytes": [0] }]
```

```sh
gb rom patch ./game.gb ./patches.json --output ./modified.gb --fix-checksums --json
```

Every guard must match before writing. Overlapping and out-of-range patches fail. Omitted guards require `--allow-unguarded`. Source, original, manifest, and RGBDS `INCLUDE`/`INCBIN`/`READFILE` inputs are protected from output collisions, including symbolic-link aliases. Multi-file builds stage all artifacts, reject filesystem aliases, and restore earlier outputs if a commit fails. Project `patches.json` records changes; source builds do not automatically reapply it.

## Test and debug

Assertion files use mapped CPU addresses and decimal JSON numbers:

```json
{ "memory": [{ "address": 49152, "bytes": [42] }], "registers": { "a": 42 } }
```

```sh
gb test ./game.gb --frames 10 --assert ./checks.json --json
gb run ./game.gb --breakpoint 0x150 --max-instructions 100000 --json
```

Results report the cartridge SHA-256, frame events, ticks, CPU registers, requested memory, framebuffer SHA-256, and assertions. `--buttons right,A` holds buttons during a run. Each frame budget is 70,224 ticks; actual video frame events can differ during initialization or CGB timing.

An ordered debugger script:

```json
{
  "version": 1,
  "commands": [
    { "op": "breakpoints", "addresses": [336] },
    { "op": "run", "frames": 1, "maxInstructions": 100000 },
    { "op": "breakpoints", "addresses": [] },
    { "op": "step", "count": 4 },
    { "op": "read", "address": 49152, "length": 16 }
  ]
}
```

```sh
gb debug ./game.gb --script ./debug.json --json
gb schema debug --json
```

Operations include `step`, `frame`, `read`, `write`, `breakpoints`, `run`, and `assert`. The entire script and aggregate budgets are validated before execution. Failed assertions stop later operations. Breakpoints match mapped 16-bit PC addresses in the active bank; a newly set current-PC breakpoint stops before execution, and a subsequent run resumes past that stop. A step may be a HALT/DMA hardware step. Writes affect temporary emulated RAM/IO/mapper state. Every invocation starts a fresh session; save states and battery saves are not persisted.

## Agents and libraries

```sh
gb capabilities --json
gb schema project --json
gb schema patches --json
gb schema assertions --json
gb mcp serve
```

Results use `{schemaVersion:1,command,ok,data}`. Failures include `error:{code,message}` and can retain evidence in `data`. Exit codes: 0 success, 1 operation failure, 2 usage error, 3 verification/assertion failure. The local MCP server exposes `gb_cli`, `gb_capabilities`, and `gb_schema` with bounded requests/responses. See [agent integration](docs/AGENT-WORKFLOW.md) and [CLI contracts](docs/CLI.md).

Reference `BlastGb.Core` for typed cartridge operations and GameSpec compilation. Reference `BlastGb.Runtime` for disposable emulator sessions and bounded run/debug services. Both libraries are independent of CLI output and host-file conventions. Byte arrays in CLI JSON remain numeric arrays.

## Develop and verify

```sh
dotnet build BlastGb.slnx
dotnet test BlastGb.slnx
RGBDS_BIN=/path/to/rgbds/bin dotnet test BlastGb.slnx
dotnet run --project eng/BlastGb.Verify
dotnet format BlastGb.slnx --verify-no-changes
```

The SDK and NuGet dependencies are pinned. NuGet restores into the ignored `.packages/` directory. `dotnet restore --locked-mode` verifies dependency locks. The package verifier packs the tool, installs it in a temporary directory, and runs real cartridge, compiler, and debugger workflows without a global installation.

Tests include byte-for-byte migration fixtures, all 512 opcodes, cartridge validation, input/output preservation, native RGBDS round trips, actual emulator execution, debugger scripts, and MCP framing. Native RGBDS checks require `RGBDS_BIN`. Physical hardware and Windows/Linux execution have not been verified here.

| Project | Responsibility |
| --- | --- |
| `src/BlastGb.Core` | Cartridge tools and original game compiler |
| `src/BlastGb.Runtime` | C# Wasmtime host, CPU/bus access, execution and debugger services |
| `src/BlastGb.Cli` | CLI parsing, files, projects, RGBDS, JSON contracts, MCP |
| `tests/BlastGb.*.Tests` | xUnit regression and integration tests |
| `eng/BlastGb.Verify` | C# package installation verification |
| `examples/hello` | Original minimal assembly with executable assertions |

The C# rewrite preserves version-1 project and automation contracts. See [migration notes](docs/MIGRATION.md).
