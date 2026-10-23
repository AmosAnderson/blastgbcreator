# Command utilities

The toolkit requires .NET 10. Build this checkout with `dotnet build -c Release`.
Run the compiled utilities with `dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll`,
or install the packed .NET tool into a local directory as described in the
[README](../README.md). The installed command is `gb`.

`gb --help` and `gb rom --help` list commands. `gb COMMAND --help` explains a
specific utility. Like a2utils, commands are grouped by the artifact they work
on, with project/build/run/test utilities for the development loop.

| Utility                                     | Purpose                                                      |
| ------------------------------------------- | ------------------------------------------------------------ |
| `gb rom info ROM`                           | Header, mapper, size, checksums, SHA-256                     |
| `gb rom verify ROM`                         | Check logo, exact declared size, and checksums               |
| `gb rom diff BEFORE AFTER`                  | Compare physical bytes with bounded previews                 |
| `gb rom patch-create BEFORE AFTER --output FILE` | Export complete expected-byte guards and changes         |
| `gb rom patch ROM PATCHES --output NEW_ROM` | Apply guarded byte patches                                   |
| `gb asm compile SOURCE.asm`                 | Assemble RGBDS source into a ROM, symbols, and map           |
| `gb asm decompile ROM`                      | Candidate instruction listing or exact RGBDS data export     |
| `gb asm symbols SYMBOLS.sym`                | Inspect labels, constants, and physical ROM offsets          |
| `gb project init DIRECTORY`                 | Create editable source                                       |
| `gb project import ROM DIRECTORY`           | Preserve a ROM and create editable data source               |
| `gb project resolve [DIRECTORY]`            | Validate manifest and resolve paths without writing          |
| `gb project status [DIRECTORY]`             | Check recorded build inputs, tools, and outputs for changes  |
| `gb project test [DIRECTORY]`               | Run a bounded suite and optionally build first               |
| `gb game validate GAME.json`                | Validate an original collection-game specification          |
| `gb game export GAME.json DIRECTORY`        | Export its customized engine as editable assembly           |
| `gb asset convert IMAGE.png --output FILE`  | Convert PNG graphics through RGBDS                           |
| `gb asset encode PIXELS.json --output FILE` | Encode numeric pixel indices as planar 2bpp tiles            |
| `gb asset decode TILES.2bpp --output FILE`  | Decode planar 2bpp tiles to numeric pixel indices            |
| `gb build [PROJECT]`                        | Build a project, source file, or built-in GameSpec           |
| `gb run ROM`                                | Execute bounded frames and inspect registers/memory          |
| `gb test ROM --assert FILE`                 | Require and check memory/register assertions                 |
| `gb debug ROM --script FILE`                | Run deterministic debugger commands                          |
| `gb capabilities --json`                    | Discover commands, arguments, side effects, and contracts    |
| `gb schema NAME --json`                     | Retrieve versioned project, authoring, and runtime contracts |
| `gb mcp serve`                              | Serve local utilities to an AI agent over MCP stdio          |

The former flat `init`, `import`, `inspect`, `verify`, `disassemble`, and `patch`
commands remain compatibility aliases. `gb --version` prints the version.

## AI and scripting contract

Append `--json` to get exactly one JSON result on stdout. The result is
`{schemaVersion:1,command,ok,data}`; failures include `{error:{code,message}}`.
A failed verification or assertion includes its evidence in `data` together
with `ok:false` and the error. The `command` field uses the invoked spelling,
including grouped command names. Use the installed `gb` or invoke the compiled
`gb.dll` directly when parsing stdout; `dotnet run` can print build diagnostics.

Exit codes are 0 for success, 1 for an operation failure, 2 for invalid usage,
and 3 for failed ROM verification, assertions, an unreached breakpoint, a failed
suite, or a stale build receipt.
Numbers in command options accept decimal or `0x` hexadecimal. JSON integer
fields also accept exact equivalent forms such as `1.0` and `1e0`; fractional
and out-of-range values are rejected without rounding. Runtime addresses are
mapped 16-bit CPU addresses. Patches and
ROM diffs use physical cartridge file offsets, across all banks.

`gb schema` lists the available schemas. `gb capabilities --json` reports
arguments, options, side effects, external tool requirements, and legacy aliases.
`gb mcp serve` reserves stdout for MCP protocol messages and executes the same
local command implementations. MCP clients should use a dedicated process.

## Edit, build, and test

```sh
gb project init ./my-game
# Edit my-game/source/main.asm using RGBDS assembly and data directives.
gb project resolve ./my-game --json
gb build ./my-game --json
gb run ./my-game/build/game.gb --frames 120 --memory 0xc000:16 --json
# Later builds require explicit replacement.
gb build ./my-game --overwrite --json
```

