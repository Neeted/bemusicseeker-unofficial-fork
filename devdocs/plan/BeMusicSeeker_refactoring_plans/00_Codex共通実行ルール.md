# Codex 共通実行ルール

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md)に基づいて、Codex が自走して実装・検証・静的レビュー・commit を繰り返すためのルールである。

## 権限と禁止事項

- outcome 内の implementation unit は、検証とサブエージェント静的レビュー後に自発的に commit してよい。
- unrelated な既存差分を変更、stage、commit しない。
- Refactoring Completion Gate 前は、ユーザーから依頼されても `git push`、tag、release、publish、version 更新を行わない。Gate 後もユーザーの明示指示なしには行わない。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約、明示的にサポートする SDK / plugin / CLI / IPC / COM / automation contract を変更する必要が生じたら、実装前にユーザーへ確認する。
- 意味の変わる fallback を追加しない。失敗を隠すより、既存の失敗契約を維持して明示的に失敗させる。
- C# symbol rename は text replacement ではなく semantic rename / compiler-driven edit を使う。

## 実行ロール

- **planner**: `unit-planner`（Sol High、read-only、approval never）。active outcome の通常実装 branch に対して exactly one vertical implementation unit を計画する。plan rebaseline audit と `GATE-01` の audit-only branch では、監査で修正対象が見つかるまで unit を計画しない。出力は Codex task 内だけに保持し、per-unit plan 文書、checkpoint、progress log を repository に作らない。
- **implementation root**: Luna Max。唯一の writer / stager / committer とし、planner の提案を実ソースに照らして実装・検証する。サブエージェントへ書き込みを委譲しない。
- **reviewer**: fresh `repo-static-review`（Sol High、read-only、approval never）。実装担当から独立して frozen snapshot を評価し、`/fork` は使用しない。build / test / format / analyzer は root が担当する。

モデルを利用できない場合に別モデルへ黙って置き換えない。作業を開始せず、利用不能なロールと理由を報告する。

## Review scope

- **unit review**: unit 開始時の clean commit を `base`、review対象 snapshot の `HEAD` を `head` とし、scope は `<unit-base>..HEAD + frozen worktree` とする。untracked file を含め、同じ unit の production route、tests、削除対象を評価する。
- **outcome review**: `PLAN_STATUS.md` の `active outcome base commit` を `base`、review対象 snapshot の `HEAD` を `head` とし、scope は `<outcome-base>..HEAD + frozen worktree` と現行 production code とする。active outcome の全 acceptance criteria と残る旧 route を評価する。
- **gate review**: `PLAN_STATUS.md` の `code baseline commit` を `base`、review対象 snapshot の `HEAD` を `head` とし、scope は `<code-baseline>..HEAD + frozen worktree`、現行コード、全 outcome state、Gate criteria、migration blocker register とする。

review依頼には review kind、absolute repo path、scope、base/head SHA、untracked file一覧を必ず含める。review開始後は root も frozen snapshot を変更しない。修正後は別 snapshot として fresh reviewを依頼する。

## Outcome state 遷移

- `not started` → `ready`: 先行 outcome と依存条件が満たされ、次に着手できるときだけ遷移する。
- `ready` → `in progress`: active outcome として選択し、最初の implementation unit を開始するときに遷移する。同時に開始時の clean commit を `active outcome base commit` として記録する。
- `GATE-01 ready` → `GATE-01 in progress`: implementation unit ではなく、gate review scope の snapshot を凍結して Full verification を開始するときに遷移する。gate review は `code baseline commit` を base にするため、新しい `active outcome base commit` は記録しない。
- `ready` → `blocked`: planner が最初の unit を開始する前に、ユーザー入力または外部状態変更なしには解消できない具体的な阻害条件を確認し、`NO_SAFE_UNIT` とした場合だけ遷移する。
- `in progress` → `blocked`: 同じ外部阻害条件が繰り返し確認され、安全な unit を作れず、ユーザー入力または外部状態変更なしに進めない場合だけ遷移する。難しい、調査が必要、変更量が大きいことは理由にしない。
- `blocked` → `ready`: 最初の unit 開始前に blocked となり、`active outcome base commit` をまだ記録していない outcome の阻害条件が解消したときに遷移する。その後の `ready` → `in progress` で base を記録する。
- `blocked` → `in progress`: unit 開始後に blocked となり、既に `active outcome base commit` がある outcome の阻害条件が解消したときに限り、同じ base を保持して遷移する。
- `in progress` → `not started`: internal owner dependency により安全な vertical unit がなく、ユーザー承認済みの backlog / ownership 再編で未完了 criteria を named outcome へ移す場合だけ使う。変更前の outcome base から frozen snapshot までを re-plan review し、移動先、依存順、必要な bridge baseline と retirement outcome を正本へ記録してから、prerequisite を唯一の active outcome にする。criteria の破棄や通常の延期には使わず、元 outcome を再開するときはその時点の clean commit を新しい base とする。
- `in progress` → `completed`: acceptance criteria、outcome-wide Full verification、outcome review、UI smoke check（該当時）が完了したときだけ遷移する。
- `completed` → `in progress`: `GATE-01` の監査で、その outcome の acceptance criteria が現行 production code で満たされていないことが判明した場合だけ使う。`GATE-01` を `not started` に戻し、修正開始時の clean commit を新しい active outcome base として記録する。
- `GATE-01 in progress` → `GATE-01 not started`: Gate の Full verification、UI smoke check、または gate review で未達 criteria が見つかり、指摘が属する completed outcome を再開するときだけ遷移する。
- `GATE-01 in progress` → `gate met`: 全 Gate criteria、Full verification、該当する UI smoke check、gate review が完了したときだけ遷移する。`gate met` は GATE-01 以外に使わない。

