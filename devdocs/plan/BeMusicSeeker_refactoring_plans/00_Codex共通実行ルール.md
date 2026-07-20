# Codex 共通実行ルール

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md)に基づいて、Codex が自走して実装・検証・静的レビュー・commit を繰り返すためのルールである。

## 権限と作業境界

- outcome 内の implementation unit は、検証とサブエージェント静的レビュー後に自発的に commit してよい。
- ユーザーが明示依頼した planning / operation docs と agent 構成だけの変更は、差分に直接対応する軽量検証後に独立 commit にしてよい。
- unrelated な既存差分を変更、stage、commit しない。
- Refactoring Completion Gate 前は、ユーザーから依頼されても `git push`、tag、release、publish、version 更新を行わない。Gate 後もユーザーの明示指示なしには行わない。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約、明示的にサポートする SDK / plugin / CLI / IPC / COM / automation contract を変更する必要が生じたら、実装前にユーザーへ確認する。
- 意味の変わる fallback を追加しない。失敗を隠すより、既存の失敗契約を維持して明示的に失敗させる。
- C# symbol rename は text replacement ではなく semantic rename / compiler-driven edit を使う。
- 検証用 script / command に固定 timeout を追加しない。特に 120 秒（2 分）で build、test、format、analyzer を打ち切らず、開始したプロセスが完了するまで待つ。既存の検証入口にも 120 秒 timeout を導入してはならない。

## 実行ロール

- **planner**: `unit-planner`（Sol High、read-only、approval never）。active outcome の active execution package を、現在の committed HEAD から実装可能な 1〜5 個の順序付き vertical unit へ分解する。planner は package 内の production route / caller / writer / lock / transaction / failure path / tests / deletion scope の read-only inventory を一度だけ担当し、内部複雑性を停止判定に使わない。出力は task 内だけに保持し、per-unit plan 文書、checkpoint、progress log を repository に作らない。
- **implementation root**: Luna Max。唯一の writer / stager / committer とし、planner が示した sequence を継続して実装・検証する。planner の調査を root 側で独立に再実行せず、結果受領後は named route / symbol / test の bounded feasibility check と実装へ進む。サブエージェントへ書き込みを委譲しない。
- **reviewer**: fresh `repo-static-review`（Sol High、read-only、approval never）。実装担当から独立して frozen snapshot を評価し、`/fork` は使用しない。build / test / format / analyzer は root が担当する。

モデルを利用できない場合に別モデルへ黙って置き換えない。作業を開始せず、利用不能なロールと理由を報告する。

## 変更種別と完了条件

作業開始時に現在の差分を次のいずれかへ分類し、その種別に定義した確認だけを行う。複数種別が同じ差分に含まれる場合は、最も強い条件を適用する。

1. **code implementation unit**
   - production / test / build / resource / verification script と、それに伴う資料更新を一つの unit として扱う。
   - 関連 build / behavior test、必要な format / analyzer / UI smoke、unit または outcome review を行う。
   - reviewer の重大指摘で code / test / reviewed docs を修正した場合は、影響範囲の検証と fresh review を行う。
2. **outcome / gate closure**
   - completion state、次 outcome、Gate evidence を review snapshot を凍結する前に更新し、code range と status を一度の outcome / gate review で評価する。
   - outcome は Full verification、該当する UI smoke、outcome review、最後の code unit を同じ commit で閉じる。`GATE-01` と承認済み plan rebaseline audit は audit range を評価するため、production code 差分がなくても audit review を行う。
3. **sequence cursor-only update**
   - review 済み implementation unit の次 cursor を planner 出力どおり `PLAN_STATUS.md` の現在値へ反映する。
   - cursor 行以外の差分が混ざっていないことと `git diff --check` を確認し、同じ implementation unit commit に含める。cursor 前進によって code の検証結果と review 結果は変わらないため、build / test / analyzer / UI smoke /再レビューは追加しない。