Assembly builds require `rgbasm` and `rgblink` on PATH. `RGBDS_BIN` can name a
directory containing those tools. They run directly with argument arrays,
with the project as their working directory. The assembler searches the
project root, source directories, and manifest include directories for includes.
Build output includes the
cartridge plus RGBDS `.sym` symbols and a `.map` file. All outputs are staged
before replacement; a failed commit attempts to restore previous outputs.
Source, manifest, and original-ROM files are protected from output collisions,
including every `INCLUDE`, `INCBIN`, and `READFILE` dependency reported by RGBDS.
Missing or ambiguous dependency reports stop the build before output replacement.
Output artifacts must be distinct on the destination filesystem, including on
case-insensitive and Unicode-normalizing volumes.

`gb asm compile ./main.asm --output ./build/game.gb --json` uses the same
RGBDS build pipeline without a project manifest. For the built-in collection
game engine without RGBDS, use `gb project init ./my-game --kind game`, edit
`game.json`, then build the same way. The default assembly project is
unrestricted RGBDS source; the optional GameSpec project uses a fixed engine.

### Validate and export GameSpec

```sh
gb game validate ./my-game/game.json --json
gb game export ./my-game/game.json ./editable-game --name "My game" --json
gb build ./editable-game --json
gb schema game --json
```

Validation reads the specification without writing. It reports the normalized
game and checks its required title, `gb`/`gbc` mode, five palette colors, objective,
and 20×18 map. Map symbols are `#` walls, `.` floor, `P` player, and `C`
collectibles. Exactly one player and 1–99 reachable collectibles are required.
The objective is descriptive text; it does not define additional mechanics.

Export creates a new source workspace with matching `source/main.asm` and an
exact copy of the supplied `game.json`. Its ROM matches the built-in compiler
when assembled with RGBDS. The JSON remains a reference: source builds do not
regenerate assembly after changes to it. Existing destination directories are
refused. `--name` changes the workspace name, not the game's title or behavior.

`project resolve` checks manifest paths and input files, reports their hashes,
and calculates artifact paths. It creates no directories and invokes no
assembler. RGBDS resolves source includes and syntax during the actual build.

## Import and modify a cartridge

```sh
gb project import ./game.gb ./my-mod --json
gb rom info ./my-mod/original.gb --json
gb asm decompile ./my-mod/original.gb --offset 0x150 --count 32 --json
# Edit my-mod/source/main.asm, then assemble it.
gb build ./my-mod --json
gb rom diff ./my-mod/original.gb ./my-mod/build/game.gb --json
```

Import retains the original bytes and exports every byte as RGBDS `db` data.
Comments show candidate instructions; they do not identify code/data boundaries
or recover original symbols, comments, or high-level source. Assembly builds
preserve every original byte, including malformed checksum fields, unless
checksum repair is requested. `asm decompile ROM --output FILE.asm` without
range options writes the same byte-exact RGBDS data source. With `--offset` or
`--count`, it writes a diagnostic listing rather than rebuildable assembly.

The manifest's `romSize` removes linker padding for small and partial-bank
inputs after checking the RGBDS map to prove no assembled section is discarded.
Sections beyond that size fail the build; change the field deliberately when
changing the cartridge layout. Header size and mapper changes still require
corresponding assembly edits.

For a guarded byte change, write `changes.json`:

```json
[{ "offset": 336, "expected": [243], "bytes": [0] }]
```

```sh
gb rom patch ./game.gb ./changes.json --output ./patched.gb --fix-checksums --json
```

Expected bytes must match the input before output is written. Missing guards
require `--allow-unguarded`. Overlapping edits are rejected. The input ROM is
retained. `patches.json` inside an imported project is a record of changes;
`build` uses assembly source and does not apply that file again.

`rom diff` reports total changed bytes and contiguous ranges, plus added and
removed bytes for unequal lengths. Previews contain at most 64 ranges and 32
bytes per range. `rangesTruncated` and `previewTruncated` identify omissions;
counts and hashes describe the complete files. A difference is a successful
comparison, so it exits 0; `identical` tells an agent whether bytes match.

Generate an applicable patch from the complete files:

```sh
gb rom patch-create ./original.gb ./modified.gb --output ./changes.json --json
gb rom patch ./original.gb ./changes.json --output ./reproduced.gb --json
```

