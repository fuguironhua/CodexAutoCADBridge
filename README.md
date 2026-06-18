# CodexAutoCADBridge

Minimal AutoCAD 2018 .NET plugin for testing Codex-driven drawing.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The DLL is written to:

```text
bin\Release\CodexAutoCADBridge.dll
```

## Test In AutoCAD

1. Run `NETLOAD`.
2. Select `CodexAutoCADBridge.dll`.
3. Run `CDX_HELLO`.
4. Run `CDX_DRAG_LINK`, click the first hole center, move the mouse to preview, then click the second hole center to finish.
5. Run `CDX_DRAWJSON` and choose `sample_m10x25_bolt.json`, or run `CDX_BOLT_M10X25`.

Built DLL path on this machine:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridge.dll
```

For drag-link testing after earlier versions have already been loaded in AutoCAD, use the independent V3 assembly:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeDragV3.dll
```

For block browser testing, use the independent V4 assembly:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeBlockBrowserV4.dll
```

For the block browser with name filtering, use V5:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeBlockBrowserV5.dll
```

For the fixed 5x3 grid block browser, use V6:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeBlockBrowserV6.dll
```

For the block browser with insert support, use V7:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeBlockBrowserV7.dll
```

For corrected insertion that clones the selected block definition from the source DWG, use V8:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeBlockBrowserV8.dll
```

For rectangle nesting with genetic search, use V9:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeNestingV9.dll
```

For polygon shape nesting with collision checks, use V10:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeNestingV10.dll
```

For faster polygon shape nesting with a hard time budget, use V11:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeNestingV11.dll
```

For polygon shape nesting that reads quantities from numeric text below each part, use V12:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\bin\Release\CodexAutoCADBridgeNestingV12.dll
```

Sample JSON path:

```text
C:\Users\54932\Documents\Codex\2026-06-12\1-2\outputs\autocad_net\CodexAutoCADBridge\sample_m10x25_bolt.json
```

## Commands

- `CDX_HELLO`: verify the plugin loaded.
- `CDX_DRAWJSON`: choose a JSON file and draw supported entities.
- `CDX_BOLT_M10X25`: interactively insert a corrected M10x25 bolt three-view.
- `CDX_DRAG_LINK` / `CDXDL`: select a first point, preview a two-hole link while moving the mouse, then select a second point to commit the shape.
- `CDX_BLOCK_BROWSER` / `CDXBB`: show a dialog with DWG files on the left and block previews on the right, 5 columns by 3 rows with paging. V5 adds a contains filter; V6 uses a fixed 5x3 grid; V7 adds insertion; V8 fixes insertion by cloning the selected block definition from the source DWG into the active drawing.
- `CDX_NEST_RECT` / `CDXNR`: run genetic rectangle nesting for the 6000 x 1500 board and the five detected part bounding boxes, then prompt for a point and draw the nesting layout.
- `CDX_NEST_SHAPE` / `CDXNS`: extract one board outline and five part outlines from the active drawing, run polygon collision-based nesting, optimize for leaving the largest right-side remnant, then prompt for a point and draw the nested true outlines.

Supported JSON entities: `line`, `rectangle`, `polyline`, `circle`, `arc`, `text`.
