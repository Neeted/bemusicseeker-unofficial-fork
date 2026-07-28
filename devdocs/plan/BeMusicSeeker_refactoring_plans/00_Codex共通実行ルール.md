# Codex 共通実行ルール

[現在地](./PLAN_STATUS.md) / [リファクタリング完了記録](./BeMusicSeekerリファクタリング計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## 1. Start / resume

1. worktree、HEAD、untrackedを確認し、unrelatedな差分へ触れない。
2. `PLAN_STATUS.md`のactive outcome、execution anchor、active implementation batchを読む。
3. batchに`active`／`pending`があればplannerを起動せず、最初の未完unitを実装する。
4. batchが空のときだけunit-plannerをsingle-flightで一度起動する。
5. planner結果は最初のcode unitと同じworktreeで`PLAN_STATUS.md`へmaterializeし、status-only progress commitを作らない。

commit、review完了、context切替、unit先頭はplanner再起動理由ではない。具体的なproduction evidenceがbatch前提を無効にした場合だけ、その差異に限定して再計画する。

## 2. Single-flight

planner／reviewer実行中、rootはrepositoryの読み取り、Git、検索、編集、build、test、format、analyzer、stage、commitを凍結する。rootによる同scopeの独立再調査、第2planner、consensus取得を行わない。

planner後に許されるのは、指定symbol／route／testのbounded feasibility checkと実装だけである。

## 3. Implementation unit

unitはproduction route、compatibility evidence、旧surface削除を一緒に閉じる。

- 同じowner、user-visible workflow、persisted data、failure contract、test fixtureを共有するrouteをまとめる。
- method、callback、property、binding、package、DLL一件だけをunitにしない。
- interface／adapter／DTO追加、rename、file move、行数削減だけで完了にしない。
- temporary seamは同じunitまたは明示された直後unitで退役する。
- facadeを保持してprivate operationをforwardするport／hostを別名へ移しただけにしない。

`NO_SAFE_UNIT`は無効。内部複雑性はowner／transaction／publish／compatibility corridorへ分解する。

## 4. Compatibility

次を変更するunitはbefore／after evidenceとrollbackまたはmigrationを持つ。

- setting key、type、default、serialized value、save timing
- DB schema、existing rows、DateTime／enum／null mapping、transaction／lock ordering
- chart、playlist、package、LR2、external document format
- UI observable behavior、selection／focus、failure／cancel timing
- updater manifest、folder layout、restart／rollback
- external player、Everything、native ABI、CLI／IPC／COM contract

## 5. .NET 10 engineering

- app、tests、updater、2 toolsを対象から漏らさない。
- dependency replacementは[依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md)のcorridor単位で行う。
- HintPath削除と置換route／test／publish assetを同じunitで閉じる。
- native DLLはversion、source、license、architecture、copy owner、runtime load testを持つ。
- `RuntimeIdentifier`だけでSelf-containedと判断せず、publish profileで明示する。
- build outputではなくSelf-contained publish outputを配布候補とする。
- Self-containedはmachine-installed runtimeのservicingへ自動追随しないため、Engineering Gate直前に公式の最新.NET 10 servicing SDKへ更新し、再publishする。

### Distribution layout

- folder Self-containedではmanaged dependencyとruntime fileがexe隣接になる。これは標準host layoutであり、数だけを理由に失敗扱いしない。
- managed DLLを`libs`へ移すための独自`AssemblyLoadContext`／`AssemblyResolve`、private probing、deps.json書換え、post-publish relocation、wrapper launcherを追加しない。
- 配布物の簡素化は公式single-file publishだけを一度、有限に評価する。
- single-file候補はwin-x64 Self-contained、untrimmed、ReadyToRun無効とし、必要なら公式の`IncludeNativeLibrariesForSelfExtract`を使う。
- executable／application base pathは`Environment.ProcessPath`／`AppContext.BaseDirectory`で表し、`Assembly.Location`へ依存しない。
- standard single-fileとboundedなpath修正だけで全自動受入れを通せなければ`NOT_ADOPTED`としてprobeを退役し、folder Self-containedを最終構成にする。これは正常完了である。

## 6. Verification

### Planning / agent docs only

UTF-8、LF、末尾改行、TOML／Markdown構文、相対link、table、whitespace、`git diff --check`を確認する。production／test／build／resource差分がなければbuild、test、code reviewは不要。

### Engineering unit

- affected projectのlocked restore／Release build／targeted test
- dependency／layout corridorのbehavior／golden test
- publish変更時は実際のwin-x64 Self-contained outputからruntime smoke
- frozen snapshotのfresh read-only review

### Final Engineering Gate

- clean checkout相当のlocked restore
- app、tests、updater、2 toolsのRelease buildとfull tests
- analyzer／format gate
- selected main-app profileとupdater profileのSelf-contained publish
- package／layout validator、publish-folder startup
- automated existing-data acceptance
- automated old-to-new update／rollback acceptance
- fresh outcome review

## 7. Post-engineering manual acceptance

`.NET Desktop Runtime`未導入clean machine／VM、署名、公開、BASS.NET licensee／registration証跡は[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)へhandoffする。

これらはCodexのactive outcome、Engineering Gate、planner停止条件、`EXTERNAL_BLOCKER`にしない。自動工程が完了したら`engineering migration: complete`として作業を閉じる。

`EXTERNAL_BLOCKER`にできるのは、外部入力がなければコードまたは自動検証の選択／実行自体が不可能で、安全なdefaultが正本にない場合だけである。

## 8. Review / commit / freeze

- rootだけがwrite、stage、commitする。
- active batchの状態遷移を対応code commitへ含める。
- `PLAN_STATUS.md`に過去のcommit、unit、review logを追記しない。
- outcome未完ならunit commitをユーザー応答境界にしない。
- `git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで行わない。
