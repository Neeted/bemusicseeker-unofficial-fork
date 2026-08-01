# Post-engineering Manual Acceptance

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [distribution](../../acceptance/net10-distribution-performance.md)

この文書はCodex Engineering Gate完了後にユーザーが行う確認だけを持つ。未実施をactive outcome、planner停止条件、`EXTERNAL_BLOCKER`にしない。

## Completed observation — List transitions

2026-07-31のfinal .NET 10 artifactで、playlist summary、playlist detail、full libraryの画面遷移は体感上問題なしとユーザーが確認した。logでもcached summary約32 ms、detail約129 ms、full library約23～60 msである。

## MANUAL-01 — Cold boot and distribution

`PERF-03`完了後、同じfinal commit／settings／dataで一度ずつ行う。

### A. bundle-r2r

1. PCを再起動し、通常の常駐処理が落ち着くまで待つ。
2. final `bundle-r2r` artifactを一回起動する。
3. 次を保存する。
   - process start時刻
   - managed startup start
   - `startup_ready_ui`
   - `startup_ready_operable`
   - `startup_initialization_complete`
   - folder refresh stage
4. 起動、一覧遷移、推定、終了を確認する。

### B. folder-r2r

同じ手順をfinal `folder-r2r` artifactで一回行う。

### C. Update success / rollback

各artifactについて、既存の一世代前packageからのupdate successを一回確認し、再起動後にsettings、既存data、native assetsが維持されることを確認する。その後、検証可能なfault（破損または不正manifest等）を一回投入し、updaterがfaultを失敗として報告し、旧artifactと既存dataへrollbackできることを確認する。成功／rollbackともにインストール版ではなく、比較対象artifactとrepositoryのupdaterを使う。

### Selection

- folder-r2rのprocess start→`startup_initialization_complete`が5秒以上かつ15%以上短く、機能／update／rollbackに問題がなければfolder-r2rを選択する。
- 閾値未満、またはcode fixでcold gapが消えた場合はbundle-r2rを維持する。
- 差が同じmanaged readiness stage内に残る場合、配布形式ではなくcode routeのfollow-upとする。

manual resultがfolder選択条件を満たす場合、profile、publish script、layout validator、update manifestを切り替える一つのfollow-upを依頼する。

## MANUAL-02 — Runtime-free machine

.NET Desktop Runtime未導入のclean x64 Windows／VMで、選択したSelf-contained artifactの起動、基本操作、終了、updaterを確認する。

## RELEASE-01 — Distribution rights

BASS.NET等のproprietary／vendor componentについて、licensee、registration、redistribution証跡を公開前に確認する。
