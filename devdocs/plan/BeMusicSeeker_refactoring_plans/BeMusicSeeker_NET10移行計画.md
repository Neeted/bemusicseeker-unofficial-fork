# BeMusicSeeker .NET 10 Self-contained 移行計画

[現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md)

## 1. Goal

BeMusicSeekerを.NET Framework 4.7.2から.NET 10へ移行し、Windows x64向けの**Self-contained**配布物を生成する。MVVM／owner boundaryを維持し、既存settings、library DB、playlist／chart file、native integration、update／restart contractを壊さない。

完了成果物はbuild directoryではなく、clean checkoutから再現できる`dotnet publish` outputである。

## 2. Target deployment

| Project | Target | Distribution |
|---|---|---|
| `BeMusicSeeker.csproj` | `net10.0-windows`, x64 | `win-x64` Self-contained folder publish |
| `BeMusicSeeker.Updater` | `net10.0-windows`, x64 | Self-contained。単一exe化はuntrimmedでprotocol検証後に採用可 |
| `BeMusicSeeker.Tests` | `net10.0-windows`, x64 | test runner用。配布しない |
| `chart-info-compare` | `net10.0`, x64 | tool build。配布要否は既存運用を維持 |
| `chart-info-export` | `net10.0`, x64 | tool build。配布要否は既存運用を維持 |

