# Third-party components

Blast GB Tools' original code is licensed under the [MIT License](LICENSE). The components below retain their respective licenses; the project's MIT license does not replace their license terms or required notices.

- **binjgb** — MIT-licensed Game Boy emulator by binji. The checked-in WebAssembly binary is unmodified and embedded in `BlastGb.Runtime`. See [provenance and checksum](third_party/binjgb/README.md) and [license](third_party/binjgb/LICENSE). No boot ROM or commercial game data is bundled.
- **Wasmtime 48.0.2** — .NET embedding and native WebAssembly runtime. Wasmtime's own code is licensed under Apache-2.0 with the LLVM exception; components embedded in its native binaries retain their respective licenses. Restored through NuGet with a committed dependency lock. [Source](https://github.com/bytecodealliance/wasmtime-dotnet), [provenance](third_party/wasmtime/README.md), and [license](third_party/wasmtime/LICENSE). Supported native assets are supplied by the dependency package.
- **xUnit, Microsoft.NET.Test.Sdk, and xUnit VS runner** — development-only test dependencies pinned in test projects and lock files.
- **RGBDS** — optional external assembler/linker. It is invoked through argument arrays and is not redistributed by this project. [Official project](https://rgbds.gbdev.io/).