4. **planning / operation docs-only update**
   - planning Markdown、`AGENTS.md`、`.codex/config.toml`、`.codex/agents/*.toml` だけを変更し、production / test / build / resource / verification script に差分がない作業を指す。
   - Markdown の参照、TOML 構文、相互整合、whitespace、`git diff --check` を確認する。code build / test / analyzer / UI smoke と `repo-static-review` は実行しない。

## Single-flight orchestration

- active にできるサブエージェントは常に 1 つだけとする。planner、reviewer、第 2 の調査 agent を並行起動しない。
- planner または reviewer を起動した時点から結果を受領するまで、root は repository に対する `git` / `rg` / file read、追加調査、編集、build、test、format、analyzer、stage、commit を凍結する。別 scope を名目にした並行調査も行わない。
- planner の route inventory と同じ問いを root の「独立調査」や別 planner の consensus で再検証しない。前提不一致が見つかった場合は、差異を限定して同じ planner contract に repair request を出すか、既存 sequence を局所的に分解する。
- reviewer 起動後は frozen snapshot を一切変更しない。reviewer の結果後に修正した場合は別 snapshot として fresh reviewer を一つだけ起動する。
- subagent 待機中に root が行ってよいのは、受領後に行う作業の思考整理だけである。repository evidence を新たに収集しない。

## Review scope

- **unit review**: unit 開始時の clean commit を `base`、review対象 snapshot の `HEAD` を `head` とし、scope は `<unit-base>..HEAD + frozen worktree` とする。同じ unit の production route、tests、関連資料、削除対象を評価する。
- **outcome review**: `PLAN_STATUS.md` の `active outcome base commit` を `base`、review対象 snapshot の `HEAD` を `head` とし、scope は `<outcome-base>..HEAD + frozen worktree` と現行 production code とする。active outcome の全 acceptance criteria と残る旧 route を評価する。
- **gate review**: `PLAN_STATUS.md` の `code baseline commit` を `base`、review対象 snapshot の `HEAD` を `head` とし、scope は `<code-baseline>..HEAD + frozen worktree`、現行コード、全 outcome state、Gate criteria、migration blocker register とする。

review 依頼には review kind、absolute repo path、scope、base/head SHA を含める。reviewer は `git status --short`、tracked / staged diff、`git ls-files --others --exclude-standard` から frozen worktree の変更を自ら列挙する。親が untracked file 一覧を別途作成・転記する工程は設けない。review 開始後は root も frozen snapshot を変更せず、重大指摘の修正後は別 snapshot として fresh review を依頼する。

## Outcome state 遷移

- `not started` → `ready`: 先行 outcome と依存条件が満たされ、次に着手できるときだけ遷移する。
- `ready` → `in progress`: active outcome として選択し、最初の implementation unit を開始するときに遷移する。同時に開始時の clean commit を `active outcome base commit` として記録する。
- `GATE-01 ready` → `GATE-01 in progress`: implementation unit ではなく、gate review scope の snapshot を凍結して Full verification を開始するときに遷移する。gate review は `code baseline commit` を base にするため、新しい `active outcome base commit` は記録しない。
- `ready` → `blocked`: planner または root が、ユーザー入力または外部状態変更なしには解消できない具体的な `EXTERNAL_BLOCKER` を確認した場合だけ遷移する。内部の ownership 分解、複数 caller、broad host、lock / transaction、変更量は該当しない。
- `in progress` → `blocked`: 同じ `EXTERNAL_BLOCKER` が実装中にも確認され、ユーザー入力または外部状態変更なしに進めない場合だけ遷移する。難しい、調査が必要、変更量が大きい、既存 route が broad であることは理由にしない。
- `blocked` → `ready`: 最初の unit 開始前に blocked となり、`active outcome base commit` をまだ記録していない outcome の阻害条件が解消したときに遷移する。その後の `ready` → `in progress` で base を記録する。
- `blocked` → `in progress`: unit 開始後に blocked となり、既に `active outcome base commit` がある outcome の阻害条件が解消したときに限り、同じ base を保持して遷移する。
- `in progress` → `not started`: production evidence により責務が active outcome ではなく別の prerequisite outcome に属すると判明し、target architecture / ordered backlog を変えるユーザー承認済みの ownership 再編を行う場合だけ使う。planner が broad route を分解できない、内部調査が増えた、安全な unit が大きいという理由では使わない。変更前の outcome base から frozen snapshot までを re-plan review し、移動先、依存順、必要な bridge baseline と retirement outcome を正本へ記録してから、prerequisite を唯一の active outcome にする。criteria の破棄や通常の延期には使わず、元 outcome を再開するときはその時点の clean commit を新しい base とする。
- `in progress` → `completed`: acceptance criteria、outcome-wide Full verification、outcome review、UI smoke check（該当時）が完了したときだけ遷移する。
- `completed` → `in progress`: `GATE-01` の監査で、その outcome の acceptance criteria が現行 production code で満たされていないことが判明した場合だけ使う。`GATE-01` を `not started` に戻し、修正開始時の clean commit を新しい active outcome base として記録する。
- `GATE-01 in progress` → `GATE-01 not started`: Gate の Full verification、UI smoke check、または gate review で未達 criteria が見つかり、指摘が属する completed outcome を再開するときだけ遷移する。
- `GATE-01 in progress` → `gate met`: 全 Gate criteria、Full verification、該当する UI smoke check、gate review が完了したときだけ遷移する。`gate met` は GATE-01 以外に使わない。

