# テストスイート整理計画

> この文書は未完了作業を再開するための一時的な進捗記録であり、現行仕様の正本ではない。現行のテスト運用契約は `devdocs/spec/testing-strategy.md` に置く。全作業完了時は、必要な契約だけを同仕様へ統合し、この文書を削除する。

## 1. 目的

この作業は、テスト件数を機械的に減らすことではなく、次を同時に満たすことを目的とする。

- Functional の総実行時間を際限なく増やさず、command 全体を180秒以内に保つ。
- process-global state、WPF、native library、settings、固定DB/fileなどの競合を分離しつつ、根拠のない `DoNotParallelize` や場当たり的な直列化を減らす。
- 通常完了を `Sleep`、固定 `Delay`、polling で推定せず、owner が持つ receipt、event、idle task、barrierで待つ。
- A/B選定後のbenchmark、migration characterization、実装方針決定用の一時テストを退役する。
- source substring、private field名、method配置、呼出順などの実装詳細ではなく、永続結果、通知、threading、failure、cleanupを検証する。
- `verify-refactor.ps1` の process/window起動を必要最小限にし、非interactive WPF testは共有host上でoffscreenかつnon-activatingに実行する。
- Full を canonical Functional、単一のimmutable artifact、bounded phaseで構成し、失敗やtimeoutを隠さない。

## 2. 固定した判断

- observable behavior と failure contract を維持する。テストを通すための意味変更fallbackは追加しない。
- `DoNotParallelize` は、fresh owner/fake、GUID-scoped DB/fileなどによって共有resourceが解消された場合だけ外す。
- WPF application/dispatcher、native BASS、NLog、`Settings.Default`、resource/cultureなどprocess-wide stateを扱うfixtureは、所有境界があるまで明示的に分離する。
- contention/deadlockを作る短いwatchdogは許容するが、通常完了待ちには使わない。失敗時もgateを必ず解放し、開始済みtaskをbounded drainする。
- source-text/private-reflection testを退役する際は、同じ契約をtyped/public owner routeまたはcompiled WPF behaviorで確認する。production APIをテスト都合だけで広げない。
- runner、lane、shard、fixture配置を変更した場合は、同一条件のFunctionalを3回連続で実行する。各回180秒以内、tracked file不変、残留test processなしを確認する。

## 3. 現在の停止地点

- 実装作業を停止した時点のHEAD: `5ca2c547` (`test: retire catalog and lr2 reflection probes`)
- この文書以外のworktree差分: なし
- 最後に完了した実装: Unit 8のcatalog/LR2 private-reflection整理
- 次に予定していた作業: Unit 6の残存shell/settings source-text整理
- Unit 6用plannerは利用上限で失敗し、repositoryの編集は行っていない。再開時は改めてunit-plannerを実行する。

## 4. 基準値と現在値

| 指標 | 整理開始時 | 現在 | 注記 |
| --- | ---: | ---: | --- |
| `DoNotParallelize` | 76属性 / 47ファイル | 47属性 / 26ファイル | 現値は単純な属性行数。残存属性には任意Quick filterで必要なsafety boundaryを含む。 |
| `SourceTextTestHelper` | 417参照 / 20ファイル | 275参照 / 8ファイル | 現在は7 consumer + helper本体。 |
| Unit 8対象8ファイルのprivate-reflection | planner-core 286参照 | 大幅削減 | broad regexには正当なmetadata/reflectionや1契約内の複数API呼出しも含まれるため、単純なゼロ目標にはしない。 |
| Functional | 3,962 total / 3,951 pass / 11 opt-in skip、約143/134/134秒 | Unit 9受入時 3,970 terminal / 3,959 pass / 11 not executed、152.5/155.3/158.1秒 | Unit 7・8後の統合Functionalは未実施。 |

現在 `SourceTextTestHelper` を参照するファイルは次の8件である。

