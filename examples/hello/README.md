# Minimal Game Boy assembly

This original 32 KiB cartridge writes `$2A` to work RAM at `$C000`, then loops.
It is a small executable example for assembly, assertions, and CPU debugging.

From the toolkit checkout, with .NET 10 and RGBDS installed:

```sh
dotnet build -c Release
dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll build examples/hello --json
dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll test examples/hello/build/hello.gb --frames 10 --assert examples/hello/checks.json --json
dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll debug examples/hello/build/hello.gb --script examples/hello/debug.json --json
dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll project test examples/hello --build --overwrite --report artifacts/hello-report.json --capture-dir artifacts/hello-captures --json
```

For repeat builds, add `--overwrite`. Set `RGBDS_BIN` to your RGBDS directory
if `rgbasm` and `rgblink` are not on PATH. After installing the utility, replace
`dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll` with `gb` and use paths relative to your working directory.

The debugger stops before `$0150`, executes four real CPU instructions, reads
RAM, and asserts the result. Debug operations affect that temporary emulation
session; the ROM file stays unchanged.

The manifest selects `tests.json`, which runs three cases in fresh emulator
sessions: bounded startup assertions, instruction stepping, and `input-sequence.json`.
The input sequence waits for `$C000` to become `$2A`, captures `initialized`, holds
Right and A for two frames, releases them, then checks the value and captures
`released`. This minimal ROM does not read joypad input or draw a game scene;
the sequence demonstrates the test controls and confirms that its memory result
survives them. Use a game's own state checks to test movement or other mechanics.

The suite command saves every case's final PNG under its name in
`artifacts/hello-captures`, plus the two named snapshots for `input-sequence`.
`artifacts/hello-report.json` retains hashes, CPU/memory evidence, and case results.
A failed case exits 3 after the remaining cases have run.

The alternative `gb-tests.json` suite declares an explicit ROM per case and runs
the assertion and debugger cases in fresh sessions. Select it directly with
`gb project test examples/hello/gb-tests.json --build --overwrite --json`.
The [collection example](../collection/README.md) also demonstrates scripted
controller input, visual assertions, screenshots, and guarded patch generation.
