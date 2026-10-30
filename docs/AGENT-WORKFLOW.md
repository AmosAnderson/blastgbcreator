# Agent workflow

Blast GB Tools runs locally as command-line utilities. Agents edit ordinary files,
invoke bounded commands, inspect JSON results, and test the resulting cartridge.
No browser, hosted service, account, or private AI endpoint is required.

Run `gb --help` after installing the .NET tool. From a source checkout, build with
`dotnet build -c Release`, then use `dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll`
in place of `gb`. These direct invocations keep stdout suitable for parsing.

## Discover the contract first

```sh
gb capabilities --json
gb schema --json
gb schema project --json
gb schema patches --json
gb schema assertions --json
gb schema debug --json
gb schema game --json
gb schema suite --json
gb schema build-receipt --json
```

Every ordinary command supports `--json` and returns one object containing
`schemaVersion`, `command`, `ok`, and `data` or `error`. Inspect both `ok` and the
process exit code: `0` means success, `1` an operation failure, `2` incorrect usage,
and `3` failed verification, assertions, a requested breakpoint that was not
reached, a failed suite, or stale build evidence. A failed assertion still
includes the execution evidence in `data`.

Capabilities describe commands, input/output paths, options, and side effects.
Schemas describe project manifests, guarded patches, GameSpec, assertions,
debugger scripts, project suites, and build receipts. Existing version-1 formats
remain supported. Relative CLI paths use the command's working directory;
manifest paths are workspace-relative, and suite case files are suite-relative.

## Modify a cartridge

```sh
gb rom info original.gb --json
gb rom verify original.gb --json
gb project import original.gb mods/my-game --json
gb project resolve mods/my-game --json
gb asm decompile original.gb --offset 0x150 --count 32 --json
```

Import preserves `original.gb` and creates an editable RGBDS data source in the
workspace. Keep that original as the reference. A linear disassembly decodes
candidate instructions; it cannot establish which bytes are code, recover the
original symbols, or recreate high-level source. The exported data source permits
a byte-exact rebuild while you identify and replace relevant sections yourself.

For a small modification, write a patch file using physical ROM file offsets:

```json
[{ "offset": 336, "expected": [0], "bytes": [1] }]
```

The example bytes are illustrative: read the actual bytes and determine their
meaning before making a change. Expected bytes protect against patching a
different revision. Then write a separate cartridge and review its changes:

```sh
gb rom patch original.gb changes.json --output modified.gb --fix-checksums --json
gb rom diff original.gb modified.gb --json
gb rom verify modified.gb --json
gb rom patch-create original.gb modified.gb --output complete-changes.json --json
```

For source changes, edit `mods/my-game/source/main.asm`, then run
`gb build mods/my-game --json`. Assembly builds require the optional RGBDS
toolchain. Existing destinations require `--overwrite` where supported. Imported
projects preserve bytes by default; choose checksum repair explicitly when it is
appropriate. A workspace's `patches.json` is a diff record, not an additional patch
pass automatically applied to assembly builds.

`patch-create` exports every change between equal-size ROMs with complete
expected-byte guards. It does not use the truncated previews from `rom diff`.
Apply its output without checksum repair to reproduce the target bytes exactly.
For resizing, use an assembly project and update its layout and `romSize`.

When original symbols are available, inspect them and select named locations:

```sh
gb asm symbols build/game.sym --symbol Entry --json
gb asm decompile build/game.gb --symbols build/game.sym --symbol Entry --count 32 --json
```

Labels expose their bank, CPU address, and physical ROM offset when one exists.
Exported constants and RAM symbols do not have ROM offsets. Runtime `--symbols`
resolves only unambiguous bank-zero ROM0/WRAM0/HRAM labels; select a mapper state
and explicit numeric CPU addresses for banked code or memory.

## Create and check a new game

```sh
gb project init games/my-game --json
gb build games/my-game --json
```

The default project contains editable RGBDS assembly. Use
`gb project init games/collection --kind game --json` for the built-in collection
game specification, which builds without RGBDS. Inspect the resolved project to
find its output path rather than guessing it.

```sh
gb game validate games/collection/game.json --json
gb game export games/collection/game.json games/editable --json
gb asset convert art/tiles.png --output generated/tiles.2bpp --tilemap generated/tiles.tilemap --json
```

Game export preserves the customized specification and emits matching editable
assembly in a new workspace. Later source builds use the assembly, so editing
the saved JSON does not regenerate it. The built-in engine supports its fixed
collection mechanics only. PNG conversion uses `rgbgfx`; `asset encode` and
`asset decode` convert raw numeric pixel indices and planar 2bpp tiles without
external tools. Neither operation designs graphics or infers tilemap placement.

Assembly manifests may declare ordered `sources`, `includeDirectories`,
`defines`, and generated PNG `assets`. Protect originals and source inputs when
choosing output paths. Use a receipt when a later agent needs to check whether
recorded build evidence still applies:

```sh
gb build games/editable --receipt games/editable/build/receipt.json --json
gb project status games/editable --receipt games/editable/build/receipt.json --json
```

Status compares recorded hashes of inputs, outputs, and tool executables without
rebuilding. Receipts contain absolute local paths and describe a particular
build; they are not portable cache entries or hardware certification.

Run the built cartridge with the actual bundled emulator:

```sh
gb run modified.gb --frames 60 --memory 0xc000:16 --json
gb run modified.gb --frames 1 --breakpoint 0x150 --json
gb test modified.gb --assert checks.json --frames 60 --json
gb run modified.gb --frames 60 --screenshot artifacts/frame.png --json
```

An assertion file can specify mapped CPU-memory bytes and register values:

