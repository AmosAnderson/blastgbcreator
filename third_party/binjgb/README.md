# Bundled emulator

`binjgb.wasm` is the unmodified WebAssembly distribution from
[binji/binjgb](https://github.com/binji/binjgb), downloaded from the repository's
`master/docs/` directory on 2026-10-07. The emulator is MIT licensed; see
[LICENSE](LICENSE). No Nintendo boot ROM or commercial game data is included.

`BlastGb.Runtime` embeds this binary and instantiates it directly through
Wasmtime's .NET API. The C# host binds the pinned module's exported emulator
functions and supplies its three Emscripten imports. It uses the emulator's CPU
bus for debugger reads and writes. The original JavaScript loader is not needed
or included. Runtime tests compare registers, ticks, memory, and framebuffer
hashes with saved results from the former loader for both GB and GBC cartridges.

SHA-256 integrity record:

```
52c165069441a09180b4bd63464318aa7b861b1d36e0d05e8047b48619fd2f0b  binjgb.wasm
```

There is no runtime download or CDN. Updating the pinned binary requires checking
its exports, memory layout, event behavior, and parity fixtures. Bounded emulator
tests do not certify every mapper, timing configuration, or physical console.
