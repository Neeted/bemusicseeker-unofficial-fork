# Engineering Blockers

[応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [risk register](./RESPONSIVENESS_RISK_REGISTER.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## Current decision

.NET 10 retarget、dependency、data、updater、distribution profileは完了している。release-candidate操作で見つかったmodel-lock／UI wait cycleとstartup HTTP stallはH4で解消し、Full verification、repository publish UI smoke、fresh reviewを通過した。

pre-release deadlockの中途状態を救済するdurable recovery frameworkは導入しない。release operationを正常完了させ、filesystem／song DB差分は既存startup／manual file diff contractで収束させる。

## Concurrency closure

| ID | State | Work | Exit |
|---|---|---|---|
| `CONC-01` | completed | normal-library refreshのworker→UI synchronous waitとUI→catalog lock取得のdeadlock | deterministic regression、versioned non-blocking UI drain |
| `CONC-02` | completed | estimated-install broad lease内のUI／dialog／callbackと不要なlong-held writer guard | normal completion、post-guard publication、既存file-diff収束 |
| `CONC-03` | completed | app-wide sync wait／UI invoke／callback-under-lock candidate | current wait inventoryで`P0 active`／`P1 active`が0 |
| `GATE-03` | completed | full responsiveness interaction smoke、selected publish、fresh review | H4 Engineering Gate通過 |

## Completed migration corridors

TFM／SDK、managed dependencies、SQLite、archive／audio、native interop、existing-data、updater success／rollback、performance-selected managed bundle＋ReadyToRunはcompleted。応答性修正がpublish layoutへ触れない限り再計画しない。

## Post-engineering / release follow-up

| ID | Classification | Owner | Engineeringへの影響 |
|---|---|---|---|
| `MANUAL-01` | post-engineering manual acceptance | user | なし。runtime未導入clean x64 Windows／VMで実施 |
| `RELEASE-01` | release prerequisite | user／release owner | なし。BASS.NET provenance／redistribution証跡を公開前に確認 |
| `RELEASE-02` | release operation | user／release owner | なし。署名、tag、push、public publishは明示指示後 |
