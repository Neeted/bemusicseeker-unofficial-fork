# Codex 共通実行ルール

[現在地](./PLAN_STATUS.md) / [terminal refactoring](./BeMusicSeekerリファクタリング計画.md) / [.NET 10 migration](./BeMusicSeeker_NET10移行計画.md)

## 1. Start / resume

1. worktree、HEAD、untrackedを確認する。unrelatedな既存差分へ触れない。
2. `PLAN_STATUS.md`のactive outcome、execution anchor、active implementation batchを読む。
3. batchに`active`／`pending`があればplannerを起動せず、最初の未完unitを実装する。
4. batchが空のときだけunit-plannerをsingle-flightで一度起動する。
5. planner結果を`PLAN_STATUS.md`へmaterializeし、status-only commitにせず最初のcode unitへ含める。

commit、review完了、context切替、unit先頭はplanner再起動理由ではない。具体的なproduction evidenceがbatch前提を無効にした場合だけ、その差異に限定して再計画する。

## 2. Single-flight

planner／reviewer起動から結果受領まで、rootはrepositoryの読み取り、Git、検索、編集、build、test、format、analyzer、stage、commitを凍結する。rootによる独立再調査、第2planner、同scope consensusを行わない。

planner後に許されるのは、指定symbol／route／testのbounded feasibility checkと実装だけである。

## 3. Implementation unit

unitはproduction route、behavior evidence、旧surface削除を一緒に閉じる。

- 同じowner、user-visible workflow、persisted data、failure contract、test fixtureを共有するrouteをまとめる。
- method、callback、property、binding、package、DLL一件だけをunitにしない。
- interface／adapter／DTO追加、rename、file move、行数削減だけで完了にしない。
- temporary compatibility seamは同じunitまたは明示された直後unitで退役し、owner不明のまま残さない。
- facadeを保持してprivate operationをforwardするport／hostを、別名または多数の小interfaceへ移しただけにしない。

`NO_SAFE_UNIT`は無効。内部複雑性はprepare、durable write、live apply、receipt publish、consumer apply、legacy retirementのcorridorで分解する。

## 4. Compatibility

次を変更するunitは、before／after evidenceとrollbackまたはmigrationを持つ。

- setting key、type、default、serialized value、save timing
- DB schema、existing rows、DateTime／enum／null mapping、transaction／lock ordering
- chart、playlist、package、LR2、external document format
- UI observable behavior、selection／focus、failure／cancel timing
- updater manifest、folder layout、restart／rollback
- supported external player、Everything、native ABI、CLI／IPC／COM contract

public修飾子だけをexternal contractとみなさない。out-of-repository consumerを具体化できない内部APIは、同じunitで全consumerを更新する。

## 5. .NET 10 migration rules

- app、tests、updater、2 toolsを対象から漏らさない。
- dependency replacementは[依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md)のcorridor単位で行い、複数の未知なmajor upgradeを混ぜない。
- HintPath削除と置換route／test／publish assetを同じunitで閉じる。
- native DLLはversion、source、license、architecture、copy owner、runtime load testを持つ。
- `RuntimeIdentifier`だけでSelf-containedと判断せず、publish profileまたはcommandで明示する。
- main appは初期Gateまで`PublishTrimmed=false`、`PublishSingleFile=false`、`PublishReadyToRun=false`とする。
- build outputを配布候補や最終smoke対象にしない。Self-contained publish folderから起動する。
- legacy `app.config` probingやcurrent directoryの偶然に依存してmanaged／native DLLを解決しない。
- clean-machine、existing-data、old-to-new updater acceptanceを最終Gateから省略しない。

## 6. Verification

### Planning / agent docs only

- UTF-8、LF、末尾改行、TOML／Markdown構文、相対link、table、whitespace、`git diff --check`
- production／test／build／resource差分がなければbuild、test、code reviewは不要

### Terminal refactoring

- unit中はrepository標準Quick verification
- shared owner、concurrency、playback contract、outcome closureはFull verification
- Release executableによる変更範囲のUI smoke
- frozen snapshotのfresh read-only review

### .NET 10 migration

- affected projectのrestore／Release build／targeted test
- dependency corridorのbehavior／golden test
- app、tests、updater、2 toolsのoutcome-level build
- publish／layout変更時はwin-x64 Self-contained publish folderからruntime smoke
- final Gateはclean checkout、existing-data、clean-machine、update／rollback acceptance

失敗test、timeout、全体実行時だけのfailureをbaseline／flakyとして放置しない。tool不足でmanual gateだけ実行不能な場合、automated scopeを先に完了し、具体的な`EXTERNAL_BLOCKER`として記録する。

## 7. Review

reviewerはfrozen snapshotをread-onlyで確認する。重大指摘修正後は新しいsnapshotとしてfresh reviewする。review findingを細かいroute IDへ増殖させず、active unit内で修正する。

line count、event count、interface method countだけをfindingにしない。構造的なowner違反、facade forwarding、observability欠如、data／runtime compatibility欠如をevidenceで判定する。

## 8. Commit / status

- rootだけがwrite、stage、commitする。
- unit commitにoutcome IDを含める。
- active batchの該当行を`completed`、次行を`active`へ更新し、同じcode commitへ含める。
- `PLAN_STATUS.md`に過去のcommit、unit、review log、長いverification logを追記しない。
- outcome未完ならunit commitをユーザー応答境界にしない。

## 9. External blocker / Release Freeze

停止できるのは次だけである。

- persisted data、supported external contract、UI／failure semanticsの相互排他的な選択が必要
- credential、署名鍵、external asset、clean Windows machine、利用不能な必須toolが必要
- 正本に未決で後戻り困難なdeployment／architecture選択が必要

それ以外は現行batchを継続する。`git push`、tag、release、public publish、version／release notes変更、署名、配布物公開はユーザーの明示指示まで行わない。