通常 outcome の `completed` への状態更新は、最後の production code implementation unit の outcome review snapshot に先に含め、code・test・関連資料・status を一度に review して同じ commit で閉じる。完了条件がコード変更なしで初めて満たされたように見える場合は、完了監査が遅れていないか再確認し、安全な最後の vertical unit と一緒に閉じる。

ただし、ユーザー承認済みの計画再編により `in progress` outcome を一貫した責務境界へ縮小し、移動した責務、後続 outcome、bridge retirement outcome が正本に明記され、縮小後の acceptance criteria が再編前の production code ですでに満たされている場合は、plan rebaseline audit 例外を使ってよい。Full verification、outcome review、該当する UI smoke check を再編後の criteria で実施し、無修正で通った場合に限り completion と次 outcome の `ready` を記録する audit/status commit を作る。監査のための無意味な production code変更は行わない。監査で欠陥が見つかった場合は、修正を含む最後の vertical unit と同じ commit で閉じる。

`GATE-01` は production implementation unit を持たない最終監査 outcome なので、同様に audit/status commit を許可する。全 Gate criteria、Full verification、該当する UI smoke check、gate review が無修正で完了した場合は、`gate met` と Release Freeze の状態だけを記録してよい。監査を通すための無意味な production code変更を行ってはならない。

## 互換性契約の判定

- C# の `public` / `protected` 修飾子だけでは互換性契約とみなさない。
- 維持対象は UI observable behavior、失敗契約、setting key / serialized value、DB schema / data、外部ファイル形式、および明示的にサポートしている SDK / plugin / CLI / IPC / COM / automation contract とする。
- 同一リポジトリ内の production caller、test project、XAML binding、resource lookup、reflection string は内部実装 consumer とする。
- 内部 consumer と XAML を同じ implementation unit で更新し、build / test / UI smoke check を通す場合は、public member を含む call shape を変更してよい。
- 旧 public member、旧 nested enum、旧 forwarding property を test または過去の内部 call shape のためだけに残さず、`[Obsolete]` wrapper も追加しない。
- escalation 前に具体的な supported out-of-repository consumer を特定する。特定できなければ内部リファクタリングとして進める。

## 作業開始