Both ROMs must have the same size, between 336 bytes and 8 MiB. Every output
record includes expected original bytes and replacement bytes. The export
coalesces changes within 64 KiB windows, so guarded ranges may include unchanged
bytes; no change is dropped because a diff preview was truncated. Version-1 patch
limits remain 65,536 records and 65,536 bytes per record. Identical inputs produce
an empty patch list. Apply without `--fix-checksums` to reproduce the target
exactly, including its existing checksum fields. Resizing is not represented.

## RGBDS symbols

```sh
gb asm symbols ./build/game.sym --json
gb asm symbols ./build/game.sym --symbol Entry --json
gb asm decompile ./build/game.gb --symbols ./build/game.sym --symbol Entry --count 16 --json
gb run ./build/game.gb --symbols ./build/game.sym --breakpoint Entry --memory wScore:1 --json
```

Symbol names are case sensitive. Labels report `bank`, mapped `address`, and a
nullable `romOffset`. Only bank 0 addresses `$0000–$3FFF` and banks 1–511
addresses `$4000–$7FFF` map to physical ROM offsets. RAM and unmappable labels
remain visible with `romOffset:null`. Exported numeric constants have
`kind:"constant"`, a signed 32-bit `constantValue`, and null address fields;
they cannot select an address. Comments and RGBDS Unicode name escapes are
accepted; malformed records and duplicate decoded names fail the whole file.
Files are bounded to 16 MiB, 65,536 symbols, and 1,024 UTF-8 bytes per name.

`asm decompile --symbol` requires `--symbols` and cannot accompany `--offset`.
Supplying symbols annotates matching instruction starts in the JSON result; it
does not infer code/data boundaries. With `--output` and `--symbol`, the result
is a diagnostic listing, not a byte-preserving source export.

Runtime commands accept `--symbols` for `--breakpoint`, `--memory`, and string
address/PC values in assertion and debug JSON. Only bank-zero ROM0, WRAM0, and
HRAM labels are accepted automatically. A symbol in a switchable bank cannot
establish the mapper state; use a numeric mapped address after setting that state.
For example, `{ "memory": [{ "address": "wScore", "bytes": [1] }] }` uses
the same assertion format with a label resolved before runtime validation.

## Project manifest

`gb-project.json` retains the version 1 format for existing projects:

```json
{
  "format": "blast-gb-workspace",
  "version": 1,
  "name": "My mod",
  "kind": "rom",
  "source": "source/main.asm",
  "rom": "original.gb",
  "output": "build/game.gb",
  "patches": "patches.json",
  "romSize": 32768,
  "fixChecksums": false
}
```

`kind` is `source`, `rom`, or `game`. Source and ROM projects require either
`source` or `sources`, never both;
game projects require `game`, normally `game.json`. `rom`, `patches`, `romSize`,
and `fixChecksums` are optional. `fixChecksums` defaults to false; generated
starter assembly sets it true. `--fix-checksums` enables repair for a build or
patch invocation. `romSize` fixes assembly output length to 336 bytes–8 MiB;
output is shortened only when the map proves removed bytes are linker padding.
An assembled output shorter than that length is an error. Every manifest path
must remain inside its project. Symbolic links in project paths are refused.

### Multiple source files and assets

An assembly project may use these optional version-1 fields:

```json
{
  "format": "blast-gb-workspace",
  "version": 1,
  "name": "Tile game",
  "kind": "source",
  "sources": ["source/main.asm", "source/graphics.asm"],
  "includeDirectories": ["include"],
  "defines": { "LEVEL_COUNT": "3" },
  "assets": [{
    "input": "art/tiles.png",
    "output": "generated/tiles.2bpp",
    "tilemap": "generated/tiles.tilemap"
  }],
  "output": "build/game.gb",
  "receipt": "build/receipt.json",
  "tests": "tests/suite.json",
  "fixChecksums": true
}
```

`sources` lists 1–256 distinct assembly translation units in link order. Each is
assembled separately; all objects are linked together. `includeDirectories`
contains up to 256 existing relative directories searched after the project and
source directories. `defines` maps up to 1,024 identifier names to string values
passed as RGBDS `-D name=value` definitions. Paths and values are passed as
arguments, not shell text. Each source's dependency report participates in output
protection. Arbitrary assembler/linker flags and linker scripts are not exposed.

Each asset converts a workspace PNG before assembly using `rgbgfx -d 2`.
`output` is required; `tilemap`, `palette`, and `attrmap` are optional. Assembly
can consume the generated relative path with `INCBIN`. Generated bytes are staged
for the current build, then committed with the ROM, symbols, map, and optional
receipt only after compilation succeeds. Up to 256 asset entries are supported
in assembly workspaces; built-in GameSpec workspaces do not use this pipeline.

### Convert tile graphics

