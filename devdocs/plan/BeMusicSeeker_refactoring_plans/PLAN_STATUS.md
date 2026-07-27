# PLAN_STATUS

[terminal refactoring](./BeMusicSeekerリファクタリング計画.md) / [.NET 10 migration](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

最終レビュー日: 2026-07-27

## Current checkpoint

- active outcome base commit: `05a3395d`
- observed worktree: clean
- Release Freeze: active
- `git push`／tag／release／public publish: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／owner整理: **substantially complete**
- strict Refactoring Completion Gate: **met**
- reason: B1/B2のproduction route、behavior tests、Full verification、Release UI smoke、fresh outcome reviewを完了した
- .NET 10 migration readiness: architecture is sufficient to start after terminal closure; dependency／runtime／deployment migration remains

Full verificationは`3410 passed / 16 skipped / 0 failed`、Roslynatorは`0 diagnostics`。Refactoring Gate時点のRelease buildは`bin\\x64\\Release\\net472\\BeMusicSeeker.exe`で、現在のNET10 smoke対象は`bin\\x64\\Release\\net10.0-windows\\BeMusicSeeker.exe`。メソッド単位並列による全体実行限定失敗を避けるため、テストアセンブリはクラス単位並列へ揃えた。

## Active outcome

- active outcome: `NET10-04 Converter, JSON and document helpers`
- active execution package: `.NET 10 helper dependency modernization`
- execution anchor: `NET10-04 helper replacement corridor`
- planner state: empty batch; planner is required once for the next NET10-04 route

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

active／pending unitがある間はunit-plannerを再起動しない。Q1の実装・検証・レビュー・commitを完了したため、NET10-04の次の未完routeでplannerを一度だけ起動する。

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

## Next outcome

- active outcome: `NET10-04 Converter, JSON and document helpers`
- active execution package: `.NET 10 helper dependency modernization`
- execution anchor: `NET10-04 helper replacement corridor`
- active implementation batch: empty; planner is required once for the next NET10-04 route
