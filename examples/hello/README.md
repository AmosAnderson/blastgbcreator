# Minimal Game Boy assembly

This original 32 KiB cartridge writes `$2A` to work RAM at `$C000`, then loops.
It is a small executable example for assembly, assertions, and CPU debugging.

From the toolkit checkout, with .NET 10 and RGBDS installed:

```sh
dotnet build -c Release
dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll build examples/hello --json
dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll test examples/hello/build/hello.gb --frames 10 --assert examples/hello/checks.json --json
dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll debug examples/hello/build/hello.gb --script examples/hello/debug.json --json
```

For repeat builds, add `--overwrite`. Set `RGBDS_BIN` to your RGBDS directory
if `rgbasm` and `rgblink` are not on PATH. After installing the utility, replace
`dotnet src/BlastGb.Cli/bin/Release/net10.0/gb.dll` with `gb` and use paths relative to your working directory.

The debugger stops before `$0150`, executes four real CPU instructions, reads
RAM, and asserts the result. Debug operations affect that temporary emulation
session; the ROM file stays unchanged.
