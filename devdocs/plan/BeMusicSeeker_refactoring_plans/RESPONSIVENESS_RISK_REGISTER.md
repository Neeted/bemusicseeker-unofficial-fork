# Responsiveness Risk Register

[現在地](./PLAN_STATUS.md) / [応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [性能register](./PERFORMANCE_REGRESSION_REGISTER.md)

current-only inventory。完了履歴はGit historyへ委ねる。

| ID | State | Boundary | Current decision |
|---|---|---|---|
| `RSP-001` | closed | estimated-install → normal refresh | producer-side synchronous UI waitを退役し、versioned async drainへ移行 |
| `RSP-002` | closed | estimated-install lock scope | UI／owner外callbackをguard外へ移動。incident専用recoveryは追加しない |
| `RSP-003` | closed | app-wide sync wait inventory | actual wait graphで分類し、known `BLOCKING=0` |
| `RSP-P01` | performance review | normal-library refresh drain | fake schedulerでcoalescing／one-turn boundを検証し、current .NET 10 logで実データqueue waitを観測可能にする |
| `RSP-P02` | performance review | playlist／main-list presentation | correlationでqueue、generation、terminal apply、first visibleを記録し、実データ数値は`MANUAL-02`へ渡す |
| `RSP-P03` | protected invariant | all performance fixes | synchronous UI wait、callback-under-lock、notification dropへ戻さない |

## Review rule

`Dispatcher.Invoke`、`.GetResult()`、raw lock bindingの存在だけではfindingにしない。producer lane、held guard、target lane、boundedness、deterministic test、current .NET 10 instrumentationで判定する。