通常 outcome の `completed` への状態更新は、最後の production code implementation unit と同じ commit に含める。最後のコード変更を先に commit し、後から docs-only completion commit を作ることを禁止する。完了条件がコード変更なしで初めて満たされたように見える場合は、完了監査が遅れていないか再確認し、安全な最後の vertical unit と一緒に閉じる。

ただし、ユーザー承認済みの計画再編により `in progress` outcome を一貫した責務境界へ縮小し、移動した責務、後続 outcome、bridge retirement outcome が正本に明記され、縮小後の acceptance criteria が再編前の production code ですでに満たされている場合は、plan rebaseline audit 例外を使ってよい。Full verification、outcome review、該当する UI smoke check を再編後の criteria で実施し、無修正で通った場合に限り completion と次 outcome の `ready` を記録する audit/status commit を作る。監査のための無意味な production code変更は行わない。監査で欠陥が見つかった場合は、修正を含む最後の vertical unit と同じ commit で閉じる。

本再編では `b6f2ef7e` を bridge baseline とした `LIB-01 Scan pipeline core ownership` だけが上記例外の対象である。この例外を通常の outcome completion、遅れた完了記録、未達 criteria の切り捨てに流用しない。

`GATE-01` は production implementation unit を持たない最終監査 outcome なので、同様に audit/status commit を許可する。全 Gate criteria、Full verification、該当する UI smoke check、gate review が無修正で完了した場合は、`gate met` と Release Freeze の状態だけを記録してよい。監査を通すための無意味な production code変更を行ってはならない。

## 互換性契約の判定

- C# の `public` / `protected` 修飾子だけでは互換性契約とみなさない。
- 維持対象は UI observable behavior、失敗契約、setting key / serialized value、DB schema / data、外部ファイル形式、および明示的にサポートしている SDK / plugin / CLI / IPC / COM / automation contract とする。
- 同一リポジトリ内の production caller、test project、XAML binding、resource lookup、reflection string は内部実装 consumer とする。
- 内部 consumer と XAML を同じ implementation unit で更新し、build / test / UI smoke check を通す場合は、public member を含む call shape を変更してよい。
- 旧 public member、旧 nested enum、旧 forwarding property を test または過去の内部 call shape のためだけに残さず、`[Obsolete]` wrapper も追加しない。
- escalation 前に具体的な supported out-of-repository consumer を特定する。特定できなければ内部リファクタリングとして進める。

## 作業開始

1. [PLAN_STATUS](./PLAN_STATUS.md) の active outcome と acceptance criteria を読む。
2. `git status --short` で既存差分を確認する。
3. `PLAN_STATUS.md` が plan rebaseline audit を次の作業として明記している場合、または active outcome が `GATE-01` の場合は、`unit-planner` を呼ばず下記 audit-only branch へ進む。
4. active outcome がなければ、総合計画の ordered backlog から最初の `ready` outcome を選ぶ。`ready` がなければ、先行 outcome と依存条件を確認して一つだけ `ready` にする。
5. 通常実装 branch では `unit-planner` に exactly one unit を計画させ、通常経路の owner 変更と旧経路削除が一緒に終わる vertical slice であることを root が確認する。
6. checkpoint、decision、inventory、調査メモを新規作成せず実装へ進む。

