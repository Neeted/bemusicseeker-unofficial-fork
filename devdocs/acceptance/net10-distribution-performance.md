# .NET 10 Distribution Performance Decision

[current evidence](./net10-performance-engineering.md) / [起動仕様](../spec/startup-initialization-flow.md)

## Final decision

main appの選択profileを`bundle-r2r`で確定する。

| Property | Value |
| --- | --- |
| RuntimeIdentifier | `win-x64` |
| SelfContained | `true` |
| PublishSingleFile | `true` |
| IncludeNativeLibrariesForSelfExtract | `false` |
| IncludeAllContentForSelfExtract | `false` |
| EnableCompressionInSingleFile | `false` |
| PublishReadyToRun | `true` |
| PublishTrimmed | `false` |

managed assembliesはbundleするが、native self-extract、all-content extraction、compressed bundle decompressionは使わない。

## PC reboot comparison — 2026-08-01

| Layout | Run | Ready operable | Required initialization complete |
| --- | --- | ---: | ---: |
| folder-r2r | PC起動後初回 | 22,398 ms | 32,156 ms |
| bundle-r2r | PC起動後初回 | 22,362 ms | 33,043 ms |
| folder-r2r | 2回目 | 21,815 ms | 31,406 ms |
| bundle-r2r | 2回目 | 21,956 ms | 32,504 ms |

folder-r2rの短縮は初回887 ms、2回目1,098 msで、割合は約2.7～3.4%である。事前selection ruleの「5秒以上かつ15%以上」を満たさない。機能上の大差も観測されなかったため、ファイル数が少なくupdate payload contractも単純なbundle-r2rを維持する。

## Interpretation

以前の約65秒cold gapは両distributionに共通するmanaged readiness dependencyであり、optional library-folder refreshをoperabilityから分離したことで解消した。配布形式をfolderへ戻す必要はない。

folder-r2rはfallback artifactとして再生成可能な状態を維持してよいが、通常publish、layout validator、update manifestの正本はbundle-r2rとする。managed DLLを独自`libs`へ移すcustom loader / probing / deps rewriteは導入しない。

## Application-owned directories

```text
libs/x64/  BASS / 7z native family
native/    Everything bridge / SDK runtime
lang/      language catalog
```

updaterはSelf-contained single-fileを維持する。
