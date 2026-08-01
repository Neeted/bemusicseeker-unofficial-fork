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
