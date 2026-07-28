# .NET 10 Dependency Register

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

調査基準日: 2026-07-29

## Toolchain

| Item | Current | Final engineering target | Owner |
|---|---|---|---|
| .NET SDK | `10.0.302`, `rollForward: latestPatch` | `10.0.302`, `rollForward: latestPatch` | `NET10-09 F1` |
| Main app publish | win-x64 folder Self-contained、untrimmed、non-single-file | `F2`で公式single-fileを有限評価。`ADOPTED`またはfolder SCD `NOT_ADOPTED` | `NET10-09 F2` |
| Updater publish | win-x64 Self-contained single-file | retain | `NET10-07/09` |

Self-contained artifactはmachine-installed runtimeのsecurity servicingへ自動追随しないため、公開候補は選択SDKで再publishする。

## Managed packages retained at current migration baseline

| Corridor | Package / version | Decision |
|---|---|---|
| configuration／resources | System.Configuration.ConfigurationManager 10.0.10、System.Resources.Extensions 10.0.10 | retain; generated settings／embedded icon behaviorを維持 |
| logging／JSON | NLog 6.1.4、Newtonsoft.Json 13.0.4 | retain |
| WPF MVVM | LivetCask Core／Mvvm／EventListeners／Messaging 4.0.2、Microsoft.Xaml.Behaviors.Wpf 1.1.31 transitive | retain |
| document | Microsoft.Xml.SgmlReader 1.8.30 | retain |
| SQLite | sqlite-net-pcl 1.11.285、SQLitePCLRaw.bundle_e_sqlite3 3.0.4、SourceGear.sqlite3 3.53.3 | retain; app／tests／2 tools共通policy |
| archive／audio | SevenZipExtractor 1.0.19、NVorbis 0.10.5 | retain |
| tests | Microsoft.NET.Test.Sdk 18.8.1、MSTest 3.6.4 | retain for migration closure; MSTest major upgradeは別目的 |
| analyzers | Roslynator analyzer packages 4.15.0、CLI 0.12.0 | retain |
| BASS wrapper | `libs/Bass.Net.dll` 2.4.12.1、SHA-256 `25F8BE949CF9A805A4E549590CF06D8937460DF0ABDEE4BFD712C17570E2F065` | technical retain。source／licensee／registration確認はrelease prerequisite |

Version authorityは`Directory.Packages.props`、resolved graphは各`packages.lock.json`とする。F1ではSDK servicingだけを行い、必要のないpackage major upgradeを混ぜない。

## Retained native assets

| Asset | Version / identity | Runtime owner / layout | Status |
|---|---|---|---|
| SQLite `e_sqlite3.dll` | SourceGear.sqlite3 3.53.3 | SQLitePCLRaw provider。selected publish layoutで一系統 | verified |
| `7z.dll` | 24.07 x64、SHA-256 `3691ADCEFC6DA67EEDD02A1B1FC7A21894AFD83ECF1B6216D303ED55A5F8D129` | `libs/x64/7z.dll`、SevenZip archive owner | verified |
| BASS six-file family | bass 2.4.12、bassmix 2.4.8、bass_fx 2.4、basswasapi 2.4.1、bassasio 1.3.1、bassenc 2.4.13 | `BassNativeRuntime`、`libs/x64` absolute path、x64 only | technical verification complete; release entitlement pending |
| Everything SDK | `Everything3_x64.dll` 3.0.0.9、SHA-256 `BE25B01C73BBF359B50DDF30255133225F93B4BC40A8D208173319373BCDAA5C` | `native`、Everything owner | verified |
| Everything bridge | first-party x64、SHA-256 `24863BFD06BFDFE0419DF767152575ADCD38B4104DC2063ED411AA7F956AA835` | `native` sibling absolute load／shutdown owner | verified |

## Retired legacy dependencies

次は移行済みであり再導入しない。

- legacy Livet／Livet.Extensions、System.Windows.Interactivity／Expression Interactions
- MetroRadiance、Expression Drawing／Effects、Windows API Code Pack
- QuickConverter、DynamicJson、IniLibrary、tracked SgmlReader／System.Collections.Immutable binaries
- sqlite.net HintPath、hand-placed sqlite3.dll
- OggVorbis.NET64、tracked SevenZipExtractor wrapper
- Framework startup／runtime probing configuration

## Distribution layout policy

### Folder Self-contained baseline

standard .NET host／`.deps.json` graphを使うため、managed package／runtime DLLは`BeMusicSeeker.exe`と同じdirectoryに置く。BASS／7zは`libs/x64`、Everythingは`native`、language catalogは`lang`に置く。

managed DLLを見た目のためだけに`libs`へ移す独自loader、probing、deps rewrite、post-publish relocationは認めない。

### Optional official single-file

`NET10-09 F2`で公式single-fileを評価する。採用時はmanaged assembliesと公式runtime nativeをbundleし、application-owned content／native owner directoryは必要に応じて隣接保持する。single-file非互換API、native load、updater transactionを標準機構だけで閉じられない場合は`NOT_ADOPTED`としてfolder baselineを最終構成にする。

## Release prerequisite

BASS.NETのexact source archive、正式license、licensee scope、registration／redistribution entitlementは[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)の`RELEASE-01`で確認する。これはtechnical Engineering GateやCodex停止条件ではないが、公開配布の許可を意味するものでもない。
