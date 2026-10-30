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
dotnet tool install BlastGb.Tools --version 0.4.1 --tool-path ./artifacts/tools --add-source ./artifacts/packages
./artifacts/tools/gb --help
```

The examples below assume `gb` is on PATH. On Windows, the local executable is `artifacts/tools/gb.exe`. Direct execution of a compiled CLI is also supported: `dotnet src/BlastGb.Cli/bin/Debug/net10.0/gb.dll version`.

The toolkit requires no Node.js, npm, JavaScript engine, browser, or web server. Its C# runtime hosts the bundled binjgb WebAssembly engine directly through Wasmtime. The dependency supplies native libraries for macOS, Linux, and Windows on x64 and ARM64; this checkout has been exercised on macOS ARM64 and Windows x64. See [third-party components](THIRD_PARTY.md).

Install [RGBDS](https://rgbds.gbdev.io/) with `rgbasm` and `rgblink` on PATH, or set `RGBDS_BIN` to their directory, to build arbitrary assembly. PNG asset conversion also uses `rgbgfx`. Built-in GameSpec projects, ROM utilities, raw tile conversion, and emulation do not require RGBDS.

## Utilities

| Command | Purpose |
| --- | --- |
| `gb rom info ROM` | Header, mapper, ROM/RAM sizes, checksums, and SHA-256 |
| `gb rom verify ROM` | Validate cartridge logo, size, and checksums |
| `gb rom diff BEFORE AFTER` | Compare bytes with bounded previews |
| `gb rom patch-create BEFORE AFTER --output PATCHES` | Export complete guarded changes between equal-size ROMs |
| `gb rom patch ROM PATCHES --output NEW_ROM` | Apply guarded modifications |
| `gb asm compile SOURCE.asm --output ROM` | Assemble with RGBDS; emit symbols and a map |
| `gb asm decompile ROM` | Inspect candidate instructions or export rebuildable data source |
| `gb asm symbols SYMBOLS.sym` | Inspect RGBDS labels, constants, and physical ROM offsets |
| `gb project init DIRECTORY` | Create editable original assembly source |
| `gb project import ROM DIRECTORY` | Preserve an original cartridge and create a mod project |
| `gb project resolve DIRECTORY` | Inspect a manifest and its paths without writing |
| `gb project status DIRECTORY` | Check a build receipt for changed inputs, tools, and outputs |
| `gb project test DIRECTORY` | Run a bounded suite in fresh emulator sessions |
| `gb game validate GAME.json` | Validate the built-in collection-game specification |
| `gb game export GAME.json DIRECTORY` | Export a customized GameSpec to editable RGBDS source |
| `gb asset convert IMAGE.png --output TILES.2bpp` | Generate tile, map, and palette assets with RGBDS |
| `gb asset encode PIXELS.json`, `gb asset decode TILES.2bpp` | Convert numeric pixel indices and planar tile bytes |
| `gb doctor` | Diagnose host, RGBDS tools, and bundled runtime |
| `gb game lint GAME.json` | Report GameSpec errors without building |
| `gb tiles import/export INPUT --output FILE` | Convert four-shade PNG sheets and raw 2bpp tiles |
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

Validate a customized specification or move it into an assembly project:

```sh
gb game validate ./map-game/game.json --json
gb game export ./map-game/game.json ./expanded-game --name "Expanded game" --json
gb build ./expanded-game --json
```

Export preserves the input JSON and generates matching source in a new directory. Subsequent builds use `source/main.asm`; editing the saved `game.json` does not regenerate it. `gb schema game --json` describes the fields. Runtime validation also requires exactly one player, 1–99 collectibles, and a reachable path to every collectible.

The [minimal assembly example](examples/hello/README.md) writes `$2A` to work RAM:

```sh
gb build examples/hello --json
gb test examples/hello/build/hello.gb --frames 10 --assert examples/hello/checks.json --json
gb debug examples/hello/build/hello.gb --script examples/hello/debug.json --json
gb project test examples/hello --build --overwrite --report artifacts/hello-report.json --capture-dir artifacts/hello-captures --json
```

Use `--overwrite` when rebuilding existing artifacts.

Starter projects include `gb-tests.json` and a smoke assertion. Run `gb project test ./my-game --build --overwrite --json` to validate the suite, rebuild, and execute named cases in fresh sessions. The [collection regression example](examples/collection/README.md) demonstrates scripted input, a framebuffer baseline, screenshots, and generated guarded patches without RGBDS.

Source projects can declare ordered `sources`, `includeDirectories`, `defines`, and PNG `assets` in their version-1 manifest. A build stages generated assets together with the ROM, symbols, and map. For example:

```sh
gb asset convert ./tiles.png --output ./tiles.2bpp --tilemap ./tiles.tilemap --json
gb build ./my-game --receipt ./my-game/build/receipt.json --overwrite --json
gb project status ./my-game --receipt ./my-game/build/receipt.json --json
```

Receipts record effective options and SHA-256 hashes of consumed files, generated outputs, and RGBDS executables. Status reports changed or missing files without rebuilding. See [CLI contracts](docs/CLI.md) for manifest examples, tile formats, and receipt limits.

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
gb rom patch-create ./game.gb ./modified.gb --output ./complete-patches.json --json
```