1. [PLAN_STATUS](./PLAN_STATUS.md) の active outcome、active execution package、sequence cursor、acceptance criteria を読む。
2. `git status --short` で既存差分を確認する。
3. `PLAN_STATUS.md` が plan rebaseline audit を次の作業として明記している場合、または active outcome が `GATE-01` の場合は、`unit-planner` を呼ばず下記 audit-only branch へ進む。
4. active outcome がなければ、総合計画の ordered backlog から最初の `ready` outcome を選ぶ。`ready` がなければ、先行 outcome と依存条件を確認して一つだけ `ready` にする。
5. 通常実装 branch では single-flight で `unit-planner` を一度だけ呼び、現在の cursor から 1〜5 unit の `IMPLEMENTATION_SEQUENCE` を作らせる。root は planner 実行中の repository 調査を凍結する。
6. planner 結果後、root は最初の unit が named production route、削除または縮小する旧 responsibility corridor、behavior test、検証を持つことだけを bounded check する。別の architecture survey、独立調査、第 2 planner の consensus を行わない。
7. checkpoint、decision、inventory、調査メモを新規作成せず、sequence の最初の unit を実装する。

future outcome の class / interface / method 配置を先に詳細設計しない。ただし active outcome の実装に必要な owner state / behavior、handoff direction、依存順、residual bridge の retirement outcome は正本の boundary として先に定義してよい。これは class-level design ではなく、planner が安全な sequence を作るための production ownership contract である。

internal owner dependency、複数 caller、broad host、lock ordering、DB / live atomicity、package / LR2 / resource-health residual は `blocked` の理由にしない。planner は immutable request の prepare、durable write、canonical live apply、receipt publish、consumer residual apply、host retirement の responsibility corridor へ再帰的に分解する。既存 bridge は凍結し、難しさを隠すための host、adapter、factory、test seam を追加しない。

planner の有効な出力は `IMPLEMENTATION_SEQUENCE` または具体的な `EXTERNAL_BLOCKER` だけである。`NO_SAFE_UNIT` は無効な出力として扱う。内部複雑性を理由に返された場合、root は作業を停止せず、上記 corridor と正本の named sequence を引用して planner に repair request を出し、実装可能な sequence を返させる。再び無効な出力になった場合は、正本の sequence cursor が指す named unit をそのまま開始し、同じ問いの再調査を繰り返さない。正本の target architecture / ordered backlog を変えない局所的な sequence 修正はユーザー承認を要しない。意味のある外部契約または target architecture の選択が必要な場合だけ escalation する。

## Audit-only branch

plan rebaseline audit と `GATE-01` は implementation unit ではないため、開始時に `unit-planner` を呼ばない。

1. plan rebaseline audit は outcome review、`GATE-01` は gate review の scope で Full verification と該当する UI smoke check を行う。
2. verification evidence が criteria を満たした場合は、completion / `gate met` と次状態を `PLAN_STATUS.md` に反映してから snapshot を凍結する。
3. code range、現行コード、Gate evidence、status を一度の fresh read-only outcome / gate review で評価する。
4. 未達 criteria または重大指摘が見つかった場合は completion 候補を取り下げる。plan rebaseline audit では修正対象を planner に渡して最初の実装可能 unit を含む sequence を作らせ、通常の実装ループへ入る。`GATE-01` では指摘が属する completed outcome を上記遷移で唯一の active outcome に戻してから、planner に修正 sequence を計画させる。
5. 重大指摘の修正を行った場合は影響範囲を再検証し、新しい frozen snapshot を fresh reviewer に渡す。重大指摘がなくなった最終差分の format / whitespace と `git diff --check` を確認し、対象 outcome ID を含む audit/status commit を作る。監査ログや定型的な証跡資料は追加しない。

## Implementation unit の条件

implementation unit は、1 つの user-visible workflow、1 つの ownership boundary、または broad route を横断する 1 つの responsibility corridor を、production 経路から behavior test まで閉じるまとまりにする。seam 1 個、method 1 個、test 1 個を単位にしない。

