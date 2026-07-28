# PLAN_STATUS

[terminal refactoring](./BeMusicSeekerリファクタリング計画.md) / [.NET 10 migration](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

最終レビュー日: 2026-07-27

## Current checkpoint

- active outcome base commit: `6d5136b4`
- observed worktree: clean
- Release Freeze: active
- `git push`／tag／release／public publish: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／owner整理: **substantially complete**
- strict Refactoring Completion Gate: **met**
- reason: P1/P2のproduction route、behavior tests、Full verification、Release UI smoke、fresh outcome reviewを完了した
- .NET 10 migration readiness: architecture is sufficient to start after terminal closure; dependency／runtime／deployment migration remains
- NET10-06 technical BASS runtime closure: completed; proprietary Bass.Net source／licensee／registration evidence remains the `NATIVE-01` external-gate and is not treated as release permission

Full verificationは`3480 passed / 16 skipped / 0 failed`、Roslynatorは`0 diagnostics`。既存データ受入れはcleanなSelf-contained app publishからstandalone／LR2の各profileを2回起動し、portable settings、song DBのsemantic rows、fixture hash、graceful shutdownを確認する。Refactoring Gate時点のRelease buildは`bin\\x64\\Release\\net472\\BeMusicSeeker.exe`で、現在のNET10 smoke対象は`bin\\x64\\Release\\net10.0-windows\\BeMusicSeeker.exe`。メソッド単位並列による全体実行限定失敗を避けるため、テストアセンブリはクラス単位並列へ揃えた。

## Active outcome

- active outcome: `NET10-08 Existing-data and clean-machine acceptance`
- active execution package: `.NET 10 existing-data／clean-machine acceptance`
- execution anchor: `planner required`
- planner state: `E1 completed; E2 completed; planner required; NATIVE-01 external-gate`

## Active implementation batch

| Unit | State | Closure family | Exit |
|---|---|---|---|
| `N1 APP-CFG` | completed | application runtime／portable settings baseline | app／testsの.NET 10 retarget、settings wire format、code-page bootstrap、managed loader、startup DB-openを同じrouteで検証 |
| `N2 UPDATER` | completed | updater executable／protocol corridor | updater retarget、protocol／swap／rollback／restart behavior、temporary self-contained smoke |
| `N3 CHART-TOOLS` | completed | chart metadata DB tools／outcome closure | 2 tools retarget、solution／verificationを5 projectへ拡張、CLI／DB behavior、NET10-01完了 |
| `M1 LOG-RUNTIME` | completed | NLog 6 logging／runtime corridor | NLog 6.1.4、全logging route、legacy addon cleanup、package/layout、behavior test、temporary publish smoke |
| `M2 JSON-CONTRACT` | completed | Newtonsoft.Json／persisted and external JSON corridor | Newtonsoft.Json 13.0.4、既存 JSON owner、settings／file／DB／update contract、package／publish evidence |
| `M3 RESOURCE-ICONS` | completed | System.Resources.Extensions／embedded resource runtime corridor | 11 Images.resx icons、typed Icon accessors、XAML converter route、resource package／publish ownership |
| `M4 CFG-RUNTIME` | completed | System.Configuration／settings runtime corridor | ConfigurationManager 10.0.10、app.config framework seam退役、Properties.Settings／portable migration／long-path behavior、publish resolution |
| `M5 TEST-HOST` | completed | .NET 10 test execution host／deterministic restore corridor | Test SDK 18.8.1、test project lock、win-x64 locked restore、discovery／diagnostics／publish exclusion |
| `M6 PACKAGE-GATE` | completed | central package version／analyzer gate corridor | Directory.Packages.props、app／test lock ownership、Roslynator 4.15.0、locked restore、runtime／publish exclusion |
| `L1 LIVET-RUNTIME` | completed | LivetCask WPF presentation runtime corridor | LivetCask Core／Mvvm／EventListeners 4.0.2、通知／dispatcher／command／listener／lifetime behavior、legacy Livet asset retirement |
| `L2 WPF-CHROME` | completed | native WPF chrome／legacy behavior-visual closure | MainWindow chrome、activation appearance、caption command／hit-test、search glyph、legacy Interactivity／Expression／Metro route retirement |
| `L3 WPF-PICKERS` | completed | WPF file／folder picker corridor and Code Pack retirement | UiDialogCoordinatorのfile／folder route、picker result／failure contract、全production caller、portable layout、updater cleanup、dependency／blocker closure |
| `Q1 WPF-BINDINGS` | completed | QuickConverter presentation corridor closure | MainWindow、PlaybackPanelView、SettingDialog、EditableTextBlock、PlaylistPropertyDialogのtyped binding／trigger移行、旧markup／runtime登録／package route退役、UI behavior verification |
| `J1 TABLE-DOC` | completed | playlist JSON wire boundary | BMSTable／BMSTableEntryのheader／data／entry／course JSON、playlist persistence／external sync／export、DynamicJson route退役 |
| `J2 PLAYLIST-FEEDS` | completed | playlist catalog／recommendation／URL completion | 外部feed、recommended／estimation、Stella URL completionのtyped JSON化とcache／failure contract |
| `J3 LIBRARY-IR` | completed | ranking JSON boundary | BmsLibraryIrClient request／responseとIRDataCacheInfoのtyped JSON化、candidate／DB mutation order維持 |
| `J4 SCORE-RETIRE` | completed | Score Viewer boundary and DynamicJson retirement | Score Viewer gateway typed化、DynamicJson binary／HintPath／layout／notice／license退役 |
| `D1 DOC-HELPERS` | completed | external HTML document owner and managed helper closure | PlaylistExternalSyncOwnerのmaintained SGML route、uBMplayのShift-JIS settings rewrite／restore、IniLibrary／System.Collections.Immutableのlegacy asset retirement、resource snapshotとpackage／layout／notice整合 |
| `S1 SQLITE-RUNTIME` | completed | SQLite provider／storage／native runtime corridor | app／tests／2 toolsのprovider初期化、connection／repository／schema／transaction、既存DB／lock／failure契約、package／native layout、portable／publish evidence |
| `A1 ARCHIVE-RUNTIME` | completed | SevenZipExtractor／archive extraction corridor | package由来SevenZipExtractor、archive path safety、timestamp／cleanup／metadata import、portable native layout、旧HintPath／vendor asset退役 |
| `A2 OGG-DECODE` | completed | OGG decoder corridor | NVorbis parity、cache／fallback／audio behavior、旧Ogg asset退役 |
| `A3 BASS-RUNTIME` | completed | BASS managed／native runtime corridor | retained x64 ABI、absolute load／rollback、device resetとprocess-level release、playback／device／conversion／shutdown、native inventory。`NATIVE-01` external-gateは別管理 |
| `A4 EVERYTHING-RUNTIME` | completed | Everything SDK／bridge corridor | ABI、native lifetime、installed／absent fallback、publish／license inventory |
| `P1 SCD-ARTIFACT` | completed | Self-contained publish artifact／updater payload corridor | versioned app folder SCD、updater single-file SCD、clean package layout、publish-folder startup／`--version` smoke、single-file update payload |
| `P2 SCD-TRANSACTION` | completed | Self-contained update transaction／recovery corridor | exclusive writer、durable journal、rollback／recovery、restart／failure receipt |
| `E1 DATA-ROUNDTRIP` | completed | Existing-data SCD startup／shutdown／restart acceptance | isolated legacy settings／standalone DB／LR2 profile fixture、semantic receipt、two-start hydration、graceful shutdown／lock release |
| `E2 UPDATE-ROUNDTRIP` | completed | pre-NET10 package to current SCD update／rollback acceptance | historical package provenance、legacy updater handoff、success restart、fault-package rollback、semantic data preservation |

active／pending unitがある間はunit-plannerを再起動しない。E1／E2のacceptance runner、fixture、semantic receipt、update／rollback evidenceを完了し、次はclean-machine external gateのplannerへ進む。

## Review evidence

### Established

- shellはchild feature ownerをcompositionし、XAMLはchild ownerをbinding rootとして利用する。
- Viewに残る主要処理はWPF event、selection、focus、hit-test、drag／ContextMenu／typed presentationのterminal applyで説明できる。
- library、playlist、package、LR2、configuration、path、process、native integrationにowner／adapterがある。
- app、tests、updaterのdisposable .NET 10 build rehearsalは成功記録がある。
- NLogWrapperはNLog 6.1.4 core、application／install-performance archive、network／trace targetを所有し、temporary win-x64 Self-contained publishで起動とログ出力を確認している。

### Blocking residuals

- なし。Refactoring Completion Gateを満たした。

### Migration baseline

- app／tests／updaterは`net10.0-windows`、2 toolsは`net10.0`へretarget済み。全5 projectをsolution／verificationでRelease build対象にしている。
- appはWPF＋WinForms、x64、managed HintPathとnative DLLを含む。
- current `app.config`はuserSettings sectionとsetting dataのみを持ち、Framework startup／runtime switch／private probingには依存しない。
- `global.json`は.NET SDK `10.0.301`を指定する。
- target distributionはwin-x64 Self-contained folder publish。main appはuntrimmed／non-single-file。
- NET10-06 A3の技術検証は完了しているが、`DOTNET10_MIGRATION_BLOCKERS.md` の `NATIVE-01` external-gate（Bass.Net exact source archive／正式license／licensee scope／registration entitlement）は未解消である。

## Next outcome

- active outcome: `NET10-08 Existing-data and clean-machine acceptance`
- active execution package: `.NET 10 existing-data／clean-machine acceptance`
- execution anchor: `planner required`
- active implementation batch: `empty; planner required; NATIVE-01 external-gate`
