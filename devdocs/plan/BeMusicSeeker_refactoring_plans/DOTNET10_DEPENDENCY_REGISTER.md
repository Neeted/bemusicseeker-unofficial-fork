# .NET 10 Dependency Register

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md)

調査基準日: 2026-07-26

versionは実行計画を固定するための候補baselineであり、各Outcome開始時に公式package／vendor情報を再確認する。新しいversionへ機械的に追随せず、behavior、license、native ABI、rollback evidenceを優先する。

## Managed dependencies

| ID | Current | Usage / risk | Target decision | Candidate baseline | Owner |
|---|---|---|---|---|---|
| `DEP-CFG-01` | System.Configuration.ConfigurationManager 10.0.10 | generated settings、`PortableSettingsProvider`、legacy user.config migration | app project／lockに10.0.10をdirect inputとして固定し、publish時は`runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.10`が供給する同identity assemblyを検証。section名／setting key／型／default／serializeAs、portable path、save／migration、long-path behaviorを維持し、Framework startup／runtime switchを退役。version authorityは`Directory.Packages.props` | System.Configuration.ConfigurationManager 10.0.10 + WindowsDesktop runtime pack 10.0.10 | `NET10-02` |
| `DEP-LOG-01` | NLog 6.1.4 | `NLogWrapper`がfile／performance／network／trace loggingを所有 | NLog 6 coreへ更新。custom network／trace target、archive／encoding／channel behavior、publish outputを検証済み。version authorityは`Directory.Packages.props` | NLog 6.1.4 | `NET10-02` |
| `DEP-JSON-01` | Newtonsoft.Json 13.0.4 | persisted／external JSON ownersで使用 | 13.0.4へpatch更新し、既存ownerのsettings／file／DB／update JSON behaviorとpublish resolutionを確認。version authorityは`Directory.Packages.props` | Newtonsoft.Json 13.0.4 | `NET10-02/04` |
| `DEP-RES-01` | System.Resources.Extensions 10.0.10 | `GenerateResourceUsePreserializedResources`、`Images.resx`のembedded icon serialization／runtime load | 10.0.10をapp project／lockのdirect inputとして固定し、publish時は`runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.10`が供給する同identity assemblyを検証。11 iconのResourceManager／typed accessor／XAML converterとlegacy `libs` cleanup契約を維持。version authorityは`Directory.Packages.props` | System.Resources.Extensions 10.0.10 + WindowsDesktop runtime pack 10.0.10 | `NET10-02` |
| `DEP-TEST-01` | Test SDK 18.8.1, MSTest 3.6.4 | migration verification | Test SDK 18.8.1とMSTest 3.6.4をこの移行基線としてretainし、test project／win-x64 lock graphとlocked restoreを所有。discovery／DataRow／parallelism／failure diagnosticsを維持し、MSTest major upgradeは移行完了条件に含めない。version authorityは`Directory.Packages.props` | Test SDK 18.8.1、MSTest 3.6.4 | `NET10-02` |
| `DEP-AN-01` | Roslynator 4.15.0 | build analyzer | `Directory.Packages.props`とapp lockで3 analyzer packageを4.15.0へ固定し、analyzerは`PrivateAssets=all`／runtime output外を維持。solution Roslynator analysisは0 diagnostics。CLIは`.config/dotnet-tools.json`の0.12.0を継続し、packageとtoolを混同しない | Roslynator analyzer packages 4.15.0、CLI 0.12.0 | `NET10-02` |
| `DEP-UI-01` | LivetCask.Core／Mvvm／EventListeners 4.0.2 | ViewModel、command、notification、dispatcher、listenerで広範利用 | `Directory.Packages.props`の4.0.2をapp／test lockへ固定し、旧tracked Livet binary／XAML DataContextDisposeActionを退役。WPF event／selection／focusなどのterminal applyはView境界へ残す。Livet.MessagingとMicrosoft.Xaml.BehaviorsはLivetCaskのtransitive runtime closureとしてpublishへ含める | LivetCask.Core／Mvvm／EventListeners／Messaging 4.0.2 / zlib-libpng、`Microsoft.Xaml.Behaviors.Wpf` 1.1.31 / MIT | `NET10-03` |
| `DEP-UI-02` | System.Windows.Interactivity／Expression Interactions | legacy XAML behavior namespace | direct callerを退役し、LivetCaskのlocked transitive `Microsoft.Xaml.Behaviors.Wpf` 1.1.31だけをruntime closureとして保持 | Microsoft.Xaml.Behaviors.Wpf 1.1.31 (transitive) | `NET10-03` |
| `DEP-UI-03` | MetroRadiance 3 DLL | Window chrome usageが限定的 | WPF WindowChrome／Window.IsActive resourceへ置換し削除 | package更新ではなくremove | `NET10-03` |
| `DEP-UI-04` | Expression Drawing／Effects | XAML search glyph | WPF Ellipse／Path／Geometryへ置換し削除 | remove | `NET10-03` |
| `DEP-OS-01` | retired Windows API Code Pack 2 DLL | file／folder picker | WPF OpenFileDialog／OpenFolderDialogへ置換し、HintPath／tracked binary／現行noticeを削除。updaterの旧install cleanupだけは保持 | framework API | `NET10-03` |
| `DEP-XAML-01` | QuickConverter HintPath (retired) | 旧XAMLの式変換 | MainWindow／PlaybackPanel／SettingDialog／dialogのpresentation routeをtyped converter／MultiBinding／triggerへ置換し、runtime登録、HintPath、binaryを削除 | typed WPF presentation bindings | `NET10-04 Q1` |
| `DEP-JSON-02` | DynamicJson HintPath (retired) | external JSONのdynamic access | explicit Newtonsoft.Json boundariesへ置換し、registration／upload／score viewer semanticsをgolden test化済み | Newtonsoft.Json 13.0.4 | `NET10-04` |
| `DEP-DOC-01` | Microsoft.Xml.SgmlReader 1.8.30 package (`SgmlReaderDll.dll`) | playlist／HTML parse | maintained packageへ置換し、unclosed HTML／attribute order fixtureを比較済み | Microsoft.Xml.SgmlReader 1.8.30 | `NET10-04` |
| `DEP-MISC-01` | IniLibrary HintPath (retired) | uBMplayのShift-JIS settings rewrite | source owner内の明示的なrewriteへ置換し、missing key／comment／byte restore／volume clampをtargeted testで確認 | remove legacy binary | `NET10-04` |
| `DEP-MISC-02` | System.Collections.Immutable HintPath (retired) | runtime collection support | direct HintPath／tracked binaryを削除し、.NET runtime pack供給をpublishで確認 | .NET 10 runtime pack | `NET10-04` |
| `DEP-DB-01` | sqlite.net HintPath (retired) | app／tests／2 toolsのstorage | sqlite-net-pclへ移行し、既存DB、schema、transaction、raw hydration、lock／failure契約を確認 | sqlite-net-pcl 1.11.285 | `NET10-05` |
| `DEP-DB-02` | hand-placed sqlite3.dll (retired) | native provider | SQLitePCLRaw bundleへ一元化し、win-x64 native assetをpublish／portable layoutで確認 | SQLitePCLRaw.bundle_e_sqlite3 3.0.4 | `NET10-05` |
| `DEP-ARC-01` | SevenZipExtractor DLL | archive extraction | PackageReference化しbehavior／native load検証 | SevenZipExtractor 1.0.19 | `NET10-06` |
| `DEP-AUD-01` | OggVorbis.NET64 DLL | Ogg decode | NVorbis parity spike。差異が大きければ明示retain | NVorbis 0.10.5候補 | `NET10-06` |

