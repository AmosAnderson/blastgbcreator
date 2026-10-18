# Runtime migration fixtures

These are original Blast GB homebrew starter games, generated from `createStarterSpec()`
with the former TypeScript compiler's `mode` set to `gb` or `gbc`. They contain no commercial
cartridge data. The JSON files were captured from the former binjgb JavaScript host before
its removal and are golden results for the C# Wasmtime host.

The current equivalent for generating these ROMs is
`GameCompiler.Build(GameCompiler.CreateStarterSpec() with { Mode = "gb" }).Rom`
(or `"gbc"`). Keep the captured JSON results independent of the runtime under test;
changing expected results requires explaining the intended emulation behavior change.

`*-run.json`: 60 frames, held `A` and `right`, read 32 bytes at `$C000`, no assertions or
breakpoint. `debug.json` is the complete script for `*-debug.json`, exercising initial
breakpoints, resumption, stepping, mapped bus writes and echo reads, assertions, and frames.
The tests compare all result fields, including ticks, CPU registers, framebuffer SHA-256,
diagnostics, and operation evidence. No JavaScript runtime is needed to run these tests.