- `BeMusicSeeker.Tests/BmsLibraryMutationBoundaryTests.cs`
- `BeMusicSeeker.Tests/DialogRouteConsolidationTests.cs`
- `BeMusicSeeker.Tests/Lr2PlayHistorySchemaUiTests.cs`
- `BeMusicSeeker.Tests/Lr2SynchronizationArchitectureTests.cs`
- `BeMusicSeeker.Tests/MainWindowContextMenuResourceTests.cs`
- `BeMusicSeeker.Tests/PlaylistConcurrencyArchitectureTests.cs`
- `BeMusicSeeker.Tests/RegularChartListRefreshTypesTests.cs`
- `BeMusicSeeker.Tests/SourceTextTestHelper.cs`

## 5. 元のUnit 1〜10の進捗

| Unit | 目的 | 状態 | 主なcommit / 証拠 |
| --- | --- | --- | --- |
| 1 | 大容量fixtureを通常build/Functionalから分離 | 完了 | `adebdd54`。実fixtureを明示的opt-in laneへ移動。 |
| 2 | 完了済みbenchmark・migration characterizationを現行contractへ収束 | 完了 | `17bbb41c`, `8789218f`。distribution A/B、legacy audio/list/startup比較を退役。 |
| 3 | 固定待ちをdeterministic signalへ置換し、小規模owner cohortを並列化 | 完了 | `9707487c`, `86eec92f`, `37c510ae`, `73e384f7`, `cf3e2060`, `9e8a2a1c`, `6c64413c`。 |
| 4 | 大規模per-test-owned cohortをmethod-level化 | 完了 | `a0469810`, `ae2c2d69`。7-class pre-waveとnamed shardを最終再配分。 |
| 5 | 非interactive WPF window ownershipを共有hostへ集約 | 完了 | `36f1b68d`と後続修正。共有host、offscreen/non-activating、popup/window cleanupを導入。 |
| 6 | shell/settings/processのsource-text固定テストをbehavior/semantic ruleへ置換 | 一部完了 | `39b1c260`, `e27c325e`, `59aa73eb`など。残存7 consumerの整理が未完了。 |
| 7 | playlist/workspace/updateのsource-text固定テストを置換 | 完了 | `a557b879`, `3586b182`, `6b4eb302`。 |
| 8 | model/DB/private-member固定テストをowner routeへ移す | 完了 | `aacba8f7`, `18248f8d`, `5eb09876`, `4e936abb`, `5ca2c547`。必要最小限のreflectionは理由付きで残した。 |
| 9 | Functional runnerを最終fixture構成へ収束 | 完了 | `ae2c2d69`。Functional 3連続 152.5/155.3/158.1秒。 |
| 10 | Fullをcanonical Functional＋単一artifact snapshot＋bounded phaseへ統合 | 未着手 | runner/acceptance script/spec/testの実装と、この計画書の退役が残る。 |

## 6. 完了済み作業の要点

### 6.1 並列化、待機、WPF

- class/method DNPをresource単位で監査し、owner-local/GUID resourceのfixtureから不要な属性を削除した。
- 固定待ちやpollingを、folder/property notification、scheduler generation/revision、playlist summary/reload/detail receipt、workflow terminal/idle taskへ置換した。
- contention/deadlock testは、test-owned gateを `finally` で必ず解放し、開始済みtaskを5秒以内でdrainする形に統一した。
- WPFは共有 `TestUiDispatcherHost` がApplication/dispatcher/window/popupを所有する。非interactive windowはoffscreen/non-activatingで、foregroundを必要とする6 methodだけを明示的に残した。

### 6.2 Functional runner / Unit 9

- exclusive portable-settings laneの後、7-class `method-level-pre-wave` を他shardと並行させず先行実行する。
- pre-wave完了後に `remaining` と次の3 named shardを別testhostで並行起動する。
  - `settings-presentation-classwide`: 16 class、1 worker / `ClassLevel`
  - `process-global-lifecycle`: `BassNativeRuntimeTests`, `NLogWrapperTests`、1 worker / `ClassLevel`
  - `feature-process-global-state`: 16 class、1 worker / `ClassLevel`