- 同じ outcome の acceptance criteria を少なくとも 1 つ前進させる。
- build 可能で、関連 behavior を検証できる。
- 新しい abstraction を追加する場合、同じ unit で production 経路へ接続する。
- 移管した responsibility corridor の旧 writer、旧 callback、旧 binding、旧 test seam のいずれかを削除し、少なくとも bridge surface を減らす。
- cross-owner handoff は immutable request / snapshot / receipt / event facts / lease とし、相手 owner の private state、lock、callback 一覧を contract にしない。
- durable state と live state を扱う unit は、prepare → durable commit → canonical live apply → guard release → receipt publish の順序と failure atomicity を behavior test で固定する。
- package、LR2、playlist、UI など後続 owner の baseline residual は canonical receipt publish 後の composition として残してよい。catalog guard を保持したまま別 owner を callback しない。
- 構造変更と意図的な挙動変更を混ぜない。

DTO、interface、result、planner、host、diagnostics API の追加だけで implementation unit を完了しない。安全上どうしても一時 scaffolding が必要でも、同じ未コミット unit 内で production 接続と corridor 固有の旧 route 削除まで進める。

broad host 全体、全 operation-specific caller、全 consumer residual の削除を毎 unit に強制しない。正本に依存順付き retirement unit がある場合、既存 host / composition は member、factory、callback category を増やさず、各 unit で担当 corridor の surface を減らす限り、その retirement unit まで残してよい。これを build 可能な中間 commit のための新規 forwarding seam に置き換えない。

## 実装・レビュー・commit ループ

1. planner が返した sequence の最初の unit を実装し、必要な behavior test と、code変更に伴う仕様・計画資料を同じ差分で更新する。
2. 変更範囲に対応する build / targeted test / format / analyzer / `git diff --check` を実行する。review 前の snapshot を変更しなければ、この結果を unit の最終検証結果として使う。
3. この unit で outcome を閉じる場合は、開始 commit からの全変更と現行コードを対象に Full verification と該当する UI smoke check を行い、`PLAN_STATUS.md` の当該 outcome、次 outcome、Active outcome、Gate evidence を completion 候補へ更新する。
4. single-flight で、code / test / build / resource の変更と関連資料を含む frozen snapshot を `repo-static-review` に渡す。通常 unit は unit review、outcome 完了候補は outcome review とする。reviewer は frozen worktree の tracked / staged / untracked changes を自ら列挙する。
5. 結果受領後に重大指摘を修正した場合は、影響を受ける build / test / format / analyzer を再実行し、修正後の snapshot を履歴 `/fork` なしの fresh reviewer に渡す。重大指摘がなくなるまで一つずつ繰り返す。
6. outcome が未完なら、reviewer が重大指摘なしとした code snapshot を変えず、planner が出力した次 cursor だけを `PLAN_STATUS.md` に反映する。cursor-only diff と `git diff --check` を確認し、build / test / analyzer / UI smoke /再レビューを追加せず同じ commit に含める。
7. outcome が完了候補なら、completion state を含む outcome review snapshot をそのまま commit 対象にする。review 後に status だけを追加して二度目の review を作らない。
8. outcome ID を含む commit message で commit する。検証・レビュー結果は command output と Git diff / commit で追跡し、定型的な証跡資料は追加しない。
9. **commit は内部 checkpoint であり、ユーザーへの応答境界ではない。** outcome が未完なら planner sequence の次 unit を直ちに開始する。sequence を使い切っても outcome が未完なら、ユーザー承認待ちにせず single-flight で planner を再実行する。outcome が完了したなら `ready` にした次 outcome へ進む。
10. planner sequence の前提が実装 evidence で崩れた場合だけ、その差異を限定して planner に局所的な再分解を依頼する。root の broad な独立調査や同じ package の全面再調査を行わず、現在 unit の code snapshot を閉じる。

`GATE-01` だけは完了時に `completed` ではなく `gate met` とし、Active outcome を `none`、次 outcome を設定しない。Release Freeze は `gate met / explicit release instruction required` と記録し、`.NET 10` migration plan またはリリース作業を自動開始しない。

レビュー修正で code / test / build / resource / reviewed docs が変わった場合は、影響範囲を再検証する。reviewer が重大指摘なしとした snapshot に cursor-only 前進を加えただけの場合は、review 前の検証結果を最終結果として扱う。

