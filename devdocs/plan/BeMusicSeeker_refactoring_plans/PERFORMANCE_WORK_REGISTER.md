# Performance Work Register

[維持方針](./BeMusicSeeker_性能回帰改善計画.md) / [現在地](./PLAN_STATUS.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

このregisterはcurrent-onlyである。完了したunit / commit履歴はGit historyへ委ねる。

| ID | State | Current invariant |
| --- | --- | --- |
| `UI-01` | protected completion | stable summary source、atomic main-table commit、data-only invalidationを維持する |
| `SAFE-01` | protected completion | normal refreshはnon-blocking producer。sync UI wait / callback-under-lockを復活させない |
| `START-01` | protected completion | folder tree / optional post workをoperabilityとrequired initializationへ再接続しない |
| `INSTALL-01` | protected completion | catalog、destination resource index、pending stateをoperable前に完成させる |
| `PLAYLIST-01` | protected completion | local entries hydrationはrequired。automatic URL / reference / external syncはpostとして可視化する |
| `SORT-01` | allowed optional cost | virtual sort / adjacent index prewarmはpost。未完時はon-demand fallback |
| `DIST-01` | final | bundle-r2rを正本とする。folder-r2rとの差はselection threshold未満 |

現在のblocking residualはない。