- runnerはpre-wave/named shardのexact membership、worker数、scope、remainingとの重複排除を独立literal allowlistで起動前に検証する。
- timeout、cleanup reserve、DNPを延長せず、Functionalは次の3回連続で成功した。
  - `tests-functional-20260821-073049`: 152.5秒
  - `tests-functional-20260821-073332`: 155.3秒
  - `tests-functional-20260821-073613`: 158.1秒
- 3回ともfingerprintは `1D84874D3FA6848C088C34738B450D78ECF2CF0ACB037C1C36D01E1FB2CC8E87`、残留 `testhost` / `vstest` はなかった。

### 6.3 Unit 7: source-textからbehaviorへの置換

- playlist/workspace cohortは、durable→visible publication、visible publish中cancellation、lock-order、catalog version、reference index、BMSON projection、空BMT progressをpublic/typed routeで検証するようにした。5 fixture Quickは586/586成功 (`tests-quick-20260821-084934`)。
- updaterは、source上の配置ではなくrecording prepared-launchの `StartCount == 0` でprepare-without-startを検証する。owner-only start、failure cleanupは既存typed testsを維持した。commit `3586b182`。
- playlist property/bulk/settings pageは実WPF controlを使い、13項目のlocalized label、effective TwoWay binding、VM↔controlを検証する。Quick 11/11成功 (`tests-quick-20260821-091405`)。

### 6.4 Unit 8: private-reflection整理

- settings presentation: native closeを実 `Window.Close()`、schema/danger状態をtyped state portで検証し、構築失敗時もtemporary DBを回収する。commit `18248f8d`。
- settings dialog: LR2Configの実file load/save failure、typed snapshot/conflict/sync service、startup repairのinstall-dir不変をpublic/internal routeで検証する。commit `5eb09876`。
- pending install: stale retryでinstalled/missing partitionを再構築し、nested BMSON metadataをpublic destination routeで検証する。commit `aacba8f7`、Quick 48/48。
- playlist owner: external initialization、output migration/type、inline edit、remove、hydration repair、store replacement、recommended import lock再確認をpublic routeとdeterministic barrierで検証する。commit `4e936abb`、Bms 95/95、Workspace 146/146。
- catalog/LR2: setup notification drain、metadata cache、SHA-only digest、file-scan replacement、file-diff/catalog-write failure→LR2 durable statusをtyped/public routeで検証する。commit `5ca2c547`、Quick 208/208。
- 残したreflectionは、public routeでは分離不能なmode-write block、test-only state injection、presentation gateなどである。再整理する場合も、対応するobservable contractを先に用意する。

## 7. 未完了事項

### 7.1 Unit 6: 残存source-text consumer

最優先の未完了事項。再開時は必ずunit-plannerで、次の7 consumerを15ファイル未満のreviewable unitへ分割する。

- `MainWindowContextMenuResourceTests.cs`: 最大の残件。shell binding、context-menu、settings、startup/progress、library/modelが混在する。領域ごとに既存owner/WPF behavior testへ移し、一括削除しない。
- `PlaylistConcurrencyArchitectureTests.cs`: playlist以外のcomposition/settings/audio/dialog/source配置 assertionが残る。semanticなreflection-only contractとsource placementを区別する。
- `DialogRouteConsolidationTests.cs`: dialog confirmation/progress/file picker/modalをtyped dialog service/coordinator behaviorへ置換する。
- `BmsLibraryMutationBoundaryTests.cs`: catalog/package/folder/installation mutationをDB・receipt・failure resultで検証する。
- `Lr2PlayHistorySchemaUiTests.cs`: actual settings page/controlとschema stateで検証する。
- `Lr2SynchronizationArchitectureTests.cs`: typed synchronization owner/request/resultへ移す。
- `RegularChartListRefreshTypesTests.cs`: private method bodyの文字列ではなくrefresh request/type/resultを観測する。