## NET10-02 closure evidence

NET10-02のmanaged packageは公式NuGet packageをsourceとし、version authorityを`Directory.Packages.props`、resolved graphを各projectの`packages.lock.json`へ分離して記録する。licenseはpackage metadataで確認した。

| ID | Source / license | Version authority / lock owner | Evidence |
|---|---|---|---|
| `DEP-CFG-01` | NuGet `System.Configuration.ConfigurationManager` 10.0.10 / MIT、WindowsDesktop runtime pack | app project／root lock | settings wire、portable migration、long-path、SCD publishでruntime-pack resolutionを確認 |
| `DEP-LOG-01` | NuGet `NLog` 6.1.4 / BSD-3-Clause | app project／root lock | `NLogWrapper` route、Full verification、SCD log startupを確認 |
| `DEP-JSON-01` | NuGet `Newtonsoft.Json` 13.0.4 / MIT | app project／root lock | persisted／external JSON contract、Full verification、SCD resolutionを確認 |
| `DEP-RES-01` | NuGet `System.Resources.Extensions` 10.0.10 / MIT、WindowsDesktop runtime pack | app project／root lock | 11 icon resource contract、XAML converter、SCD runtime-pack resolutionを確認 |
| `DEP-TEST-01` | NuGet `Microsoft.NET.Test.Sdk` 18.8.1、MSTest 3.6.4 / MIT | test project／test lock | discovery、DataRow、category、DoNotParallelize、Full suiteを確認 |
| `DEP-AN-01` | NuGet Roslynator analyzer packages 4.15.0 / Apache-2.0; CLI 0.12.0 / tool manifest | app project／root lock、CLI manifest | 0 diagnostics、analyzer assetのruntime／publish除外を確認 |
| `DEP-UI-01` | NuGet LivetCask.Core／Mvvm／EventListeners／Messaging 4.0.2 / zlib-libpng; Microsoft.Xaml.Behaviors.Wpf 1.1.31 / MIT | app project／root lock | Release outputの`Livet.Core.dll`／`Livet.EventListeners.dll`／`Livet.Messaging.dll`／`Livet.Mvvm.dll`／`Microsoft.Xaml.Behaviors.dll`、旧`Livet.dll`／`Livet.Extensions.dll`不在、XAML DataContextDisposeAction退役、対象テストを確認 |

## NET10-03 L2 closure evidence

