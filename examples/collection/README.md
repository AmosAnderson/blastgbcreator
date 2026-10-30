# Gameplay and visual regression example

This original one-collectible game builds without RGBDS. Its suite checks the
startup player position and framebuffer, then presses Right, releases it, and
asserts collection and victory in an independent emulator session.

```sh
gb project test examples/collection --build --json
gb run examples/collection/build/game.gb --frames 10 --screenshot examples/collection/build/start.png --provenance --json
gb debug examples/collection/build/game.gb --script examples/collection/debug.json --screenshot examples/collection/build/victory.png --provenance --json
```

Repeat builds and captures require `--overwrite`. The startup hash in
`checks.json` is for the bundled immutable binjgb engine and the exact ten-frame
tick budget; inspect the image before intentionally changing that baseline.
The RGBA framebuffer hash differs from the encoded PNG file hash.

To review a same-length source change, keep the first build and choose a separate
output for the edited specification:

```sh
# Edit game.json, then build a separate cartridge.
gb build examples/collection --output examples/collection/build/edited.gb --provenance --json
gb rom diff examples/collection/build/game.gb examples/collection/build/edited.gb --patch-output examples/collection/build/changes.json --json
gb rom patch examples/collection/build/game.gb examples/collection/build/changes.json --output examples/collection/build/patched.gb --json
gb rom diff examples/collection/build/edited.gb examples/collection/build/patched.gb --json
```

Patch export includes every difference even when the diagnostic previews are
truncated. Guards reject a different revision. GameSpec changes still use the
fixed collection engine; objective prose does not add mechanics.