```sh
gb asset convert ./art/tiles.png --output ./tiles.2bpp --tilemap ./tiles.tilemap --palette ./tiles.pal --attrmap ./tiles.attrmap --json
gb asset decode ./tiles.2bpp --output ./pixels.json --json
gb asset encode ./pixels.json --output ./copied-tiles.2bpp --json
```

PNG conversion requires `rgbgfx` on PATH or in `RGBDS_BIN`. Images must be 8–4096
pixels in each dimension, multiples of 8, and at most 4,096 tiles in total; input
files are limited to 16 MiB. RGBDS validates image colors and produces the
requested tile, map, palette, and attribute files. This interface uses RGBDS's
2bpp conversion defaults; custom quantization, deduplication, and sprite ordering
options are not exposed.

Raw `encode` and `decode` are independent of RGBDS. `pixels.json` is a numeric
array of indices 0–3, grouped as 64 pixels per tile in row order; tiles are stored
consecutively. Each encoded 8×8 tile occupies 16 bytes with low and high bitplanes
interleaved by row. Both directions require 1–4,096 complete tiles. They preserve
tile order and do not assign colors or generate a tilemap. All asset commands
require separate outputs and explicit `--overwrite` for existing files.

Use `.tilemap` and `.attrmap` for raw map bytes; RGBDS linker `.map` files are
text. This repository marks common raw asset extensions as binary in
`.gitattributes`. Add equivalent binary attributes for custom raw extensions to
prevent Git newline conversion from changing cartridge data.

### Build receipts and status

```sh
gb build ./my-game --receipt ./my-game/build/receipt.json --json
gb project status ./my-game --receipt ./my-game/build/receipt.json --json
gb schema build-receipt --json
```

The manifest's optional `receipt` selects a workspace-relative receipt; the
build's `--receipt` overrides it with a path relative to the working directory.
A receipt records the toolkit version, effective options, consumed source and
dependency hashes, output hashes, and resolved RGBDS executable paths, versions,
and hashes. It is committed with the build outputs. Assembly receipt builds
assemble twice to discover and verify a stable dependency graph. Changed inputs
or tools during that process fail without replacing outputs.

`project status` reads the manifest and selected receipt, then checks recorded
files without running RGBDS or the emulator. Missing or changed inputs, outputs,
tools, or toolkit version produce `stale:true`, a `changes` list, and exit 3;
unchanged files produce `stale:false` and exit 0. An absent receipt is stale.
Receipts contain absolute local paths. They are evidence for that recorded build,
not a portable build cache or a guarantee that a newly selected tool on PATH is
the same tool; changing PATH alone does not alter the recorded executable.

## Bounded execution and assertions

The bundled binjgb WebAssembly runs through the C# Wasmtime host without a
browser, external emulator, boot ROM, or network. `--frames N` sets a budget of `N * 70224`
hardware clock ticks (default 60, maximum 3600). Results report actual video
frames, ticks, registers, requested memory, and framebuffer SHA-256. Frames can
differ from the budget while the LCD is disabled or with CGB timing.

```sh
gb run ./game.gb --frames 120 --buttons A,start --memory 0xc000:16 --json
gb run ./game.gb --breakpoint 0x150 --max-instructions 100000 --json
gb run ./game.gb --frames 60 --screenshot ./frame.png --json
```

Buttons remain pressed for the bounded run. PC breakpoints apply to mapped
16-bit addresses in whichever ROM bank is selected. PC is checked between
actual emulator instruction/hardware steps. `--max-instructions` bounds these
steps while seeking a breakpoint (default 1000000, maximum 10000000).
An unreached requested breakpoint exits 3 and reports the stopping budget.

An assertion file can contain memory, register, and/or framebuffer checks:

```json
{
  "registers": { "PC": 336 },
  "memory": [{ "address": 336, "bytes": [243] }]
}
```

```sh
gb test ./game.gb --breakpoint 0x150 --assert ./checks.json --json
```

Register names are case insensitive. Failed assertions include expected and
actual values and exit 3. `test` requires an assertion file; `run` accepts one
optionally. These checks provide bounded emulator evidence and do not certify
behavior on every mapper, timing configuration, or physical console.

`--screenshot FILE.png` on `run`, `test`, and `debug` writes the final 160×144
RGBA framebuffer as a lossless PNG. Add `"framebufferSha256":"<64 hex digits>"`
to an assertion object to compare it against a known frame. Copy the hash from a
reviewed run at the same stopping point; it hashes raw RGBA pixels, not the PNG
encoding. Screenshots are also saved on assertion failure, and existing outputs
require `--overwrite`.