| ID | Source / license | Version authority / lock owner | Evidence |
|---|---|---|---|
| `DEP-UI-02` | WPF framework API; LivetCask transitive `Microsoft.Xaml.Behaviors.Wpf` 1.1.31 / MIT | app lock／LivetCask package graph | MainWindow／dialog XAMLにlegacy interaction namespace／behavior callerがなく、transitive behavior assemblyだけをpublishへ含めることを確認 |
| `DEP-UI-03` | WPF framework `WindowChrome`／`Window.IsActive` | app project | MainWindowのcaption command／hit-test、active appearance、maximized marginをnative WPF routeで維持し、MetroRadiance DLL／HintPath／現行noticeを削除 |
| `DEP-UI-04` | WPF `Ellipse`／`Path`／`Geometry` | app project | chart／playlist summary両search glyphをnative WPF shapeへ置換し、Expression Drawing／Effects DLL／現行noticeを削除 |

## NET10-03 L3 closure evidence

| ID | Source / license | Version authority / lock owner | Evidence |
|---|---|---|---|
| `DEP-OS-01` | WPF framework `OpenFileDialog`／`OpenFolderDialog` | app project | UiDialogCoordinatorのfile／folder routeをframework dialogへ置換し、typed file／folder result、filter／initial directory、multi-select、failure contract、all production callers、portable layout、updater legacy cleanup、現行notice／tracked Code Pack binary削除を確認 |

## NET10-04 D1 closure evidence

| ID | Source / license | Version authority / lock owner | Evidence |
|---|---|---|---|
| `DEP-JSON-02` | Newtonsoft.Json 13.0.4 / MIT | app project／root lock | DynamicJson binary／HintPath／noticeを退役し、playlist、library IR、score viewerのtyped JSON routeとgolden testを確認 |
| `DEP-DOC-01` | Microsoft.Xml.SgmlReader 1.8.30 / Apache-2.0 | app project／root lock | package-owned `SgmlReaderDll.dll`で外部playlist HTMLを解析し、unclosed documentとattribute orderのfixtureを確認 |
| `DEP-MISC-01` | source-owned Shift-JIS rewrite / no external dependency | app project／test fixture | uBMplayの6設定値、section/key add semantics、failure swallowing、original byte restore、volume clampを確認し、IniLibrary binary／HintPath／noticeを退役 |
| `DEP-MISC-02` | .NET 10 runtime pack `System.Collections.Immutable.dll` / MIT | runtime pack／publish output | direct HintPath、tracked binary、portable required root entryを削除し、runtime pack outputとlegacy forbidden-path cleanupを確認 |

## NET10-05 S1 closure evidence

| ID | Source / license | Version authority / lock owner | Evidence |
|---|---|---|---|
| `DEP-DB-01` | sqlite-net-pcl 1.11.285 / MIT | app／test／2 tool project locks | old HintPath／tracked DLLを削除し、SQLite-net API、既存DB／schema／transaction、raw string／NULL hydration、real Busy／Locked contention、startup／reopen behavior、locked restoreを確認 |
| `DEP-DB-02` | SQLitePCLRaw.bundle_e_sqlite3 3.0.4 / Apache-2.0; SourceGear.sqlite3 3.53.3 native / SQLite Public Domain | app／test／2 tool project locks | RuntimeBootstrapと2 tool entryでproviderを一度初期化し、framework-dependent portable outputでは`runtimes/win-x64/native/e_sqlite3.dll`、SCD publishではrootの`e_sqlite3.dll`を各1点だけ含め、旧sqlite3 assetsとvendor DLLを削除 |

`SQLitePCLRaw.bundle_e_sqlite3` は NuGet metadata の Apache-2.0 を適用し、ライセンス本文は `third_party/licenses/11-Apache-2.0.txt` を参照する。native asset は version 3.53.3 に固定した [`SourceGear.sqlite3` package](https://www.nuget.org/packages/SourceGear.sqlite3/3.53.3) が供給する `e_sqlite3.dll` であり、SQLite upstream の Public Domain notice (`third_party/licenses/12-SQLite-Public-Domain.txt`) を同梱する。

## Vendor / native dependencies

| ID | Current | Decision | Required evidence | Owner |
|---|---|---|---|---|
| `DEP-AUD-02` | Bass.Net＋BASS native family | vendor-supported x64組合せへ更新または明示retain | exact version、license／redistribution、ABI、device／decode／shutdown smoke | `NET10-06` |
| `DEP-ARC-02` | 7z.dll | SevenZipExtractorと整合するx64 binaryをpublish | version、license、encrypted／failure／path-safety tests | `NET10-06` |
| `DEP-NATIVE-01` | Everything3_x64／EverythingBridge_x64 | x64 bridgeをretain可能 | ABI、installed／absent behavior、publish path、license | `NET10-06` |
| `DEP-UIH-01` | WPF＋WinForms host＋legacy WebBrowser／COM | 初回移行ではretain | startup、host creation、navigation、shutdown、clean-machine smoke | `NET10-03/08` |

## Removal rule

HintPathを消しただけで完了にしない。source usage、XAML、reflection string、resource、test、publish outputを確認し、置換routeと旧binaryの双方を同じunitで閉じる。vendor binaryをretainする場合は、理由、exact version、source、license、architecture、runtime load testをこの台帳へ記録する。