future outcome の class / interface / method 配置を先に詳細設計しない。ただし active outcome の安全な vertical unit が後続 owner 未確定のため作れない場合は、adapter を追加する前に、総合計画の正本へ owner が持つ state / behavior、handoff direction、依存順、residual bridge の retirement outcome を定義する。これは class-level design ではなく production ownership boundary の確定であり、必要な場合は先に行う。

internal owner dependency は `blocked` の理由にしない。既存 bridge を凍結し、ユーザー承認が必要な backlog 再編を行ったうえで prerequisite outcome を進める。難しさを隠すための host、adapter、factory、test seam を追加して active outcome を継続しない。

## Audit-only branch

plan rebaseline audit と `GATE-01` は implementation unit ではないため、開始時に `unit-planner` を呼ばない。

1. plan rebaseline audit は outcome review、`GATE-01` は gate review の scope で snapshot を凍結する。
2. Full verification、該当する UI smoke check、fresh read-only review を順に行う。
3. 未達 criteria または重大指摘が見つかった場合、status を完了へ進めない。plan rebaseline audit では修正対象を exactly one vertical unit として planner に渡し、通常の実装ループへ入る。`GATE-01` では指摘が属する completed outcome を上記遷移で唯一の active outcome に戻してから、planner に修正 unit を計画させる。
4. 無修正で監査を通過した場合だけ status を更新し、その status 差分を含む frozen snapshot を fresh reviewer に再レビューさせる。
5. 最終差分の format / whitespace と `git diff --check` を確認し、対象 outcome ID を含む audit/status commit を作る。監査ログや定型的な証跡資料は追加しない。

## Implementation unit の条件

implementation unit は次を満たすまとまりにする。原則として 1 つの user-visible workflow または 1 つの ownership boundary を端から端まで閉じ、seam 1 個、method 1 個、test 1 個を単位にしない。

- 同じ outcome の acceptance criteria を少なくとも 1 つ前進させる。
- build 可能で、関連 behavior を検証できる。
- 新しい abstraction を追加する場合、production 経路へ接続する。
- 旧 owner の責務、旧 route、旧 binding、旧 test seam のいずれかを減らす。
- cross-owner handoff は immutable request / snapshot / receipt / event facts / lease とし、相手 owner の private state や callback 一覧を contract にしない。
- 構造変更と意図的な挙動変更を混ぜない。

DTO、interface、result、planner、host、diagnostics API の追加だけで implementation unit を完了しない。安全上どうしても一時 scaffolding が必要でも、同じ未コミット unit 内で通常経路への接続と旧経路削除まで進める。build 可能な中間 commit を作るためだけに ownership 移管を分割しない。

library ownership phase では、総合計画の bridge baseline に既に存在し、retirement outcome が明記された bridge だけを一時的に残してよい。既存 bridge の member / factory / callback category を増やさず、当該 outcome が担当する category を同じ unit で減らす。consumer 固有の invalidation、freshness、presentation 更新を一つの巨大 mutation result または総合 host に移し替えない。

## 実装・レビュー・commit ループ

1. implementation unit を実装し、必要な behavior test を追加または更新する。
2. 関連 build / targeted test を実行する。
3. format / whitespace と `git diff --check` を確認する。
4. サブエージェントに未コミット差分の静的レビューを依頼する。レビュー担当は編集・build・test・format・analyzer・commit を行わない。
5. 重大指摘を修正する。
6. 修正の影響を受ける build / test を再実行する。
7. 重大指摘がなくなるまで、修正後の各 frozen snapshot を履歴 `/fork` なしの fresh `repo-static-review` へ再レビュー依頼する。同じ reviewer を再利用しない。
8. この unit で outcome を閉じる場合は、開始 commit からの全変更と現行コードを対象に full test とサブエージェント静的アーキテクチャレビューを行う。指摘があれば同じ unit で修正・再テスト・再レビューする。
9. outcome の全 acceptance criteria を満たした場合は、`PLAN_STATUS.md` で当該 outcome を `completed`、次 outcome を `ready` にする。Active outcome セクションを次 outcome の目的 / acceptance criteria / non-goals へ置き換える。この status 更新を含む最終未コミット差分を再レビューする。
10. 最終差分に対して必要な build / test / format / analyzer / `git diff --check` を実行する。
11. outcome ID を含む commit message で commit する。検証・レビュー結果は command output と Git diff / commit で追跡し、定型的な証跡資料は追加しない。
12. outcome が未完なら docs-only checkpoint を挟まず同じ outcome の次 unit へ、完了したなら `ready` にした次 outcome へ進む。

