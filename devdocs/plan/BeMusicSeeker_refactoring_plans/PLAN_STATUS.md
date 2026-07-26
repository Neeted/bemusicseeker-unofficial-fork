# PLAN_STATUS

[terminal refactoring](./BeMusicSeekerリファクタリング計画.md) / [.NET 10 migration](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

最終レビュー日: 2026-07-26

## Current checkpoint

- observed committed HEAD: `5658b4f386e0f66f9b90ed15ee3810caf3d78179`
- observed worktree: clean
- Release Freeze: active
- `git push`／tag／release／public publish: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／owner整理: **substantially complete**
- strict Refactoring Completion Gate: **not yet met**
- reason: `RFR-02` の observable playback-start residual
- .NET 10 migration readiness: architecture is sufficient to start after terminal closure; dependency／runtime／deployment migration remains

最新のB1候補でFull verificationは`3357 passed / 13 skipped / 0 failed`、Roslynatorは`0 diagnostics`。B1のfresh reviewとcommit後にB2へ進む。

## Active outcome

- active outcome: `RFR-01 Refactoring closure`
- active execution package: `Terminal architecture residual closure`
- execution anchor: `RFR-B2 Observable playback-start contract`
- planner state: active batch already materialized; plannerを起動しない

## Active implementation batch

| Unit | State | Closure family | Exit |
|---|---|---|---|
| `B1` | completed | library file-operation composition | `BMSLibrary`保持broad port／nested adapter退役、全file mutation routeとtests維持 |
| `B2` | active | observable playback start | non-event `async void PlayStart`退役、Task／typed resultとfailure tests |
| `CLOSE` | pending | Refactoring Completion Gate | Full verify、Release UI smoke、fresh outcome review、Gate met、`NET10-01` active |

active／pending unitがある間はunit-plannerを再起動しない。各unitのstatus更新はproduction code commitへ含める。

## Review evidence

### Established

- shellはchild feature ownerをcompositionし、XAMLはchild ownerをbinding rootとして利用する。
- Viewに残る主要処理はWPF event、selection、focus、hit-test、drag／ContextMenu／typed presentationのterminal applyで説明できる。
- library、playlist、package、LR2、configuration、path、process、native integrationにowner／adapterがある。
- app、tests、updaterのdisposable .NET 10 build rehearsalは成功記録がある。

### Blocking residuals

1. `BeMusicSeeker/Models/InternalBMSAutoPlayerSoundOnly.cs`の`PlayStart`は非event `async void`で、`PlaybackPanelViewModel`が非同期failureを観測できない。

### Migration baseline

- app、tests、updater、2 toolsは現在`net472`。
- appはWPF＋WinForms、x64、managed HintPathとnative DLLを含む。
- current `app.config`はframework startupと`libs` private probingを持つ。
- `global.json`は.NET SDK `10.0.301`を指定する。
- target distributionはwin-x64 Self-contained folder publish。main appはuntrimmed／non-single-file。

## Next outcome

`CLOSE`完了時に次へ更新する。

- active outcome: `NET10-01 Retarget and complete project baseline`
- active execution package: `.NET 10 Self-contained migration`
- execution anchor: `NET10-01 project retarget baseline`
- active implementation batch: empty; planner required once