root agent を唯一の writer / stager / committer とする。planner・reviewer は読み取り専用とし、同時に 1 agent だけを使う。subagent 実行中は root の repository 操作を凍結し、結果後にだけ次の phase へ進む。

## 標準確認

PowerShell 7 で実行する。

code implementation unit は統合入口を使う。

```powershell
pwsh -File .\scripts\verify-refactor.ps1 -Mode Quick
pwsh -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<関連 test filter>'
pwsh -File .\scripts\verify-refactor.ps1 -Mode Full
```

`Quick` は build、test、whitespace、diff check、`Full` はこれに restore、tool restore、Roslynator を加える。`Quick` で filter を省略した場合は全 test を実行する。共有 model、root ViewModel、DB、file system、settings、dispatcher、lock / concurrency に触れた場合と outcome 完了時は `Full` を使う。

planning / operation docs-only update は統合入口を使わず、変更した形式に対応する構文・参照確認と `git diff --check` を行う。cursor-only update は cursor 行だけの差分確認と `git diff --check` を行う。

script が環境要因で使えない場合の個別コマンドは次のとおり。

```powershell
dotnet build .\BeMusicSeeker.sln /p:Configuration=Release
dotnet test .\BeMusicSeeker.sln /p:Configuration=Release
dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal
$msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -version "[17.0,18.0)" -products * -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin"
dotnet roslynator analyze .\BeMusicSeeker.sln --msbuild-path $msbuildPath --properties Configuration=Release --severity-level warning --verbosity minimal
git diff --check
```

小さい unit は targeted test を先に実行してよい。reviewer の重大指摘を修正して code snapshot が変わった場合は、影響範囲に対応する統合入口を再実行する。

targeted / Full test で失敗を検出した場合、直接の変更箇所と無関係に見えても「既知の失敗」「baseline failure」「flaky」と分類して完了扱いしない。コンテキスト圧縮により、現在の agent が原因となった過去の implementation unit を保持していない可能性を前提に、失敗 test、production route、期待値の導入・変更 commit を Git 履歴から確認する。現行契約に対して実装または期待値のどちらが誤っているかを判断し、同じ作業サイクルで修正して最終差分を再検証する。

全体 test で失敗し個別実行で成功する test は、一過性の成功ではなく test isolation / synchronization の欠陥として扱う。共有 mutable static、実行順、非同期 worker と observable apply の完了条件、dispatcher / scheduler queue、時刻、file / DB / environment state を調査する。個別再実行の成功だけで閉じず、observable completion を待つ、専用 dispatcher / state へ隔離する、共有状態を確実に復元するなど test 構造を改善し、反復実行と全体 test の両方で確認する。

SDK は `global.json` の .NET SDK 10 系を使う。Roslynator 0.12.0 が MSBuild 18 で動作しない間は、上記のとおり Visual Studio 2022 / MSBuild 17 を指定する。analyzer Gate はコマンドが正常終了し、今回差分による warning が増えていないこととする。既存 warning は root `AGENTS.md` の方針に従い、info 診断は目的を定めた棚卸しでだけ扱う。

### UI outcome の smoke check

UI observable behavior に触れる outcome の完了時は、自動テストに加えて変更範囲に該当する次の操作を確認する。

実操作では、リポジトリを Release build した次の x64 executable だけを使用する。

```text
<repo-root>\bin\x64\Release\net472\BeMusicSeeker.exe
```

- `bin\Release\net472`、インストール済み BeMusicSeeker、表示名だけで見つけた同名 window は検証対象にしない。インストール版の path を取得・列挙する必要はなく、検証対象にも含めない。
- 起動前に上記ファイルが今回の build で生成済みであることを確認する。
- 起動は resolved absolute path を `Start-Process -FilePath` または UI 操作ツールの `process:<resolved-absolute-executable-path>` に直接渡す。インストール版を探すための app 一覧取得や path 置換を行わない。
- UI 操作ツールで対象 process / window を同定する場合は、built executable path の完全一致を process 側で確認する。同名候補や別 executable path が返っても、それを検証対象へ切り替えない。対象 process と window の対応を確認できない場合は操作せず、smoke 未実施として報告する。
- screenshot や目視結果を evidence とする前に、対象 process の executable path が上記の resolved absolute path と一致することを確認する。一致を確認できない実操作結果は無効とする。
- smoke check 後は起動した repository build だけを正常終了する。インストール版の起動・終了・path 取得は行わない。

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