Ctrl+C cancels active run, test, and debug operations. Cancellation is checked
between emulator events, instruction steps, and memory operations, and returns
the structured `CANCELLED` error in JSON mode. C# library callers can pass a
`CancellationToken` to runtime and session operations.

## Scripted debugger

```json
{
  "version": 1,
  "commands": [
    { "op": "breakpoints", "addresses": [336] },
    { "op": "run", "frames": 1, "maxInstructions": 100000 },
    { "op": "assert", "registers": { "pc": 336 } },
    { "op": "step", "count": 1 },
    { "op": "write", "address": 49152, "bytes": [42] },
    { "op": "read", "address": 49152, "length": 16 },
    { "op": "assert", "memory": [{ "address": 49152, "bytes": [42] }] }
  ]
}
```

```sh
gb debug ./game.gb --script ./debug.json --json
```

Operations share one emulator session in order. `step` executes instructions,
`frame` advances bounded frames, `breakpoints` replaces the mapped PC breakpoint
set, and `run` continues until a breakpoint or a budget. `read` and `write` use
the emulator's real CPU bus. Writes affect RAM, IO, or mapper state according
to hardware semantics and preserve the cartridge file. Results retain operation
evidence, final CPU state, and assertion outcomes. Sessions are ephemeral;
battery saves and save states are not persisted.

For changing input and condition-based waits, use these additional operations:

```json
{
  "version": 1,
  "commands": [
    { "op": "buttons", "pressed": ["right", "A"] },
    { "op": "frame", "count": 2 },
    { "op": "buttons", "pressed": [] },
    { "op": "until", "frames": 10, "maxInstructions": 10000,
      "memory": [{ "address": 49152, "bytes": [42] }] },
    { "op": "capture", "name": "ready" }
  ]
}
```

```sh
gb debug ./game.gb --script ./gameplay.json --capture-dir ./captures --screenshot ./final.png --json
```

`buttons` replaces the held set; an empty array releases everything. Accepted
names are `up`, `down`, `left`, `right`, `A`, `B`, `start`, and `select`.
`until` requires at least one memory or register condition, checks initially and
after advancing instructions, and stops when all conditions match or a budget
is exhausted. It defaults to 60 frames and 100,000 instructions. An unmet
condition fails the script and prevents later operations. Adapt the illustrative
address and bytes above to the cartridge under test.

`capture` records the current framebuffer under a unique 1–64 character ASCII
name beginning with a letter/digit and containing letters, digits, `_`, or `-`.
Names ignore case for uniqueness; device names and `failure-N` are reserved.
`--capture-dir` writes these snapshots as `<name>.png`; failed framebuffer
assertions also retain `failure-N.png` evidence. A capture without that option
still records its framebuffer hash in the operation result.

Before opening an emulator, validation enforces aggregate limits of 1,000
commands, 3,600 frames, 1,000,000 instructions, 1,048,576 memory bytes, and 32
explicit captures. `until` reserves memory for all potential condition checks,
so its instruction limit affects the memory budget as well. Failed assertions
stop later operations; audio/video events that do not advance time do not consume
instruction budgets.

## Project test suites

Place a suite at the manifest's `tests` path, at `tests.json` by default, or
select it with `--suite FILE`:

```json
{
  "version": 1,
  "cases": [
    { "name": "startup", "frames": 60, "assert": "startup.json" },
    { "name": "held-input", "frames": 30, "buttons": ["right"], "assert": "movement.json" },
    { "name": "gameplay", "script": "gameplay.json" }
  ]
}
```

```sh
gb project test ./my-game --build --overwrite --report ./report.json --capture-dir ./captures --json
gb schema suite --json
```

Paths in cases resolve within the suite file's directory. Each case is either a
bounded run with a required assertion file or a debugger script; the two forms
cannot be combined. The suite validates every file and shared budget before an
optional build or emulator session. It accepts 1–32 uniquely named cases, with
the same aggregate frame, instruction, memory, command, and capture limits as a
debug script. `--symbols FILE.sym` resolves supported labels in all case files.

Each case starts a fresh session from the same ROM. A failing case does not
prevent later cases from running. The result reports totals and full per-case
evidence; any failed case yields exit 3. `--report` saves that result data as JSON.
`--capture-dir` saves each case's `_final.png`, scripted captures, and framebuffer
failure captures under a case-name subdirectory. Report and capture files are
committed together, including on assertion failures. `--build` first commits a
successful ordinary project build; test failures do not roll that build back.
Replacing existing report or capture files in a source/ROM project requires
`--build` or a fresh receipt named in its manifest, so every assembler dependency
can be protected from accidental replacement.