`GATE-01` だけは完了時に `completed` ではなく `gate met` とし、Active outcome を `none`、次 outcome を設定しない。Release Freeze は `gate met / explicit release instruction required` と記録し、`.NET 10` migration plan またはリリース作業を自動開始しない。

レビュー修正後の再テストを省略しない。レビュー前の test 結果を最終差分の検証結果として扱わない。

root agent を唯一の writer / stager / committer とする。planner・調査・レビューのサブエージェントは読み取り専用とし、review 依頼後は root も対象差分を変更しない。修正した場合は新しい snapshot として fresh reviewer に再レビューさせる。

## 標準確認

PowerShell 7 で実行する。

通常は統合入口を使う。

```powershell
pwsh -File .\scripts\verify-refactor.ps1 -Mode Quick
pwsh -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<関連 test filter>'
pwsh -File .\scripts\verify-refactor.ps1 -Mode Full
```

`Quick` は build、test、whitespace、diff check、`Full` はこれに restore、tool restore、Roslynator を加える。`Quick` で filter を省略した場合は全 test を実行する。共有 model、root ViewModel、DB、file system、settings、dispatcher、lock / concurrency に触れた場合と outcome 完了時は `Full` を使う。

script が環境要因で使えない場合の個別コマンドは次のとおり。

```powershell
dotnet build .\BeMusicSeeker.sln /p:Configuration=Release
dotnet test .\BeMusicSeeker.sln /p:Configuration=Release
dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal
$msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -version "[17.0,18.0)" -products * -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin"
dotnet roslynator analyze .\BeMusicSeeker.sln --msbuild-path $msbuildPath --properties Configuration=Release --severity-level warning --verbosity minimal
git diff --check
```

小さい unit は targeted test を先に実行してよい。レビュー修正後は、最終差分に対して統合入口を再実行する。

SDK は `global.json` の .NET SDK 10 系を使う。Roslynator 0.12.0 が MSBuild 18 で動作しない間は、上記のとおり Visual Studio 2022 / MSBuild 17 を指定する。analyzer Gate はコマンドが正常終了し、今回差分による warning が増えていないこととする。既存 warning は root `AGENTS.md` の方針に従い、info 診断は目的を定めた棚卸しでだけ扱う。

### UI outcome の smoke check

UI observable behavior に触れる outcome の完了時は、自動テストに加えて変更範囲に該当する次の操作を確認する。

実操作では、リポジトリを Release build した次の x64 executable だけを使用する。

```text
<repo-root>\bin\x64\Release\net472\BeMusicSeeker.exe
```

- `bin\Release\net472`、インストール済み BeMusicSeeker、表示名だけで見つけた同名 window は検証対象にしない。
- 起動前に上記ファイルが今回の build で生成済みであることを確認する。
- UI 操作ツールで起動・選択する場合は executable path の完全一致で対象 process / window を同定する。同名候補が複数ある場合は操作せず、path を再確認する。
- Computer Use では raw executable path が同名のインストール済み app へ解決される場合があるため、`process:<resolved-absolute-executable-path>` を launch / window 識別子に使う。返された window の `app` が同じ `process:` path と完全一致しなければ操作しない。
- screenshot や目視結果を evidence とする前に、対象 process の executable path が上記の resolved absolute path と一致することを確認する。一致を確認できない実操作結果は無効とする。
- smoke check 後は起動した repository build を正常終了し、インストール版を起動・終了しない。

- application startup、initial scan / reload、正常 shutdown。
- regular chart / playlist detail / playlist summary / play history の表示切替。
- table の sort、filter、selection、context action、drag-drop。
- settings dialog の open、edit、save、再表示。
- playback panel / external player host の主要操作。
- progress 表示、cancel、失敗時の dialog / status。

Codex が操作可能な環境では自走して確認する。外部 player、実データ、資格情報などが必要で確認できない項目だけを、outcome 完了前の manual evidence としてユーザーへ依頼する。

## サブエージェント静的レビュー

依頼文は次を基本形にする。

