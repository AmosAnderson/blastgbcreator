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
| `gb rom patch ROM PATCHES --output NEW_ROM` | Apply guarded byte patches                                   |
| `gb asm compile SOURCE.asm`                 | Assemble RGBDS source into a ROM, symbols, and map           |
| `gb asm decompile ROM`                      | Candidate instruction listing or exact RGBDS data export     |
| `gb project init DIRECTORY`                 | Create editable source                                       |
| `gb project import ROM DIRECTORY`           | Preserve a ROM and create editable data source               |
| `gb project resolve [DIRECTORY]`            | Validate manifest and resolve paths without writing          |
| `gb build [PROJECT]`                        | Build a project, source file, or built-in GameSpec           |
| `gb run ROM`                                | Execute bounded frames and inspect registers/memory          |
| `gb test ROM --assert FILE`                 | Require and check memory/register assertions                 |
| `gb debug ROM --script FILE`                | Run deterministic debugger commands                          |
| `gb capabilities --json`                    | Discover commands, arguments, side effects, and contracts    |
| `gb schema NAME --json`                     | Retrieve envelope/project/patch/assertion/debug JSON Schemas |
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
and 3 for failed ROM verification, assertions, or an unreached breakpoint.
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
project root and source directory for includes. Build output includes the
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

`kind` is `source`, `rom`, or `game`. Source and ROM projects require `source`;
game projects require `game`, normally `game.json`. `rom`, `patches`, `romSize`,
and `fixChecksums` are optional. `fixChecksums` defaults to false; generated
starter assembly sets it true. `--fix-checksums` enables repair for a build or
patch invocation. `romSize` fixes assembly output length to 336 bytes–8 MiB;
output is shortened only when the map proves removed bytes are linker padding.
An assembled output shorter than that length is an error. Every manifest path
must remain inside its project. Symbolic links in project paths are refused.

## Bounded execution and assertions

The bundled binjgb WebAssembly runs through the C# Wasmtime host without a
browser, external emulator, boot ROM, or network. `--frames N` sets a budget of `N * 70224`
hardware clock ticks (default 60, maximum 3600). Results report actual video
frames, ticks, registers, requested memory, and framebuffer SHA-256. Frames can
differ from the budget while the LCD is disabled or with CGB timing.

```sh
gb run ./game.gb --frames 120 --buttons A,start --memory 0xc000:16 --json
gb run ./game.gb --breakpoint 0x150 --max-instructions 100000 --json
```

Buttons remain pressed for the bounded run. PC breakpoints apply to mapped
16-bit addresses in whichever ROM bank is selected. PC is checked between
actual emulator instruction/hardware steps. `--max-instructions` bounds these
steps while seeking a breakpoint (default 1000000, maximum 10000000).
An unreached requested breakpoint exits 3 and reports the stopping budget.

An assertion file can contain memory and/or register checks:

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
