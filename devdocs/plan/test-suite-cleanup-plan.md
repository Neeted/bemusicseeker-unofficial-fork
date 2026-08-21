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
- 最初の単発 timeout は process tree と diagnostics を保存し、残留 process がないことを確認して、同一 command・filter・budget で一度だけ再実行する。2回目が成功し再発 evidence がなければ一過性のマシン負荷として記録し、直ちに調査 unit へ切り替えない。
- 2回目も timeout / failure、同じ症状の再発、または artifact 上の決定的 evidence がある場合は本筋を止めて調査する。timeout 延長、無制限の再試行、並列度低下だけによる隠蔽は行わない。

## 3. 現在の停止地点

- この監査の基準HEAD: `7ce680ae` (`docs: refresh test suite cleanup plan`)
- 直前のproduction/test実装commit: `5ca2c547` (`test: retire catalog and lr2 reflection probes`)
- bundleに含まれる基準worktree差分: なし
- 最後に完了した実装: Unit 8のcatalog/LR2 private-reflection整理
- 次に再開する作業: Unit 6の残存shell/settings source-text整理
- 前回のUnit 6専用plannerは利用上限で失敗し、計画結果もrepository編集も残していない。専用planner前提は廃止し、ルートが計画を統括して Luna Low `plan-clarifier` の点検を一度受ける運用へ移行する。
- Codex運用見直しの変更はagent設定・運用文書・本計画の監査だけであり、Unit 6のproduction/test実装そのものはまだ再開していない。

## 4. 基準値と現在値

| 指標 | 整理開始時 | 現在 | 注記 |
| --- | ---: | ---: | --- |
| `DoNotParallelize` | 76属性 / 47ファイル | 47属性 / 26ファイル | 現値は単純な属性行数。残存属性には任意Quick filterで必要なsafety boundaryを含む。 |
| `SourceTextTestHelper` | 417参照 / 20ファイル | 275 textual reference / 8ファイル | 現在は7 consumerの274 call siteとhelper本体。 |
| source-text依存test method | 未採取 | 118 method | 7 consumerに残るmethod数。fixture単位ではなくbehavior sliceで退役する。 |
| Unit 8対象8ファイルのprivate-reflection | planner-core 286参照 | 大幅削減 | broad regexには正当なmetadata/reflectionや1契約内の複数API呼出しも含まれるため、単純なゼロ目標にはしない。 |
| Functional | 3,962 total / 3,951 pass / 11 opt-in skip、約143/134/134秒 | Unit 9受入時 3,970 terminal / 3,959 pass / 11 not executed、152.5/155.3/158.1秒 | Unit 7・8後の統合Functionalは未実施。 |

現在のconsumer別inventoryは次のとおりである。

| consumer | source-text依存method | helper call site | 主な領域 |
| --- | ---: | ---: | --- |
| `MainWindowContextMenuResourceTests.cs` | 82 | 194 | shell binding、navigation、context menu、settings、startup/progress、playlist/play history、library/package |
| `PlaylistConcurrencyArchitectureTests.cs` | 23 | 49 | settings lifecycle、composition、audio/dialog port、view host |
| `DialogRouteConsolidationTests.cs` | 8 | 16 | confirmation、progress、file picker、modal / overlay route |
| `BmsLibraryMutationBoundaryTests.cs` | 2 | 6 | package/folder/library mutation ownership |
| `Lr2PlayHistorySchemaUiTests.cs` | 1 | 4 | settings UI と schema install boundary |
| `Lr2SynchronizationArchitectureTests.cs` | 1 | 2 | library synchronization composition |
| `RegularChartListRefreshTypesTests.cs` | 1 | 3 | typed refresh request / result route |
| **合計** | **118** | **274** | helper本体を含めると8ファイル・275 textual reference |

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

最優先の未完了事項である。ルートは各waveのdraft planを作り、実装へ渡す前に Luna Low `plan-clarifier` へ一度だけ点検させる。最終計画では、削除対象method、replacement test、writable path、Quick filter、依存順を固定する。

共通受入条件:

- 対象sliceの `SourceTextTestHelper` 呼出しがなくなる。
- 削除したmethodごとに、既存または新規behavior testへの対応表をworker handoffとreview scopeへ含める。
- source placementの違反を検出する必要が本当に残る場合は、compiled symbol / semantic ruleで閉じられるかを先に検討し、substring assertionを名前だけ変えて残さない。
- WPFを扱う場合は共有hostを使い、foreground/window allowlistを増やさない。
- fixed wait、unbounded await、failure時に解放されないgateを追加しない。
- production変更は、既存observable routeでは契約を検証できないことが確認できた場合だけ行い、test都合だけのpublic APIを増やさない。
- 各workerはfocused Quickを行う。並列workerを統合したwaveごとに、ルートが一度だけFunctionalとstatic reviewを行い、worker別に同じ全体検証・reviewを重複させない。
- 最後のconsumerが消えた時点で `SourceTextTestHelper.cs` を削除する。