最初に git status、tracked / staged diff、untracked file を read-only で列挙し、frozen worktree の code / test / build / resource 変更と、それに伴う資料更新を静的レビューしてください。
編集、build、test、format、analyzer、commit は禁止です。
git diff / git status / git ls-files / rg / Get-Content などの読み取りだけを使ってください。

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

完了候補の unit では、Full verification と該当する UI smoke check の後に `PLAN_STATUS.md` の completion 候補を作り、開始 commit 以降の commit 済み変更、現在の未コミット差分、現行コード、completion state を一度の outcome review で評価する。重大指摘の修正、再検証、fresh review を同じ最終 unit に含める。通常 outcome の completion は最後の code unit と同じ commit に置き、Outcome state 遷移で定義した plan rebaseline audit と `GATE-01` だけは audit range を評価した audit/status commit を使う。

## 計画資料と ADR

active な計画資料は次の 4 ファイルに限定する。

- `BeMusicSeekerリファクタリング計画.md`
- `00_Codex共通実行ルール.md`
- `PLAN_STATUS.md`
- `DOTNET10_MIGRATION_BLOCKERS.md`

`PLAN_STATUS.md` は baseline、active outcome と acceptance criteria、active execution package と現在の sequence cursor、outcome states、Gate scorecard、active outcome blocker だけを持つ。更新は outcome transition、cursor の前進、blocker の発生 / 解消、Gate evidence の実質的変化に限定する。過去 cursor、完了履歴、テスト件数、行数推移、seam の調査ログは追記せず、Git commit とコード差分に残す。

planning / operation docs-only update では、正本と agent 設定の意味・参照・構文を root が確認する。code reviewer は implementation diff の設計・挙動・互換性を評価する役割に限定し、docs-only update や cursor-only update の承認工程として使わない。

永続判断は原則として関連する正本の target / Gate / blocker policy へ反映する。独立 ADR は次をすべて満たす場合だけ、4文書制を拡張する理由とともにユーザーへ提案し、承認後に追加する。

- 複数の現実的な選択肢がある。
- outcome 完了後も判断理由を参照する必要がある。
- persisted data、UI observable behavior、失敗契約、supported external contract、lock / concurrency、`.NET 10` migration policy のいずれかに影響する。

「次に切る helper」「次の private reflection test」「一時的な class / interface 配置」は ADR にしない。

## 自走と escalation

Codex は、active outcome の範囲内で planning、設計、実装、test、review、commit を連続して行う。planner sequence、unit commit、review 完了は自走の checkpoint であり、ユーザーへの応答理由にしない。次の場合だけユーザーへ確認する。

- UI observable behavior、失敗契約、persisted data、supported external contract の意味を変える必要がある。supported external contract を理由にする場合は具体的な out-of-repository consumer を特定する。
- 目標アーキテクチャまたは ordered backlog を実質的に変更する、相互に排他的で後戻り困難な選択をユーザーが決める必要がある。
- 外部資産、資格情報、手動 UI 操作、利用不能な必須 tool / model など、Codex だけでは取得・解消できない具体的な外部状態が必要である。

次は escalation、`blocked`、応答終了の理由にしない。

- 複数 caller、broad host、nested host、callback fan-out がある。
- lock order、DB / live atomicity、failure fallback が複数責務にまたがる。
- package、LR2、playlist、resource-health、UI の residual が同じ旧 route に残る。
- 実装が大きい、難しい、時間がかかる、追加調査が必要である。
- planner の最初の候補が広すぎる、または一つの unit に閉じない。

これらは planner が responsibility corridor と依存順へ再分解して解消する。root は planner 実行中に独立調査せず、結果後は sequence を継続して実装する。
