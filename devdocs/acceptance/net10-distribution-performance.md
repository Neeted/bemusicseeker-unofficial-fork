# .NET 10 Distribution Performance Decision

[性能計画](../plan/BeMusicSeeker_refactoring_plans/BeMusicSeeker_性能回帰改善計画.md) / [current evidence](./net10-performance-engineering.md)

## Current profile

main appの現行profileは`bundle-r2r`である。

| Property | Value |
|---|---|
| RuntimeIdentifier | `win-x64` |
| SelfContained | `true` |
| PublishSingleFile | `true` |
| IncludeNativeLibrariesForSelfExtract | `false` |
| IncludeAllContentForSelfExtract | `false` |
| EnableCompressionInSingleFile | `false` |
| PublishReadyToRun | `true` |
| PublishTrimmed | `false` |

このprofileはmanaged assembliesをbundleするが、native self-extract、all-content disk extraction、compressed assembly decompressionを使用しない。したがって、今回の`startup_ready_ui`後65秒gapを「起動時に全DLLを展開しているため」とは説明できない。

## Previous candidate evidence

2026-07-29の同一session計測では、次の二候補が実用上同等だった。

| Candidate | Warm-cache median | Fresh-install median | Ready working set | Files |
|---|---:|---:|---:|---:|
| `folder-r2r` | 3.302 s | 4.650 s | 211.9 MiB | 505 |
| `bundle-r2r` | 3.284 s | 4.665 s | 217.9 MiB | 24 |

この計測はOS page cacheを消去せず、PC再起動直後のcold bootを評価していない。よって現行profile選択はcold-boot観点ではprovisionalである。

## Performance-first fallback

`PERF-03`のcode-level readiness修正中は`bundle-r2r`を維持する。修正完了後、同じcommit／settings／dataで次を一回ずつ確認できるartifactを生成する。

```text
bundle-r2r
folder-r2r
```

folder profileは標準.NET host layoutを使う。managed DLLを独自`libs`へ移すloader、probing、deps書換え、post-publish relocationは導入しない。

PC再起動後の最終判断:

- process start→`startup_initialization_complete`がfolder-r2rで5秒以上かつ15%以上短く、機能／update／rollbackに問題がなければfolder-r2rへ切り替える。
- 差が閾値未満、またはcode fixでcold gapが消えた場合はbundle-r2rを維持する。
- stage log上、差がmanaged application内の同じreadiness edgeに残る場合は配布形式で隠さずcode routeへ戻す。

この一回比較はpost-engineering manual acceptanceであり、Codexを結果待ちで停止させない。

## Current reproducibility evidence

S1～S3完了後の同一HEADから、次の二artifactを再生成できることを確認した。出力は`artifacts/performance/net10-distribution-s4/`配下のignored local artifactであり、commitには含めない。

```text
HEAD: c5c5a47366ff446f6aafcbca47756416cb870f39
SDK: 10.0.302 (global.json, rollForward=latestPatch)
RuntimeIdentifier: win-x64
SelfContained: true
PublishReadyToRun: true
PublishTrimmed: false
PublishReadyToRunComposite: false
IncludeAllContentForSelfExtract: false
EnableCompressionInSingleFile: false
```

```powershell
dotnet restore .\BeMusicSeeker.sln --runtime win-x64 --locked-mode -p:PublishReadyToRun=true
dotnet publish .\BeMusicSeeker.csproj /p:Configuration=Release /p:Platform=x64 --runtime win-x64 --self-contained true --no-restore -p:PublishTrimmed=false -p:PublishReadyToRunComposite=false -p:EnableCompressionInSingleFile=false -p:IncludeAllContentForSelfExtract=false -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:PublishDir=artifacts/performance/net10-distribution-s4/bundle-r2r
dotnet publish .\BeMusicSeeker.csproj /p:Configuration=Release /p:Platform=x64 --runtime win-x64 --self-contained true --no-restore -p:PublishTrimmed=false -p:PublishReadyToRunComposite=false -p:EnableCompressionInSingleFile=false -p:IncludeAllContentForSelfExtract=false -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:PublishDir=artifacts/performance/net10-distribution-s4/folder-r2r
```

| Artifact | Files | Bytes | SHA-256 (BeMusicSeeker.exe) |
|---|---:|---:|---|
| bundle-r2r | 24 | 201,075,466 | `41BF8438E883AFC67B4836090BC39B31E2570AAF869C98450C55A6B74FF0C75A` |
| folder-r2r | 505 | 207,373,687 | `0D1E0C54F088C2F46728BA61C2D9BF363093DF4B28CF809A4BD3DCE3325018D8` |

`folder-r2r`は`BeMusicSeeker.deps.json`、`BeMusicSeeker.runtimeconfig.json`、managed runtime DLLをexe隣接に置く標準.NET host layoutである。両artifactとも`libs/x64`、`native`、`lang`を維持し、custom loader、AssemblyResolve、private probing、managed DLL relocation、deps書換えは使用しない。PC再起動後の比較、機能／update／rollbackのmanual acceptanceはMANUAL-01へhandoffし、engineering gateを停止させない。