#### Wave 6A: typed owner route の最小再開単位

最大2workerで並列化できる最初の候補である。互いのreplacement先が重ならないことをfinal planで確認してから開始する。

- **6A-1** `Lr2SynchronizationArchitectureTests.cs` の1 method / 2 call site
  - `LibraryFileOperationOwnerUsesDirectCapabilityComposition`
  - library file operation owner、mutation boundary、typed request / resultのobservable routeへ置換する。
- **6A-2** `RegularChartListRefreshTypesTests.cs` の1 method / 3 call site
  - `RefreshChartRowsView_DispatchesRegularProductionEntry`
  - private method bodyではなくregular refresh request、dispatch、result / notificationを観測する。

workerは別々のconsumer fileとreplacement fixtureを所有し、同じproduction fileへの変更が必要になった場合は並列編集を止めてルートへ返す。統合後に両fixtureからhelper参照が消えたこと、Quick対応表、Functional、reviewを一つのwaveとして閉じる。

#### Wave 6B: 小規模なmutation / settings UI

6Aの運用とhandoffが成立した後に進める。次の2件はdomainが分かれるため並列候補だが、replacement fixtureの所有pathをfinal planで固定する。

- **6B-1** `BmsLibraryMutationBoundaryTests.cs` の2 method / 6 call site
  - package、folder auto rename、pending package、shell context-menu stateを、mutation receipt / result、DB/file結果、busy stateで検証する。
- **6B-2** `Lr2PlayHistorySchemaUiTests.cs` の1 method / 4 call site
  - actual settings page/control、schema state、明示的install boundaryのcompiled behaviorへ置換する。

#### Wave 6C: dialog route

`DialogRouteConsolidationTests.cs` の8 method / 16 call siteを1workerで処理する。confirmation、progress、file picker、window modal、overlay、dispatcher message box禁止、score viewer registrationを、typed coordinator/serviceのrecording fakeと実WPF hostの適切な組合せへ移す。settings presentationのdestination fixtureと重なりやすいため、6B-2や後続MainWindow settings sliceとは同時編集しない。

#### Wave 6D: PlaylistConcurrencyArchitectureTests

23 method / 49 call siteを同一fileで扱うため、複数workerの同時書込みは禁止する。1workerずつ次の2sliceで閉じる。

1. **6D-1 settings / composition lifecycle**
   - `CustomFolderOutputResolution_UsesDedicatedProviderBoundary` から `StartupPlayerServices_AreConstructedByApplicationComposition` までの15 method。
   - custom folder、settings edit / persistence、startup / shutdown、settings store、main table / library / player service compositionを、public owner/store/composition behaviorへ移す。
2. **6D-2 technology-neutral ports / view host**
   - `PlaybackPlayersUseThePlayerSettingsGatewayBoundary` から `MainWindowRuntimeSettings_UseCompositionEditSession` までの8 method。
   - player gateway、audio adapter isolation、dialog contracts、pending-delete port、view host store、child owner composition、runtime edit sessionをtyped contract testへ移す。

両sliceは `ApplicationComposition.cs`、`MainWindowViewModel.cs`、settings関連fixtureへ到達しやすいため逐次実行し、各slice開始前に前sliceのcurrent diffを前提としてfinal planを更新する。

#### Wave 6E: MainWindowContextMenuResourceTests

82 method / 194 call siteを含む最大の残件であり、一括削除も同一fileへの並列書込みも行わない。1workerが同時にこのfileを所有し、ルートが各sliceの正確なmethod一覧をfinal planへ固定する。候補sliceは次の5領域である。

1. tree / navigation / regular chart / table projection
2. playlist / play history / workspace
3. settings / dialog / startup / progress
4. package / library / maintenance / cache invalidation
5. selected-chart context menu / external action / shell WPF terminal behavior

source file内の順序は領域と一致しないため、各methodを一度だけいずれかのsliceへ割り当てるinventoryを作る。replacementは既存のfeature owner、ViewModel、compiled WPF behavior fixtureを優先し、`MainWindowContextMenuResourceTests.cs` 自体を新しい巨大catch-all fixtureとして延命しない。`PlaylistConcurrencyArchitectureTests.cs` や `DialogRouteConsolidationTests.cs` と同じdestination fixtureへ到達するsliceは、それらのwave完了後に逐次実行する。

#### Wave 6F: helper削除と最終監査

- 7 consumerすべてのhelper参照が0であることを確認し、`SourceTextTestHelper.cs` を削除する。
- source substring、private member placement、method body extractionの残存をinventoryし、behavior / compiled semantic ruleへのreplacementまたは理由付きの残置を確認する。
- Unit 6全体のremoved method -> replacement test対応表を統合し、Functionalとstatic reviewを行う。

### 7.2 Unit 8の残存reflection再監査

