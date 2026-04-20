# EverythingBridge_x64

Native bridge DLL for BeMusicSeeker chart/resource split scan.

## Purpose

- Execute Everything SDK 3 queries in native code.
- Split scan inputs into:
  - chart files
  - audio files
  - image files
  - movie files
- Re-aggregate resource hits to the nearest owning chart directory.
- Return one packed result buffer to C# without per-item P/Invoke loops.

## Returned data shape

The bridge returns a chart-directory keyed hash-only result:

- chart file paths
- chart directories
- all-resource basename hashes by chart directory
- audio/image/movie basename hashes by chart directory
- audio/image/movie relative-path hashes by chart directory

`sibling:` based resource collection is no longer used.

## Build

From PowerShell:

```powershell
pwsh native/EverythingBridge/build-x64.ps1 -Configuration Release
```

Output:

- `native/EverythingBridge_x64.dll`

## Runtime requirements

- `native/EverythingBridge_x64.dll`
- `native/Everything3_x64.dll`

Both files must be placed in the same `native` directory under the app base path.

## Exported C API

```c
int  __cdecl EBridge_ScanChartAndResources(
    const wchar_t* chartQuery,
    const wchar_t* audioQuery,
    const wchar_t* imageQuery,
    const wchar_t* movieQuery,
    EBridgeResult** outResult);
void __cdecl EBridge_FreeResult(EBridgeResult* result);
```

`EBridge_FreeResult` must be called for every successful `EBridge_ScanChartAndResources`.

## Repository layout and operation

- `native/` is the source-of-truth location for runtime native DLLs.
  - Keep distributable binaries here:
    - `native/Everything3_x64.dll`
    - `native/EverythingBridge_x64.dll`
- `native/EverythingBridge/` is for bridge source/build files only.
  - `EverythingBridge.cpp`, `EverythingBridge.vcxproj`, `build-x64.ps1`, etc.
- `bin/<Configuration>/<TFM>/native/` is build output (copied by `dotnet build`).
  - Do not edit or manage files there manually.
  - Do not commit `bin/` artifacts.

## Recommended workflow

1. Build bridge: `pwsh native/EverythingBridge/build-x64.ps1 -Configuration Release`
2. Build app: `dotnet build`
3. Verify runtime copy under `bin/.../native/` (generated output)
4. Commit only source + `native/` canonical DLLs as needed