```text
review kind: <unit review | outcome review | gate review>
absolute repo path: <absolute-repo-path>
scope: <unit-base..HEAD + frozen worktree | outcome-base..HEAD + frozen worktree | code-baseline..HEAD + frozen worktree>
base SHA: <full-base-sha>
head SHA: <full-head-sha>
untracked files: <absolute path list | none>

現在の未コミット差分を静的レビューしてください。
編集、build、test、format、analyzer、commit は禁止です。
git diff / git status / rg / Get-Content などの読み取りだけを使ってください。

確認事項:
- active outcome の acceptance criteria を実際に前進させているか
- state と behavior の owner が明確になり、root の責務が減っているか
- abstraction / host / adapter / DTO を増やしただけになっていないか
- cross-owner handoff が immutable facts ではなく、相手 owner の private callback / mutable state / lock を列挙する contract になっていないか
- library ownership phase の residual bridge が baseline から追加・拡張されていないか。担当 category の bridge が同じ unit で減っているか
- 旧 route、旧 binding、root relay、callback host、test-only production seam が不要に残っていないか
- consumer 固有の cache / freshness / reference 更新を一つの巨大 mutation result に束ねていないか
- 挙動、DB schema、setting key、serialized value、外部形式、lock ordering を意図せず変えていないか
- public modifier の差分自体を互換性問題にしていないか。supported external contract を主張する場合は具体的な out-of-repository consumer が特定されているか
- View / global singleton / Settings / NLog / DB / Dispatcher への依存方向を悪化させていないか
- private 実装配置を固定する brittle test を増やしていないか
- .NET 10 migration blocker を増やしていないか

重大度順にファイルと行番号を付けて返してください。
問題がなければ「重大な指摘なし」と返してください。
```

重大指摘には少なくとも次を含む。

- build / test / runtime behavior を壊す可能性が高い。
- persisted data、UI observable behavior、失敗契約、supported external contract を意図せず変える。
- responsibility owner が増える、循環する、または root に残ったままになる。
- production の通常経路で使わない abstraction や test 専用 seam を追加する。
- global dependency、UI technology、DB connection、lock、Dispatcher の漏出を増やす。
- Gate の測定値だけを partial split や file move で満たす。

## Outcome 完了判定

implementation unit の積み重ねだけで outcome を自動完了にしない。開始 commit からの全差分と現行コードを確認し、総合計画の Outcome completion rule をすべて満たすことを確認する。

完了候補の unit を commit する前に、サブエージェントへ開始 commit 以降の commit 済み変更、現在の未コミット差分、現行コードをまとめて静的レビューさせる。重大指摘の修正、再テスト、再レビュー、`PLAN_STATUS.md` の完了更新を同じ最終 unit に含める。完了だけを記録する docs-only commit は作らない。ただし Outcome state 遷移で定義した plan rebaseline audit と `GATE-01` の例外は、それぞれの条件をすべて満たす場合に限り audit/status commit を許可する。

## 計画資料と ADR

active な計画資料は次の 4 ファイルに限定する。

- `BeMusicSeekerリファクタリング計画.md`
- `00_Codex共通実行ルール.md`
- `PLAN_STATUS.md`
- `DOTNET10_MIGRATION_BLOCKERS.md`

`PLAN_STATUS.md` は baseline、active outcome と acceptance criteria、outcome states、Gate scorecard、active outcome blocker だけを持つ。更新は outcome transition、blocker の発生 / 解消、Gate evidence の実質的変化に限定する。完了履歴、テスト件数、行数推移、次 unit / seam の調査ログは Git commit とコード差分に残す。

永続判断は原則として関連する正本の target / Gate / blocker policy へ反映する。独立 ADR は次をすべて満たす場合だけ、4文書制を拡張する理由とともにユーザーへ提案し、承認後に追加する。

- 複数の現実的な選択肢がある。
- outcome 完了後も判断理由を参照する必要がある。
- persisted data、UI observable behavior、失敗契約、supported external contract、lock / concurrency、`.NET 10` migration policy のいずれかに影響する。

「次に切る helper」「次の private reflection test」「一時的な class / interface 配置」は ADR にしない。

## 自走と escalation

Codex は、active outcome の範囲内で設計・実装・test・review・commit を継続する。次の場合だけユーザーへ確認する。

- UI observable behavior、失敗契約、persisted data、supported external contract の意味を変える必要がある。supported external contract を理由にする場合は具体的な out-of-repository consumer を特定する。
- 目標アーキテクチャまたは ordered backlog を実質的に変更する必要がある。
- 外部資産、資格情報、手動 UI 操作など、Codex だけでは取得できない情報が必要である。
- 安全な選択肢を調査しても、複数案の trade-off をユーザーが決める必要がある。

単に実装が大きい、難しい、時間がかかる、追加調査が必要という理由では停止しない。outcome を implementation unit に分けて進める。
