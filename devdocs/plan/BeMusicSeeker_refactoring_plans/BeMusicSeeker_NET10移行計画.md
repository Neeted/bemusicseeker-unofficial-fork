# BeMusicSeeker .NET 10 Self-contained 移行計画

[現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## 1. 現在の判定

MVVM／owner整理と.NET 10移行の主要実装は完了している。app、tests、updaterは`net10.0-windows`、2 toolsは`net10.0`へ移行済みで、managed／native dependency、SQLite、Self-contained publish、existing-data、update／rollbackの自動受入れが成立している。

Codexの.NET 10 Engineering Gateは`NET10-09 Final engineering closure`で完了した。

- 最新servicing SDKでSelf-contained artifactを再生成した。
- 公式single-fileを有限に評価し、正式layoutとして採用した。
- 選択した配布構成で全自動Engineering Gateを閉じた。

`.NET Desktop Runtime`未導入machine／VMでの確認とproprietary license証跡はCodex工程ではなく、Engineering Gate後のユーザー手動受入れ／release prerequisiteとする。

## 2. Target matrix

| Project | Target | 配布／検証 |
|---|---|---|
| `BeMusicSeeker.csproj` | `net10.0-windows`, x64 | win-x64 Self-contained single-file。`lang`／config／native ownerは隣接、trimming／ReadyToRun無効 |
| `BeMusicSeeker.Tests` | `net10.0-windows`, x64 | full test／architecture／publish behavior |
| `BeMusicSeeker.Updater` | `net10.0-windows`, x64 | win-x64 Self-contained single-file |
| `chart-info-compare` | `net10.0`, x64 | locked restore／Release build／DB behavior |
| `chart-info-export` | `net10.0`, x64 | locked restore／Release build／DB behavior |

Self-contained main appはmachine-installed runtimeへ依存しない一方、machine側のruntime servicingへ自動追随しない。そのため最終artifactはEngineering Gate時点の公式最新.NET 10 servicing SDKで再生成する。

## 3. 完了済みcorridor

| Outcome | 状態 | 成立した境界 |
|---|---|---|
| `NET10-01` | completed | 全5 projectのretarget、solution／verification baseline |
| `NET10-02` | completed | central package versions、lock graph、configuration／logging／resource／test host |
| `NET10-03` | completed | LivetCask、native WPF chrome／picker、legacy WPF dependency退役 |
| `NET10-04` | completed | typed XAML binding、JSON／HTML／INI helper modernize |
| `NET10-05` | completed | sqlite-net-pcl／SQLitePCLRawと既存DB互換性 |
| `NET10-06` | completed | archive、NVorbis、BASS、Everythingのx64 runtime owner |
| `NET10-07` | completed | main app folder SCD、updater single-file SCD、transaction／rollback |
| `NET10-08` | completed | existing-data roundtripとold-to-new update／rollbackの自動受入れ |

詳細なunit／commit履歴はGit historyに委ねる。

## 4. Completed outcome: `NET10-09 Final engineering closure`

F1〜F3とHANDOFFを完了し、以後plannerは起動しない。手動受入れとrelease prerequisiteは別checklistへhandoffする。

### `F1 SDK servicing baseline`

目的:

- `global.json`の`10.0.301`を、計画更新時点の公式最新servicing SDK `10.0.302`へ更新する。
- 再現性のためroll-forwardを同じfeature bandのpatch範囲へ限定する。
- Self-containedに含まれる.NET runtimeを最新servicing levelで再生成する。

作業:

1. `global.json`を`10.0.302`、`rollForward: latestPatch`へ更新する。
2. app、tests、updater、2 toolsをclean locked restoreし、必要なlock差分だけを更新する。
3. Release build、full tests、analyzerを実行する。
4. selected app／updater profileをpublishし、startup、layout、existing-data、update／rollbackの代表自動受入れを再実行する。

Exit:

- `dotnet --info`とartifact inventoryが選択SDK／runtimeを示す。
- package major upgradeやbehavior changeを混ぜず、全baseline verificationが通る。

### `F2 Distribution layout decision`

#### Baseline

F2開始時のmain appは標準のfolder Self-contained publishであった。この形式ではmanaged dependencyとruntime fileが`BeMusicSeeker.exe`の隣に並ぶ。これらを単純に`libs`へ移すと標準host／`.deps.json` resolutionから外れるため、その方式は採用しない。F2完了後の正式profileは公式single-fileである。

禁止する回避策:

- 独自`AssemblyLoadContext`／`AssemblyResolve`
- Framework-style private probing
- `.deps.json`の手動書換え
- publish後のmanaged DLL relocation／削除
- loader専用wrapper executable
- updaterだけが知る二重layout

#### Bounded single-file evaluation

exe隣接DLLを減らす唯一の候補として、公式SDKのsingle-fileを一度だけ評価する。

候補profile:

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<PublishTrimmed>false</PublishTrimmed>
<PublishReadyToRun>false</PublishReadyToRun>
```

`IncludeAllContentForSelfExtract`、trimming、ReadyToRun、NativeAOTは使わない。`lang`、`libs/x64`、`native`、user-editable／application contentは既存owner directoryへ残してよい。

作業:

1. `ApplicationPathSnapshot`、audio encoder／writer、BASS runtime等のpath利用を棚卸しし、exe pathは`Environment.ProcessPath`、application baseは`AppContext.BaseDirectory`へ統一する。`Assembly.Location`へfallbackしない。
2. candidate profileを作り、single-file compatibility warningを解消する。
3. candidate publishからstartup／shutdown、settings、SQLite、playlist、scan、package、archive、BASS／audio、Everything installed／absent、existing-dataを検証する。
4. `publish.ps1`、portable layout validator、update manifest、success／rollback acceptanceをcandidate layoutへ通す。
5. root managed dependencyを前提にするtestsは、single-fileを採用する場合だけbundle contractへ更新する。

Adoption rule:

- 公式profileとboundedなpath修正だけで全自動受入れが通る場合は`ADOPTED`とし、main appの正式profile／package contractをsingle-fileへ切り替える。
- third-party dependency、native load、updater contractを成立させるため禁止回避策が必要、またはbehavior／startup costが受入不能なら`NOT_ADOPTED`とする。candidate profile／probe seamを退役し、現行folder Self-containedを最終構成として明記する。
- `NOT_ADOPTED`は正常完了であり、再調査やユーザー承認待ちにしない。

### `F3 Automated Engineering Gate`

選択したmain-app profileに対して次をclean checkout相当で閉じる。

1. 全5 projectのlocked restore、Release build、full tests。
2. Roslynator／format／warning gate。
3. main appとupdaterのwin-x64 Self-contained publish。
4. package／layout validator、hash／managed／native／license inventory。
5. publish-folderからのstartup／shutdownと主要runtime smoke。
6. existing-data acceptance。
7. pre-NET10 packageからのupdate success／fault rollback acceptance。
8. frozen snapshotのfresh outcome review、重大指摘修正後の再検証／fresh review。

`.NET Desktop Runtime`未導入machine／VMはこのGateへ含めない。repository内で自動実行できるevidenceがすべて通ればEngineering Gateを満たす。

### `HANDOFF Engineering completion`

F3完了と同じclosure cycleで次を行う。

- `PLAN_STATUS.md`を`engineering migration: complete`、active batch emptyへ更新する。
- 選択した配布profileとlayout decisionを依存関係台帳へ反映する。
- `.NET Desktop Runtime`未導入machine／VMの確認とBASS.NET entitlement確認を[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)へhandoffする。
- manual結果待ちでCodexを停止せず、Engineering Outcomeを完了する。

## 5. Engineering Completion Gate

次をすべて満たしたとき、Codexの.NET 10移行作業を完了とする。

1. tracked projectに`net472`がない。
2. 全5 projectが最新servicing baselineでlocked restore／Release buildでき、full testsとanalyzerが通る。
3. main appとupdaterのwin-x64 Self-contained publishが再現できる。
4. selected layoutが標準hostまたは公式single-fileだけで成立し、custom probing／loader／relocationを持たない。
5. retained native componentがversion、source、license notice、ABI test、publish ownerを持つ。
6. existing-data、update success、rollbackの自動受入れが通る。
7. fresh outcome reviewで重大指摘がない。

次はEngineering Gateの条件ではない。

- `.NET Desktop Runtime`未導入clean machine／VMでのユーザー手動確認
- code signing、tag、push、public release／publish
- BASS.NETのlicensee／registration／redistribution権限のユーザー確認

これらはrelease前に[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)で閉じる。
