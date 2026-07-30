# .NET 10 Performance Engineering Evidence

[性能計画](../plan/BeMusicSeeker_refactoring_plans/BeMusicSeeker_性能回帰改善計画.md) / [現在地](../plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md) / [作業register](../plan/BeMusicSeeker_refactoring_plans/PERFORMANCE_WORK_REGISTER.md)

この文書はcurrent-only reportである。過去unitの逐次logはGit historyとignored artifactsへ委ねる。

## Current decision

reviewed HEAD:

```text
72445a5029ba12356ac50340e4a399f7292f349f
```

`PERF-01`で次は成立した。

- normal refresh deadlock safety。
- full-library summary用のUI-thread ordered-row全件materialization退役。
- playlist detail compute／applyの高速route。
- resource／song-table／estimation／scan／parserのcomponent allocation削減。
- full tests、analyzer、Self-contained publish、existing-data、update／rollback。

しかし、2026-07-31の実データlogでは一覧遷移のユーザー体感遅延が残っているため、performance engineering completionを取り消し、`PERF-02`を開始する。

## 2026-07-31 symptom evidence

この数値は厳密なA/B Gateではなく、current .NET 10の修正優先度を決める。

| Route | net472 sample | current .NET 10 sample | Interpretation |
|---|---:|---:|---|
| playlist summary初回 | input→first visible 約0.69 s | 約1.18 s | current compute約0.32 sよりUI applyが長い |
| playlist summary再訪 | summary presentはほぼ即時 | compute約0.015 s、input→visible 約0.90 s | cache computeではなくsource／binding／notificationが支配 |
| playlist detail | 約0.13～0.25 s | 約0.10～0.18 s | current routeは保護対象 |
| detail→full library | 約0.38～0.45 s | 約1.17 s | declared rows／column workはほぼ0～1 ms。blind presentation interval |
| startup ready | 約38.2 s | 約24.9 s | startup全体は今回の最優先ではない |

## Source correlation

### High-confidence direct defects

1. `ISettingsDialogWorkspacePort.SubscribePlaylistTableChanges`は、`PlaylistWorkspaceViewModel.PropertyChanged`全体へ接続されている。
2. playlist summaryはUI threadで毎回`ObservableCollection`を作り、source identityを交換する。
3. `CustomTableView.OnItemsSourceChanged`はdata-only変更でもcolumn layoutをinvalidateする。
4. detail→libraryはold detail source clear、new source apply、playlist-related PropertyChangedを別々にpublishする。
5. performance markerはNLog `FileTarget`へ同期書込みされる。
6. `UseAsyncChartRowsViewBinding`はproduction／XAML consumerがなく、notificationだけを増やしている。

これらはproduction benchmarkを待たずに修正する。

## Evidence policy for PERF-02

- net472側を変更・再build・再計測しない。
- production dataをCodex／CI prerequisiteにしない。
- `DIRECT_FIX`／`LIKELY_OPTIMIZATION`はbehaviorとconcurrencyをtestし、実機benchmarkがなくても実装する。
- synthetic corpusが既にあるrouteではcomponent evidenceを利用する。
- instrumentation-onlyで既知遅延を完了扱いにしない。
- final real-data measurementは全engineering作業後にユーザーが一度行う。
- raw log／trace／benchmark outputはignored artifactに置き、current summaryだけを更新する。

## Current acceptance state

| Area | State |
|---|---|
| functional correctness | met |
| deadlock safety | met; protect |
| playlist detail route | acceptable; protect |
| playlist summary user-visible transition | open |
| detail／summary→library transition | open |
| table invalidation contract | open |
| diagnostic logging hot-path cost | open |
| startup／estimation／scan／parse second-wave optimization | open |
| final user real-data check | pending after `F6`; non-blocking |
