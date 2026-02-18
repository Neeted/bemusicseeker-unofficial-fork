# EverythingBridge_x64

Native bridge DLL for BeMusicSeeker Phase B.

## Purpose

- Execute Everything SDK 3 queries in native code.
- Aggregate results into:
  - BMS full paths (`bms_count`, `bms_blob`)
  - Per-directory file-name hash arrays (`dir_count`, `hashes_blob`)
- Return one packed result buffer to C# to avoid per-item P/Invoke loops.

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
int  __cdecl EBridge_Scan(const wchar_t* bmsQuery, const wchar_t* siblingQuery, EBridgeResult** outResult);
void __cdecl EBridge_FreeResult(EBridgeResult* result);
```

`EBridge_FreeResult` must be called for every successful `EBridge_Scan`.

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