Unit 8は完了扱いだが、broad regexは対象8ファイルにまだヒットする。これは「すべて削除すべき」という意味ではない。Unit 6完了後の最終監査で次を確認する。

- production private member名・配置だけを固定するreflectionが残っていないか。
- serialization/metadata、WPF lifecycle、到達不能なfailure gateなど、残す理由が説明できるか。
- 残す場合は、test名または近接commentからresource/contractが分かるか。

### 7.3 Unit 10: Full runner

Unit 6完了後、ルートがUnit 10のdraft planを作り、Luna Low `plan-clarifier` へ一度点検させてから Luna Max workerへ渡す。`scripts/verify-refactor.ps1` とacceptance scriptsが同じartifact / phase contractを共有するため、write-heavyな実装は原則1workerで逐次進める。read-onlyなinventoryだけは必要なら別範囲で並列化してよい。

候補slice:

1. **10A canonical Functional / budget**
   - FullがFunctional相当を再構築せず、canonical Functional routeを一度だけ実行する。
   - Full全体または各phaseに明示的なbudgetがあり、timeout時にactive phase/process/artifactを残す。
2. **10B immutable distribution artifact**
   - publish/update acceptanceが同じimmutable artifact identity / pathを受け取り、`dist` の更新時刻でlatest zipを選ばない。
   - `accept-net10-update.ps1` がFull中に再publishしない。
3. **10C acceptance process / cleanup / contracts**
   - `UpdaterPackageSyncTests` 等の非UI process起動に `CreateNoWindow = true` を設定し、必要なUI testだけを例外とする。
   - runner membership、budget、artifact identity、失敗時cleanupをcompiled/script contract testで検証する。
   - runner/lane変更後はFunctionalを3回連続で実行し、その後Fullを実行する。

### 7.4 最終統合検証と計画書退役

Unit 6と10の実装・review後、次の順で完了させる。

1. `SourceTextTestHelper`、DNP、fixed wait、private reflection、opt-in large fixtureの最終inventoryを採取する。
2. Unit 7・8後のintegrated Functionalを確認する。Unit 10でrunner/laneを変更する場合は、その最終snapshotで3回連続実行する。
3. `verify-refactor.ps1 -Mode Full` を実行し、単一artifact、budget、tracked fingerprint、残留processを確認する。
4. 継続運用に必要な契約だけを `devdocs/spec/testing-strategy.md` へ統合する。
5. 一時的なartifact名、途中の失敗、commit一覧、進捗表を仕様へコピーしない。
6. この `devdocs/plan/test-suite-cleanup-plan.md` を削除して最終commitする。

## 8. 再開時の最初の手順

1. `git status --short` とHEADを確認し、想定外の差分がないことを確認する。基準commitは `7ce680ae` であり、Codex運用見直しpatchを適用した場合はその差分を既知の前提として扱う。
2. `SourceTextTestHelper` の7 consumer / 118 method / 274 call siteと、Wave 6Aの2 methodがまだ残っていることを短いinventoryで確認する。数が変わっていれば、その差分だけをplanへ反映する。
3. ルートがWave 6A-1 / 6A-2について、replacement test、writable path、Quick filter、統合受入条件を含むdraft planを作る。
4. Luna Low `plan-clarifier` を一度呼び、repoで解けた事実と並列衝突を反映する。observable semanticsの未決事項がある場合だけユーザー回答を得てfinal planを完成させる。
5. path ownershipが分離できた場合は2つの Luna Max `implementation-worker` を並列起動する。分離できなければ1workerずつ実行する。workerはfocused Quickだけを行い、同じFunctionalやreviewを重複実行しない。
6. ルートがhandoffとdiffを統合し、Wave 6AのQuick補完とFunctionalを一度行う。最初の単発 timeout は同一条件で一度だけ再実行し、2回目が成功すれば一過性負荷として記録する。2回目も失敗した場合だけ調査へ切り替える。
7. 実装threadを閉じ、凍結snapshotを `repo-static-review` へ渡す。review中にルートは重複チェックを行わない。
8. Wave 6Aを閉じた後、6B、6C、6D、6E、6Fの順に同じ境界で進める。Unit 6完了後にUnit 10を新しく計画し、Unit 6とUnit 10のwrite-heavy変更を同じsnapshotへ混在させない。

## 9. この文書の退役条件

次をすべて満たしたときだけ、この文書を削除する。

- 7 consumerから `SourceTextTestHelper` 参照が消え、helper本体も削除済み。
- 残存DNP/fixed wait/private reflectionに所有resourceまたはfailure contractの根拠がある。
- Unit 10のcanonical Functional、immutable artifact、bounded Fullが実装・review済み。
- 必要な運用契約が `devdocs/spec/testing-strategy.md` に一度だけ記載されている。
- 最終Functional/Fullが成功し、tracked file不変、残留test processなしを確認している。