Use `gb rom diff BEFORE AFTER --patch-output changes.json --json` to generate complete guarded patches from equal-length ROMs. Preview limits never truncate the patch file.

Every guard must match before writing. Overlapping and out-of-range patches fail. Omitted guards require `--allow-unguarded`. Source, original, manifest, and RGBDS `INCLUDE`/`INCBIN`/`READFILE` inputs are protected from output collisions, including symbolic-link aliases. Multi-file builds stage all artifacts, reject filesystem aliases, and restore earlier outputs if a commit fails. Project `patches.json` records changes; source builds do not automatically reapply it.

`patch-create` requires equal-size ROMs and exports every change, independently of the bounded `diff` previews. Applying its output without checksum repair reproduces the second ROM exactly. Patch ranges may include unchanged bytes to keep the export bounded; ROM resizing is not represented.

On Windows, RGBDS dependency reports are checked as both UTF-8 and the active system code page. Ambiguous filenames or legacy encodings that cannot be decoded and re-encoded exactly fail without replacing outputs.

## Test and debug

Assertion files use mapped CPU addresses and decimal JSON numbers:

```json
{ "memory": [{ "address": 49152, "bytes": [42] }], "registers": { "a": 42 } }
```

```sh
gb test ./game.gb --frames 10 --assert ./checks.json --json
gb run ./game.gb --breakpoint 0x150 --max-instructions 100000 --json
gb run ./game.gb --frames 60 --screenshot ./frame.png --json
gb asm symbols ./my-game/build/game.sym --symbol Entry --json
gb asm decompile ./my-game/build/game.gb --symbols ./my-game/build/game.sym --symbol Entry --count 16 --json
```

Results report the cartridge SHA-256, frame events, ticks, CPU registers, requested memory, framebuffer SHA-256, and assertions. Add `--screenshot capture.png` for a PNG, and `--provenance` for toolkit and pinned engine identity. Assertion files may include `framebufferSha256`; it hashes raw RGBA pixels, not PNG bytes. `--until checks.json --max-instructions N --trace-capacity 32` polls bounded memory/register conditions and retains a recent trace. Unreached conditions exit 3. `--buttons right,A` holds buttons during a run. Each frame budget is 70,224 ticks; actual video frame events can differ during initialization or CGB timing.

An ordered debugger script:

```json
{
  "version": 1,
  "commands": [
    { "op": "breakpoints", "addresses": [336] },
    { "op": "run", "frames": 1, "maxInstructions": 100000 },
    { "op": "breakpoints", "addresses": [] },
    { "op": "step", "count": 4 },
    { "op": "read", "address": 49152, "length": 16 },
    { "op": "buttons", "pressed": ["right"] },
    { "op": "frame", "count": 2 },
    { "op": "buttons", "pressed": [] },
    { "op": "capture", "name": "after-input" }
  ]
}
```

```sh
gb debug ./game.gb --script ./debug.json --capture-dir ./captures --json
gb schema debug --json
```

Operations include `step`, `frame`, `read`, `write`, `breakpoints`, `run`, `assert`, `buttons`, `until`, and `capture`. `buttons` replaces the pressed set; `until` polls memory/register conditions within explicit budgets. The entire script and aggregate budgets are validated before execution. Failed assertions and unmet `until` conditions stop later operations. Breakpoints match mapped 16-bit PC addresses in the active bank; a newly set current-PC breakpoint stops before execution, and a subsequent run resumes past that stop. A step may be a HALT/DMA hardware step. Writes affect temporary emulated RAM/IO/mapper state. Every invocation starts a fresh session. Explicit `--save-input` and `--save-output` files load and capture supported raw battery RAM; save states and RTC saves are not supported.

Pass `--symbols FILE.sym` to use unambiguous RGBDS label names in runtime address fields or `--breakpoint`/`--memory`. Runtime lookup accepts bank-zero ROM0, WRAM0, and HRAM labels; banked addresses require explicit numbers and appropriate mapper state. Constants are inspectable but are not addresses.

Project suites combine named assertion runs and debugger scripts, each in a fresh session:

```sh
gb project test ./my-game --suite ./tests.json --build --overwrite --report ./test-report.json --capture-dir ./test-captures --json
gb schema suite --json
```

A project-ROM suite can contain up to 32 cases within shared execution budgets. Standalone suites with an explicit ROM path per case support up to 100 cases. Reports retain evidence for every case, and optional capture directories contain final frames and scripted captures. See [suite examples](docs/CLI.md#project-test-suites).

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

The SDK and NuGet dependencies are pinned. NuGet restores into the ignored `.packages/` directory. `dotnet restore --locked-mode` verifies dependency locks. The package verifier packs the tool, installs it in a temporary directory, and runs real cartridge, compiler, and debugger workflows without a global installation. Assembly subprocesses have a 120-second deadline; `--timeout-seconds 1..600` overrides it. `gb doctor --json` reports tool paths/versions and runtime health. `build --provenance` includes tool identities and dependency hashes observed after compilation, rather than promising an atomic source snapshot.

Tests include byte-for-byte migration fixtures, all 512 opcodes, cartridge validation, input/output preservation, native RGBDS round trips, actual emulator execution, debugger scripts, and MCP framing. Native RGBDS checks require `RGBDS_BIN`. Physical hardware has not been verified. The checked-in CI workflow defines macOS/Linux/Windows package checks and a separate native RGBDS lane; local verification does not establish those remote jobs have passed.

| Project | Responsibility |
| --- | --- |
| `src/BlastGb.Core` | Cartridge tools and original game compiler |
| `src/BlastGb.Runtime` | C# Wasmtime host, CPU/bus access, execution and debugger services |
| `src/BlastGb.Cli` | CLI parsing, files, projects, RGBDS, JSON contracts, MCP |
| `tests/BlastGb.*.Tests` | xUnit regression and integration tests |
| `eng/BlastGb.Verify` | C# package installation verification |
| `examples/hello` | Original minimal assembly with executable assertions |

The C# rewrite preserves version-1 project and automation contracts. See [migration notes](docs/MIGRATION.md).

## License

Blast GB Tools' original code, documentation, examples, and built-in game engine are licensed under the [MIT License](LICENSE).

Third-party components retain their own licenses and notices; see [third-party components](THIRD_PARTY.md). The project's MIT license does not change the licensing of ROMs or other files supplied by users.
