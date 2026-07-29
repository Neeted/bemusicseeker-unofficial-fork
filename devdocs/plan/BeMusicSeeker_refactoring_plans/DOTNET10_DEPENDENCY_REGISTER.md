# .NET 10 Dependency Register

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

調査基準日: 2026-07-29

## Toolchain／deployment

| Item | Current | Decision |
|---|---|---|
| .NET SDK | `10.0.302`, `rollForward: latestPatch` | retain。release候補は同servicing baselineで再publish |
| Main app | win-x64 SCD、managed bundle、native runtime隣接、R2R有効 | retain。current .NET 10 evidenceでprofile自体が原因と確認された場合だけ再検討 |
| Updater | win-x64 SCD single-file | retain |
| Main app content | `lang`、config、`libs/x64`、`native` | owner directoryを維持 |

Self-contained artifactはmachine-installed runtimeのservicingへ自動追随しないため、公開候補は選択SDKで再publishする。

## Managed packages retained

| Corridor | Package / version | Decision |
|---|---|---|
| configuration／resources | System.Configuration.ConfigurationManager 10.0.10、System.Resources.Extensions 10.0.10 | retain |
| logging／JSON | NLog 6.1.4、Newtonsoft.Json 13.0.4 | retain。performance markerはaggregate／disabled-low-overhead contractを守る |
| WPF MVVM | LivetCask 4.0.2、Microsoft.Xaml.Behaviors.Wpf 1.1.31 transitive | retain |
| document | Microsoft.Xml.SgmlReader 1.8.30 | retain |
| SQLite | sqlite-net-pcl 1.11.285、SQLitePCLRaw.bundle_e_sqlite3 3.0.4、SourceGear.sqlite3 3.53.3 | retain; app／tests／2 tools共通policy |
| archive／audio | SevenZipExtractor 1.0.19、NVorbis 0.10.5 | retain |
| tests／analyzers | Microsoft.NET.Test.Sdk 18.8.1、MSTest 3.6.4、Roslynator 4.15.0／CLI 0.12.0 | retain |
| BASS wrapper | `libs/Bass.Net.dll` 2.4.12.1、SHA-256 `25F8BE949CF9A805A4E549590CF06D8937460DF0ABDEE4BFD712C17570E2F065` | technical retain。公開権限は`RELEASE-01` |

Version authorityは`Directory.Packages.props`、resolved graphは各`packages.lock.json`とする。

## Retained native assets

| Asset | Version / identity | Runtime owner / layout | Status |
|---|---|---|---|
| SQLite `e_sqlite3.dll` | SourceGear.sqlite3 3.53.3 | SDK-owned root native file | verified |
| `7z.dll` | 24.07 x64、SHA-256 `3691ADCEFC6DA67EEDD02A1B1FC7A21894AFD83ECF1B6216D303ED55A5F8D129` | `libs/x64/7z.dll` | verified |
| BASS six-file family | bass 2.4.12系 | `BassNativeRuntime`、`libs/x64`、x64 only | technical verification complete; entitlement pending |
| Everything SDK／bridge | SDK 3.0.0.9＋first-party x64 bridge | `native`、explicit load／shutdown owner | verified。native throughputは`MANUAL-02` |

## Selected distribution

`devdocs/acceptance/net10-distribution-performance.md`を正本とし、managed bundle＋ReadyToRunを選択済み。`IncludeNativeLibrariesForSelfExtract=false`、compression／trimming／Composite R2R／NativeAOTは無効。

PERF-01はapplication hot pathを対象とし、net472 comparisonやproduction data不足を理由にdistribution profileを再開しない。publish propertyに変更がない限りdistribution benchmarkを再実行しない。

SDK／SQLite／WPF native runtimeのexe隣接fileは標準host layoutとして許可する。これらを`libs`へ移すcustom loader、probing、deps rewrite、post-publish relocation、wrapper launcherは追加しない。

## Retired legacy dependencies

legacy Livet、Expression／MetroRadiance、Windows API Code Pack、QuickConverter、DynamicJson、IniLibrary、sqlite.net HintPath、hand-placed sqlite3、OggVorbis.NET64等は再導入しない。詳細はGit historyとlock graphを正本とする。

## Release prerequisite

BASS.NETのexact source、正式license、licensee scope、registration／redistribution entitlementは[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)の`RELEASE-01`で確認する。これはCodexのEngineering Gateを停止しない。
