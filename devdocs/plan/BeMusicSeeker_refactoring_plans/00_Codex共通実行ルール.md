# Codex 共通実行ルール

[現在地](./PLAN_STATUS.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md)

## 1. 正本と現在地

1. `PLAN_STATUS.md`を読む。
2. active outcomeに対応する計画と台帳を読む。
3. active implementation batchに未完unitがあればplannerを起動せず、記載順に進める。

`PLAN_STATUS.md`には現在の判定、active batch、現行evidenceだけを置く。過去のunit、commit、review logはGit historyへ委ねる。

## 2. Orchestration

- planner／reviewerは同時に一つだけ起動する。
- サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、format、analyzer、stage、commitを凍結する。
- rootは同scopeを独立再調査しない。planner後は指定symbol、route、testのbounded feasibility checkだけを行う。
- active batchが空、batch完了後もanchor未達、または具体的evidenceでbatch前提が崩れた場合だけplannerを起動する。
- `NO_SAFE_UNIT`は無効。内部複雑性はowner、transaction、compatibility、publish、performance corridorへ分解する。

## 3. Unit

一つのunitで次を閉じる。

- production routeとowner boundary
- behavior／compatibility evidence
- 旧route、fallback、adapter、test seamの退役
- targeted verification
- frozen snapshotのfresh review
- 重大指摘修正後の再検証
- 対応する`PLAN_STATUS.md`の状態遷移

method、callback、property、package、DLL、candidate profile一件だけをunitにしない。同じowner、invariant、fixture、verification scopeを持つ変更をまとめる。status-only progress commitを作らない。

## 4. Architecture regression

- feature state、domain decision、durable write、retry／fallback、複数serviceの順序制御をroot View／facadeへ戻さない。
- facade private state、lock、mutable collection、private operationを列挙するbroad host／portを作らない。
- non-event `async void`、unobserved Task、通常経路の意味を変えるfallbackを作らない。
- WPF terminal mappingを理由なくserviceへ隠さず、platform dependencyをfeature ownerへ漏らさない。
- setting、DB、file／update contractをmigration evidenceなしに変えない。

行数、file数、type数は調査signalにだけ使い、合否値にしない。

## 5. Performance-first distribution

- main appの最終配布profileは、公式SDK機構だけを使った再現可能なbenchmarkで決める。
- file数、exe一個、zipの見た目はsecondary metricであり、startup、working set、runtime failureより優先しない。
- end-to-end startupはharness側のStopwatchでprocess startからmain-window ready／`startup_ready_operable`検出まで測る。production log内の`elapsedMs`はphase内訳にだけ使う。
- self-extract profileは専用`DOTNET_BUNDLE_EXTRACT_BASE_DIR`を使い、fresh install／cache missとwarm cacheを分ける。
- candidateは同じHEAD、SDK、fixture、settings、native asset、acceptance routeで比較し、実行順を交互またはrotationする。
- compression、trimming、Composite ReadyToRun、NativeAOTを性能推測だけで有効化しない。ReadyToRunも実測で採否を決める。
- standard folder hostのmanaged／runtime fileはexe隣接を許可する。application-owned native／contentは`libs/x64`、`native`、`lang`等へ整理する。
- managed DLLを見た目のために`libs`へ移す独自loader、probing、deps rewrite、post-publish relocation、wrapper launcherを追加しない。
- updaterはtransaction handoffの単一payloadとしてsingle-fileを維持し、main appのprofile変更と不必要に連動させない。

## 6. Compatibility

次を変更するunitはbefore／after evidenceとrollbackまたはmigrationを持つ。

- setting key、type、default、serialized value、save timing
- DB schema、existing rows、DateTime／enum／null mapping、transaction／lock ordering
- chart、playlist、package、LR2、external document format
- UI observable behavior、selection／focus、failure／cancel timing
- updater manifest、folder layout、managed-file manifest、restart／rollback
- external player、Everything、native ABI、CLI／IPC／COM contract

## 7. Verification

### Planning／agent docs only

UTF-8、LF、末尾改行、TOML／Markdown構文、相対link、table、whitespace、`git diff --check`を確認する。production／test／build／resource差分がなければbuild、test、code reviewは不要。

### Engineering unit

- affected projectのlocked restore／Release build／targeted test
- behavior／golden／layout／performance evidence
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
- `devdocs/acceptance/net10-distribution-performance.md`のcurrent report、raw report hash、再現command
- fresh outcome review

## 8. 手動受入れとRelease Freeze

`.NET Desktop Runtime`未導入clean machine／VM、署名、公開、BASS.NET licensee／registration証跡は[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)へhandoffする。これらをCodexのactive outcome、Engineering Gate、planner停止条件、`EXTERNAL_BLOCKER`にしない。

rootだけがwrite、stage、commitする。`git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで行わない。
