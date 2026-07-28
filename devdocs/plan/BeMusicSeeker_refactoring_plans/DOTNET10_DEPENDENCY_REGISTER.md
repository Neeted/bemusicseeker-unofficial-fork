# .NET 10 Dependency Register

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

調査基準日: 2026-07-29

## Toolchain／deployment

| Item | Current | Final target | Owner |
|---|---|---|---|
| .NET SDK | `10.0.302`, `rollForward: latestPatch` | retain; release候補は同servicing baselineで再publish | `NET10-10 P4` |
| Main app | win-x64 SCD、single-file、native self-extract、R2R無効 | candidate matrixから選択し、中立profile `WinX64SelfContained`へ固定 | `NET10-10 P1/P2/P3` |
| Updater | win-x64 SCD single-file | retain | `NET10-07/10` |
| Main app content | `lang`、config、`libs/x64`、`native` | owner directoryを維持 | `NET10-10 P3` |

Self-contained artifactはmachine-installed runtimeのservicingへ自動追随しないため、公開候補は選択SDKで再publishする。

## Managed packages retained

| Corridor | Package / version | Decision |
|---|---|---|
| configuration／resources | System.Configuration.ConfigurationManager 10.0.10、System.Resources.Extensions 10.0.10 | retain |
| logging／JSON | NLog 6.1.4、Newtonsoft.Json 13.0.4 | retain |
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
| SQLite `e_sqlite3.dll` | SourceGear.sqlite3 3.53.3 | SQLitePCLRaw provider。profileに応じSDK bundle／standard root | verified |
| `7z.dll` | 24.07 x64、SHA-256 `3691ADCEFC6DA67EEDD02A1B1FC7A21894AFD83ECF1B6216D303ED55A5F8D129` | `libs/x64/7z.dll` | verified |
| BASS six-file family | bass 2.4.12系 | `BassNativeRuntime`、`libs/x64`、x64 only | technical verification complete; entitlement pending |
| Everything SDK／bridge | SDK 3.0.0.9＋first-party x64 bridge | `native`、explicit load／shutdown owner | verified |

## Performance candidate policy

| Family | Managed assemblies | Runtime native | Extraction | Directory characteristic |
|---|---|---|---|---|
| folder SCD | exe隣接 | exe隣接 | なし | standard host fileがrootに並ぶ |
| managed bundle | bundle | exe隣接 | なし | managed DLLを減らしつつstandard bundlerを使用 |
| native self-extract bundle | bundle | bundle→temporary extraction | fresh install／cache missであり | root fileは最少 |

各familyでReadyToRun有無を比較する。`EnableCompressionInSingleFile=false`、trimming／Composite R2R／NativeAOTは使わない。

file数はselection metricではない。standard host fileを`libs`へ移すcustom loader、probing、deps rewrite、post-publish relocation、wrapper launcherは禁止する。application-owned native／contentだけを`libs/x64`、`native`、`lang`へ整理する。

## Retired legacy dependencies

legacy Livet、Expression／MetroRadiance、Windows API Code Pack、QuickConverter、DynamicJson、IniLibrary、sqlite.net HintPath、hand-placed sqlite3、OggVorbis.NET64等は移行済みであり再導入しない。詳細はGit historyとlock graphを正本とする。

## Release prerequisite

BASS.NETのexact source、正式license、licensee scope、registration／redistribution entitlementは[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)の`RELEASE-01`で確認する。これはCodexのEngineering Gateを停止しない。