各unitの受入条件:

- 対象fixtureから `SourceTextTestHelper` 参照がなくなる。
- 削除したmethodごとに、既存または新規behavior testへの対応表をreviewへ渡す。
- WPFを扱う場合は共有hostを使い、foreground/window allowlistを増やさない。
- fixed wait、unbounded await、failure時に解放されないgateを追加しない。
- 最後のconsumerが消えた時点で `SourceTextTestHelper.cs` を削除する。

### 7.2 Unit 8の残存reflection再監査

Unit 8は完了扱いだが、broad regexは対象8ファイルにまだヒットする。これは「すべて削除すべき」という意味ではない。Unit 6完了後の最終監査で次を確認する。

- production private member名・配置だけを固定するreflectionが残っていないか。
- serialization/metadata、WPF lifecycle、到達不能なfailure gateなど、残す理由が説明できるか。
- 残す場合は、test名または近接commentからresource/contractが分かるか。

### 7.3 Unit 10: Full runner

`scripts/verify-refactor.ps1`、`scripts/accept-net10-update.ps1`、関連acceptance tests/specを対象に再度unit-plannerを実行する。現在分かっている未完了契約は次のとおり。

- FullがFunctional相当を再構築せず、canonical Functional routeを一度だけ実行すること。
- publish/update acceptanceが同じimmutable distribution artifactを参照し、`dist` の更新時刻でlatest zipを選ばないこと。
- Full全体または各phaseに明示的なbudgetがあり、timeout時にactive phase/process/artifactを残すこと。
- `accept-net10-update.ps1` がFull中に再publishしないこと。
- `UpdaterPackageSyncTests` 等の非UI process起動に `CreateNoWindow = true` を設定し、必要なUI testだけを例外とすること。
- runnerのmembership、budget、artifact identity、失敗時cleanupをcompiled/script contract testで検証すること。
- runner/lane変更後はFunctionalを3回連続で実行し、その後Fullを実行すること。

### 7.4 最終統合検証と計画書退役

Unit 6と10の実装・review後、次の順で完了させる。

1. `SourceTextTestHelper`、DNP、fixed wait、private reflection、opt-in large fixtureの最終inventoryを採取する。
2. Unit 7・8後のintegrated Functionalを確認する。Unit 10でrunner/laneを変更する場合は、その最終snapshotで3回連続実行する。
3. `verify-refactor.ps1 -Mode Full` を実行し、単一artifact、budget、tracked fingerprint、残留processを確認する。
4. 継続運用に必要な契約だけを `devdocs/spec/testing-strategy.md` へ統合する。
5. 一時的なartifact名、途中の失敗、commit一覧、進捗表を仕様へコピーしない。
6. この `devdocs/plan/test-suite-cleanup-plan.md` を削除して最終commitする。

## 8. 再開時の最初の手順

1. `git status --short` とHEADを確認し、この文書以外に差分がないことを確認する。
2. Unit 6残件についてunit-plannerを実行する。前回plannerは利用上限で失敗しており、計画結果は存在しない。
3. 最初のbounded unitは、consumer 1〜3ファイルに限定し、Quick→Functional→static review→commitの順で閉じる。
4. Unit 6完了後にUnit 10を新しいplannerで設計する。Unit 6とUnit 10を同じsnapshotへ混在させない。

## 9. この文書の退役条件

次をすべて満たしたときだけ、この文書を削除する。

- 7 consumerから `SourceTextTestHelper` 参照が消え、helper本体も削除済み。
- 残存DNP/fixed wait/private reflectionに所有resourceまたはfailure contractの根拠がある。
- Unit 10のcanonical Functional、immutable artifact、bounded Fullが実装・review済み。
- 必要な運用契約が `devdocs/spec/testing-strategy.md` に一度だけ記載されている。
- 最終Functional/Fullが成功し、tracked file不変、残留test processなしを確認している。