```json
{
  "memory": [{ "address": 49152, "bytes": [1] }],
  "registers": { "pc": 336 }
}
```

Use assertions appropriate to the game and the exact stopping point; the values
above are examples. Runtime addresses are 16-bit CPU bus addresses. Breakpoints
apply to whichever ROM bank is mapped there. Physical ROM patch offsets and mapped
runtime addresses are different coordinate systems.

`gb debug ROM --script FILE --json` executes an ordered debugger script. Discover
its schema before writing commands. Bus writes alter the current runtime's RAM,
I/O, or mapper state; they do not edit the cartridge. Each invocation starts a new
emulator. Use explicit `--save-input`/`--save-output` for supported raw battery RAM. Save states and RTC save formats are unsupported.

Use script `buttons` operations to change held input, `until` to wait for bounded
memory/register conditions, and `capture` to mark visual checkpoints. Supply
`--capture-dir` to save them as PNGs. A `framebufferSha256` assertion checks the
raw 160×144 RGBA pixels; establish an expected hash from a reviewed frame at the
same stopping point, not from a PNG file hash. Failed assertions stop later
script operations and retain their evidence.

For repeatable regressions, declare a version-1 suite of named assertion or
script cases. Every case starts a fresh session, and later cases still run after
an earlier failure. All case files and aggregate budgets are checked before a
build or session begins. The original hello example is ready to exercise:

```sh
gb project test examples/hello --build --overwrite --report artifacts/hello-report.json --capture-dir artifacts/hello-captures --json
```

Inspect both the aggregate result and per-case evidence. A successful build is
retained if tests fail; report and capture files are staged together. See
[CLI contracts](CLI.md) for complete manifest, asset, symbol, and suite examples.
Replacing existing suite evidence for an assembly project requires `--build` or
a fresh manifest receipt, ensuring source dependencies remain protected.

## Optional local MCP transport

Agents with shell access can use the utilities directly. For clients that invoke
MCP tools, launch the same implementation over stdio:

```sh
gb mcp serve
```

From a source checkout, build in Release mode first. A typical client entry
launching the compiled CLI has this shape:

```json
{
  "mcpServers": {
    "blast-gb": {
      "command": "dotnet",
      "args": [
        "/absolute/path/to/blastgbcreator/src/BlastGb.Cli/bin/Release/net10.0/gb.dll",
        "mcp",
        "serve"
      ]
    }
  }
}
```

Client configuration formats vary. Set the server working directory using your
client's supported configuration, or pass absolute file paths. The transport
inherits the local process's file access and environment, including `RGBDS_BIN`.
It does not impose a workspace sandbox. No client configuration is changed by
the utility.

| Tool              | Arguments                                           | Result                                                            |
| ----------------- | --------------------------------------------------- | ----------------------------------------------------------------- |
| `gb_capabilities` | `{}`                                                | The same command catalog as `gb capabilities --json`              |
| `gb_schema`       | `{ "name": "project" }`                             | A schema listed by `gb schema --json`                            |
| `gb_cli`          | `{ "arguments": ["rom", "info", "/path/game.gb"] }` | Any ordinary CLI command, with JSON mode forced                   |

Tool results include `structuredContent.exitCode` and
`structuredContent.envelope`, plus the same object serialized as text. CLI errors
remain the normal result envelope and set `isError`. Invalid arguments and
transport limits produce an MCP tool error. Unknown tools and invalid JSON-RPC
requests produce protocol errors. Invoking `mcp` through `gb_cli` is rejected.
The transport implements no separate ROM operations: it delegates to the same
CLI entry point used by shell invocations.

The transport uses the .NET base class library to implement the tools-only
stdio surface for MCP `2025-06-18` and `2025-11-25`. It supports `initialize`,
`notifications/initialized`, `ping`, `tools/list`, and `tools/call`. An unsupported
requested version negotiates `2025-11-25`; clients must accept that version or
disconnect. It does not implement the `2026-07-28` discovery protocol, HTTP,
resources, prompts, tasks, sampling, or progress. Tool calls execute serially. Send `notifications/cancelled` with the original
`requestId` to cancel an active or queued tool call. Runtime and RGBDS work honor
the request token; an artifact commit already in progress finishes or rolls back
as a transaction. At most 128 queued messages are retained; additional tool calls
return `MCP_BUSY` and may reply out of order. Close stdin to drain queued work and
shut down. A transport failure cancels active and queued work and waits for cleanup
before returning the original error. All stdout writes remain serialized.

Messages are UTF-8 JSON, one per newline. Stdout contains only protocol messages.
Each request is limited to 2 MiB; CLI arguments are limited to 1–4,096 strings,
32 KiB of UTF-8 per argument and 1 MiB combined, without NUL characters. Complete
responses are limited to 16 MiB. Oversized results return an error; a command's
normal file side effects may already have completed. Ordinary output destinations,
expected-byte guards, and explicit overwrite rules still apply.

The transport follows the MCP specification's
[stdio framing](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports),
[initialization lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle),
and [tools contract](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).

## Regression workflows

Use `gb doctor --json` before diagnosing tool discovery or runtime issues.
`gb project test DIRECTORY --build --overwrite --json` prevalidates a complete
suite, builds its project, and executes independent named cases. `gb schema suite`
defines case paths and aggregate bounds; malformed later cases prevent all builds.

Use debug `buttons` operations to press and release input, and `run.until` for
bounded after-step memory/register conditions. `--screenshot FILE.png` captures
final evidence even when assertions fail. Framebuffer assertions compare raw
RGBA hashes, so retain `--provenance`, exact budgets, and the original baseline.
Use `rom diff BEFORE AFTER --patch-output FILE` for complete guarded equal-length
patches; diagnostic preview limits do not apply to the exported file.