main appの初期publish profileは次を固定する。

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
<PublishSingleFile>false</PublishSingleFile>
<PublishTrimmed>false</PublishTrimmed>
<PublishReadyToRun>false</PublishReadyToRun>
```

`RuntimeIdentifier`だけにSelf-contained判定を任せない。WPF、reflection、legacy serializer、native DLL、update layoutの移行を安定させるため、main appのsingle-file、trimming、ReadyToRun、NativeAOTは本計画の対象外とする。

## 3. Compatibility invariants

- generated settingsのkey、type、default、serialized value、save timing、既存`user.config`読込
- SQLite schema、PRAGMA、DateTime／enum／null変換、transaction／lock ordering、既存DBのin-place open
- BMS／bmson／playlist／LR2／package metadataの読書き結果
- install directory、portable／user data path、resource lookup、external player／LR2／Everything integration
- updater manifest、download、swap、restart、rollback、旧版から新版への更新
- UI startup、library initialize／scan、playlist、search／filter、playback、package、maintenance、settings、shutdown
- failure時にlive state、DB、package state、notificationを部分適用しない既存契約

互換性を意図的に変える場合は、migration、rollback、user-visible説明を同じunitへ含める。

## 4. Ordered outcomes

### `NET10-01 Retarget and complete project baseline`

対象:

- app、tests、updater、2 toolsの全5 project
- solution／verification script／output path
- .NET 10 breaking-change inventory

作業:

1. 全projectをtarget matrixへretargetする。Framework-only explicit referenceをSDK reference／PackageReferenceへ必要最小限で置換する。
2. `System.Configuration.ConfigurationManager`を導入し、既存settings providerとmigration testsを維持する。
3. app、tests、updater、2 toolsをclean restore／Release build可能にする。toolsをsolution外だから未検証のまま残さない。
4. target framework起因のwarning／runtime failureをblocker IDへ分類する。恒久shimで隠さない。

Exit:

- 全5 projectのRelease buildが通る。
- existing full testsが.NET 10 runnerで通る。
- startup、settings load、library DB openの最小smokeが通る。

### `NET10-02 Managed package and configuration baseline`

対象:

- NLog、Newtonsoft.Json、System.Resources.Extensions、test SDK／MSTest、Roslynator
- package versionの中央管理／lock policy
- `app.config` startup／probing依存

作業:

1. [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md)の候補versionを実行時点で再確認し、一度に未知なmajor upgradeを混ぜずcorridor単位で更新する。
2. NuGet化できるmanaged DLLはPackageReferenceへ移す。package lockを導入した後はlocked restoreをverificationに加える。
3. configuration adapter外の`System.Configuration`依存を増やさない。
4. legacy `app.config`のruntime startup／private probingを最終publish contractに使わない。

Exit:

- managed dependencyのsource、version、license、owner outcomeが台帳と一致する。
- app／testsで同じlibraryをHintPathとNuGetの二重供給にしない。

### `NET10-03 WPF dependency modernization`

対象:

- Livet／Livet.Extensions
- System.Windows.Interactivity／Microsoft.Expression.Interactions
- MetroRadiance／Expression Drawing／Effects
- Windows API Code Pack

作業:

1. Livetは現行API利用をinventoryし、`LivetCask`候補へverticalに移行する。notification、command、dispatcher、lifetime semanticsをtestsで比較する。
2. behavior callerが残る場合は`Microsoft.Xaml.Behaviors.Wpf`へ移す。現在のMainWindow／dialog routeではdirect behaviorを退役し、LivetCaskのlocked transitive runtimeだけを保持する。
3. file／folder pickerはWPFの`Microsoft.Win32.OpenFileDialog`／`OpenFolderDialog`へ移し、Windows API Code Packを削除する。
4. MetroRadiance chromeとExpression drawing／effectsはWPF `WindowChrome`、resource、Path／Geometryへ置換する。

Exit:

-旧Livet、Interactivity、Expression、MetroRadiance、Windows API Code PackのHintPathがない。updaterの旧install cleanupだけは維持する。
- main windows／dialogs／drag／command／selection／shutdown smokeが通る。

### `NET10-04 Converter, JSON and document helpers`

対象:

- QuickConverter（Q1でretire）
- DynamicJson
- SgmlReaderDll
- IniLibrary、System.Collections.Immutable

作業:

1. Q1ではQuickConverter式をfeature family単位のtyped converter／MultiBinding／triggerへ置換し、XAMLのobservable behaviorを維持する。以降のhelper置換は同じNET10-04 owner corridorで続ける。
2. DynamicJson利用をNewtonsoft.JsonまたはSystem.Text.Jsonを包む明示boundaryへ移し、missing member、number、date、null、case semanticsをgolden testで固定する。
3. SgmlReaderDllをmaintained package候補へ移し、外部playlist／HTML parse fixtureを比較する。
4. source usageのないIniLibraryとSystem.Collections.Immutable HintPathは削除する。必要性が判明した場合だけmodern packageを追加する。

Exit:

- QuickConverterのlegacy binary／markup／runtime registrationがなく、MainWindow／dialogs／playbackのtyped presentation routeで置換されている。DynamicJson、IniLibraryのlegacy binary referenceがない。
- external document／JSON fixtureと主要XAML binding behaviorが維持される。

### `NET10-05 SQLite provider migration`

対象:

- app、tests、2 toolsの`sqlite.net.dll`／`sqlite3.dll`
- schema、mapping、transaction、backup／repair behavior

作業:

1. `sqlite-net-pcl`＋`SQLitePCLRaw` bundle候補でspikeし、既存ORM behaviorとnative initializationを比較する。
2. storage owner／repositoryごとに移行し、raw SQL、parameter、DateTime、enum、primary key、busy／lock、transaction failureのgolden testを追加する。
3. existing user DBのcopyを使ったread／write／rollback／maintenance／tool compare-export testを行う。
4. provider migration後に旧`sqlite.net.dll`と手配置`sqlite3.dll`を削除し、native assetを一系統にする。

Exit:

- 全DB routeと2 toolsが同じprovider policyを使う。
- schema migrationを要求しない既存DBはそのまま開ける。要求する場合はversioned migration／backup／rollbackがある。

### `NET10-06 Archive, audio and native runtime corridor`

対象:

- SevenZipExtractor／7z.dll
- Bass.Net／BASS native family
- OggVorbis.NET64 (retired; NVorbis 0.10.5)
- Everything bridge／native DLL

作業:

1. SevenZipExtractorはPackageReference候補へ移し、encrypted／multi-file／failure／path traversal fixtureと7z native loadingを検証する。
2. BASS managed／nativeは、`Bass.Net.dll` 2.4.12.1と`vendor/native/x64`の固定six-file set（bass 2.4.12、bassmix 2.4.8、bass_fx 2.4、basswasapi 2.4.1、bassasio 1.3.1、bassenc 2.4.13）を技術的にretainする。`BassNativeRuntime`の絶対path load、ABI probe、partial-load rollback／release、device resetとprocess-level releaseの分離、playback／decode／device／shutdown smokeを同じrouteで検証する。source archive、正式`LICENSE.rtf`、licensee scope、既存registration entitlementは`NATIVE-01` external-gateとして別管理し、証跡が揃うまで配布可能とは扱わない。
3. OggVorbisはNVorbis 0.10.5へ置換し、legacy PCM parity、同一形式の連結logical stream、形式変更／truncated inputのfailure、cache／fallback、package／publish outputを検証する。
4. Everythingはbridge ABI、installed／absent時のfallback、native search pathをpublish outputで検証する。
5. 各native assetにsource、version、architecture、license、copy owner、runtime load testを台帳化する。

Exit:

- native DLLはproject／publish itemとして決定的に配置され、current directoryやlegacy probingの偶然に依存しない。
- x64 processでarchive、audio、Everythingの代表routeが通る。

### `NET10-07 Self-contained publish and updater closure`

作業:

1. appとupdaterにversion-controlled publish profileを追加する。
2. publish outputのmanaged／native／resource／config layoutを明示し、旧`libs` probingとbuild-output relocationを退役する。
3. updaterがSelf-contained app folderをdownload、verify、swap、restart、rollbackできるようmanifest／path policyを更新する。
4. updaterのsingle-fileはuntrimmedでのみspikeし、既存の「rootに一つのupdater exe」契約を安全に維持できる場合だけ採用する。
5. publish outputから起動し、build outputをsmoke対象にしない。

Exit:

- `dotnet publish -c Release -r win-x64 --self-contained true`で再現可能なfolder artifactが生成される。
- runtime未導入環境を想定したapp／updater起動とupdate／rollback testが通る。

### `NET10-08 Existing-data and clean-machine acceptance`

作業:

- legacy versionが作成したsettings、DB、playlist、package state、LR2 dataをcopyしてmigration smokeを行う。
- clean x64 Windows machine／VMで.NET Desktop Runtime未導入を確認し、publish folderだけから起動する。
- startup、scan、playlist、playback、package install／uninstall、maintenance、settings save／restart、updaterを検証する。
- machine-specific credential、player、LR2、Everythingがない場合の既存failure／fallbackを確認する。

Exit:

- machine-installed .NETに依存せず主要workflowが動く。
- data loss、silent reset、partial updateがない。
- manual-only evidenceは日付、OS、artifact hash、結果をGate recordへ短く残す。

### `NET10-09 Migration Gate`

完了条件:

1. tracked projectに`net472`がない。
2. app、tests、updater、2 toolsのrestore／Release build／testが通る。
3. main appとupdaterのwin-x64 Self-contained publishが再現できる。
4. obsolete managed HintPathはゼロ。明示retainするvendor/native componentは台帳、license、ABI test、publish ownerを持つ。
5. existing-data、clean-machine、update／rollback smokeが通る。
6. .NET 10 breaking changesとdependency registerに未分類項目がない。
7. fresh outcome reviewで重大指摘がない。
8. release artifactの公開、署名、tag、pushはユーザーの明示指示まで行わない。

## 5. Verification commands

project構成に合わせてscriptへ統合するが、最終的に少なくとも次をclean checkoutで実行する。

```powershell
dotnet restore BeMusicSeeker.sln
dotnet build BeMusicSeeker.sln -c Release --no-restore
dotnet test BeMusicSeeker.Tests/BeMusicSeeker.Tests.csproj -c Release --no-build
dotnet build tools/chart-info-compare/ChartInfoCompare.csproj -c Release
dotnet build tools/chart-info-export/ChartInfoExport.csproj -c Release
dotnet publish BeMusicSeeker.csproj -c Release -r win-x64 --self-contained true
dotnet publish BeMusicSeeker.Updater/BeMusicSeeker.Updater.csproj -c Release -r win-x64 --self-contained true
```

package lock導入後はclean locked restoreを追加する。publish artifactにはhash、file inventory、managed／native architecture、license inventoryを生成するが、公開はしない。

## 6. Unit policy

- package一件ではなくcompatibility corridorをunitにする。
- 一つのunitでsource update、旧reference削除、behavior／golden test、build、runtime smokeを閉じる。
- 「compileするshim追加」と「旧route削除」を別unitにして恒久seamを残さない。
- unknownなdependencyを複数同時にmajor upgradeしない。
- clean-machine、署名鍵、外部playerなど実行環境だけが不足する場合は、code／automated verificationを先に完了し、具体的なmanual gateだけを`EXTERNAL_BLOCKER`として残す。
