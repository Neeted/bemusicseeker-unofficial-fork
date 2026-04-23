# EverythingBridge_x64

Native bridge DLL for BeMusicSeeker chart/resource split scan.

## Purpose

- Execute Everything SDK 3 queries in native code.
- Keep managed code on a bridge-only contract.
- Split scan inputs into:
  - chart files
  - audio files
  - image files
  - movie files
- grouped file-enumeration queries
- Re-aggregate resource hits to chart-directory keyed ownership views.
- Return one packed result buffer to C# without per-item P/Invoke loops.

## Managed contract

- C# / tests must not call `Everything3_x64.dll` directly.
- Managed code may construct query strings, but query execution and result collection must go through `EverythingBridge_x64.dll`.
- If file search / enumeration behavior needs to change, update the bridge API first instead of adding direct managed SDK calls.
- The bridge exists specifically to avoid per-result managed/native round-trips.

## Returned data shape

The bridge returns a chart-directory keyed hash-only result with two ownership views:

- chart file paths
- chart directories
- aggregate ownership
  - all-resource basename hashes by chart directory
  - audio/image/movie basename hashes by chart directory
  - audio/image/movie relative-path hashes by chart directory
- self-only ownership
  - all-resource basename hashes by chart directory
  - audio/image/movie basename hashes by chart directory
  - audio/image/movie relative-path hashes by chart directory

Aggregate ownership means a resource is visible from every ancestor chart directory on its path.  
Self-only ownership means the resource is visible only from the nearest owning chart directory.

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
BeMusicSeeker and the bridge DLL are managed as a single shipped set; old bridge variants are not a supported runtime scenario.

## Exported C API

```c
int  __cdecl EBridge_ScanChartAndResources(
    const wchar_t* chartQuery,
    const wchar_t* audioQuery,
    const wchar_t* imageQuery,
    const wchar_t* movieQuery,
    EBridgeResult** outResult);
int  __cdecl EBridge_EnumerateGroupedFiles(
    const EBridgeGroupedQuery* queries,
    unsigned int queryCount,
    EBridgeGroupedFilesResult** outResult);
void __cdecl EBridge_FreeResult(EBridgeResult* result);
void __cdecl EBridge_FreeGroupedFilesResult(EBridgeGroupedFilesResult* result);
```

`EBridge_FreeResult` must be called for every successful scan call.  
`EBridge_ScanChartAndResources` is the canonical fixed-scan contract for library build and includes self-only ownership fields in the returned payload.
`EBridge_EnumerateGroupedFiles` batches arbitrary grouped queries such as `chart/audio/image/movie/__all__` and returns one packed result buffer.

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
