# Codex 共通実行ルール

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md)に基づき、Codexが実装・検証・静的レビュー・commitを連続して進めるための運用正本である。計画消化や行数削減ではなく、MVVM ownership、dependency direction、testability、`.NET 10` migration boundaryを優先する。

## 権限と作業境界

- active outcome内のcode unitは、検証とfresh read-only review後に自発的にcommitしてよい。
- planning / operation docsとagent設定だけの変更は、構文、参照、whitespace、`git diff --check`を確認して独立commitにしてよい。production / test / build / resource / verification scriptに差分がなければbuild、test、analyzer、code reviewは行わない。
- unrelatedな既存差分を変更、stage、commitしない。
- Gate前は`git push`、tag、release、publish、version更新を行わない。Gate後もユーザーの明示指示なしには行わない。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約、supported external contractの意味を変える必要がある場合だけ、実装前にユーザーへ確認する。
- 意味の変わるfallbackを追加せず、既存の失敗契約を維持する。

## 実行ロールとsingle-flight

- **planner**: `unit-planner`。read-onlyでexecution anchor全体のclosure inventoryとactive implementation batchを作る。
- **implementation root**: 唯一のwriter / stager / committer。plannerのinventoryを独立にやり直さず、batchを順に実装する。
- **reviewer**: fresh `repo-static-review`。frozen snapshotをread-onlyで評価する。

指定model / roleを利用できない場合は別modelへ黙って置き換えず、利用不能な外部状態として明示する。

activeにできるサブエージェントは常に1つだけとする。plannerまたはreviewerを起動してから結果を受領するまで、rootはrepositoryに対する`git` / `rg` / file read、追加調査、編集、build、test、format、analyzer、stage、commitを凍結する。第2planner、consensus取得、同scopeの「独立調査」を行わない。

planner結果後、rootはplannerが挙げたroute / symbol / testの存在と最初のunitのfeasibilityだけをbounded checkする。前提不一致があれば差異だけをplannerへ返すか、active batch内で局所修正し、package全体のarchitecture surveyを再開しない。

## Execution anchor と active batch

`PLAN_STATUS.md`はper-unit cursorではなく、次を持つ。

- **active execution package**: Outcome内の安定した残存境界。
- **execution anchor**: package内の現在phase。package entry、grouped implementation、outcome closureなどの粗い再開点。
- **active implementation batch**: plannerがanchor全体をinventoryして作った原則2〜4個の有限なunit列。

execution anchorはimplementation unitごとには更新しない。phaseが変わるときだけ更新する。plannerが作った長いunit IDや「planner required」をcursorとして永続化しない。

### Plannerを起動する条件

plannerは次のいずれかの場合だけsingle-flightで一度起動する。

1. 新しいexecution anchorへ入り、active batchがまだmaterializeされていない。
2. active batchの全unitが`completed` / `skipped`だが、anchorのexit conditionが未達である。
3. 現行production evidenceがbatchの具体的な前提を無効にし、残unitをそのまま実装できない。

active batchに`active`または`pending`のunitがある間はplannerを再起動しない。commit、context reset、review完了、次unit開始はplanner再起動条件ではない。

### Batch materialization

planner結果受領後、rootは次を行う。

1. `PLAN_STATUS.md`の`Active implementation batch`へ、batch ID、unit、state、closure familyを直ちに記入する。
2. このstatus差分を単独commitせず、最初のcode unitと同じworktreeで保持する。contextが変わっても未コミット差分からbatchを再開できる。
3. 最初のcode unit commitでは、実装済み行を`completed`、次行を`active`へ更新して同じcommitに含める。
4. 後続unitも対応するbatch stateだけを同じcode commitで更新する。status-only progress commitを作らない。
5. batch完了時にexecution anchorを次phaseへ進める。anchor transitionがoutcome closure auditだけを開始する場合は、最後のcode unitまたは最終audit/status commitに含める。

active batchの履歴は残さない。batchが完了してanchorを進めるときは、完了batchを削除し、次anchor用の未materialize状態または新batchへ置き換える。履歴はGitに残す。

## Planner contract

plannerの有効な出力は`CLOSURE_INVENTORY` + `IMPLEMENTATION_BATCH`、または具体的な`EXTERNAL_BLOCKER`だけである。`NO_SAFE_UNIT`は無効である。

plannerはanchor対象surfaceを一度だけinventoryし、各残件を`BLOCKING`、`ALLOWED_BOUNDARY`、`DEFERRED_OWNER`へ分類する。最初に見つかった未完routeだけを返さず、anchorを閉じる有限集合を示す。

batchは原則2〜4unitとする。1unitだけを返してよいのは、anchor / outcomeの最終closure、または同じowner familyのcohesiveな残件が一つしかない場合だけである。各unitは同じowner、同じuser-visible behavior、同じtest / verification scopeの隣接routeをまとめる。

内部複雑性、複数caller、broad host、lock ordering、DB / live atomicity、package / LR2 / resource-health residual、変更量、追加調査はblockerではない。prepare / durable write / canonical live apply / receipt publish / consumer residual / host retirementのresponsibility corridorへ分解する。

plannerが無効な`NO_SAFE_UNIT`やmethod一件だけのmicro-unitを返した場合、rootは停止せず、本ルールのbatch granularityを引用して一度repair requestを出す。再び無効なら、正本のexecution anchorに定義されたstable phaseをrootが開始し、同じ問いを再調査しない。

## Implementation unit の条件

unitは一つのowner family、user-visible workflow、dependency corridor、またはbroad routeのcohesiveなresponsibility corridorを、production routeからbehavior testまで閉じる。

必須条件:

- active outcomeのacceptance criteriaを実質的に前進させる。
- productionの通常経路へ新owner / contractを接続する。
- 同じunitで担当corridorの旧writer、旧callback、旧binding、旧relay、旧test seamのいずれかを削除し、surfaceを減らす。
- build可能で、private配置ではなくbehaviorを検証できる。
- cross-owner handoffをimmutable request / snapshot / receipt / event facts /用途限定leaseにする。
- durable / live stateを扱う場合は、prepare → durable commit → canonical live apply → guard release → receipt publishの順序とfailure atomicityを固定する。

次を独立unitにしない。

- method、callback、event、property、binding relay、source-text testの一件だけ。
- DTO、helper、rename、interface、adapter、host、diagnostics API、partial splitの追加だけ。
- 行数、file数、type数の削減だけ。
- planner再調査、inventory文書、status更新、checkpoint作成だけ。

同じowner、同じbehavior、同じtest fixture、同じverification scopeの隣接routeは一つのunitへまとめる。unitが大きくてもcohesiveなら分割しない。WPF view-host、transaction / failure boundary、algorithmを数値のためだけに分断しない。

broad host全体を毎unitで消す必要はないが、正本のretirement unitまでsurfaceを増やさず、各unitで担当categoryを減らす。新しいforwarding seamでbuild可能な中間状態を作らない。

## UI-05 terminal closure の実行

UI-05では[総合計画のUI-05 terminal execution package](./BeMusicSeekerリファクタリング計画.md#ui-05-terminal-execution-package)を正本とする。

- `UI05-T1`でplannerを一度だけ起動し、root ViewModel、MainWindow、XAML binding、typed presentation subscription、test seamを有限inventoryにする。
- `BLOCKING`だけを`UI05-T2`の最大3 owner-family unitへまとめる。callback / route単位のA〜AG系列を作らない。
- typed immutable presentation requestをMainWindowがdialog、focus、selection、scroll、hit-test、drag visual、ContextMenuのWPF propertyへapplyする処理は、feature decisionを持たなければ許可view-host boundaryである。
- WPF event handlerの`async void`は許可する。非event `async void`、feature state、multi-service orchestration、durable write、retry / fallbackだけをblockingとする。
- `Settings.Default`、`Application.Current`、dispatcher、path、process、native / UI technologyは、UI feature workflowをrootへ戻していなければ`MIG-01`〜`MIG-04`へ分類する。
- `UI05-T3`ではplannerを再起動せず、Full verification、UI smoke、outcome review、修正、completionまでを一つのclosure cycleで閉じる。

## 実装・レビュー・commit ループ

1. `PLAN_STATUS.md`のactive batchで最初の`active` unitを実装する。`active`がなく`pending`がある場合は先頭を`active`として扱う。
2. behavior testsと、code変更に必要な正本更新を同じ差分へ含める。
3. `scripts/verify-refactor.ps1 -Mode Quick`または必要なFull verification、format / analyzer / `git diff --check`を実行する。
4. outcome closure候補なら、Full verification、該当UI smoke、completion候補のstatus更新までsnapshotへ含める。
5. single-flightでfresh reviewerへunitまたはoutcome scopeを渡す。review中はsnapshotを凍結する。
6. 重大指摘を修正した場合は影響範囲を再検証し、新しいsnapshotをfresh reviewerへ渡す。
7. 重大指摘がなくなったら、active batchのunit stateと次unit stateを同じsnapshotで更新し、outcome IDを含むmessageでcommitする。
8. **commitは内部checkpointである。** active batchに未完unitがあればplannerを呼ばず直ちに次unitへ進む。
9. batch完了後はanchor exit conditionを確認する。満たせば次anchorへ進む。満たさなければ具体的な未達evidenceを入力にplannerを一度起動する。
10. outcomeが完了したら次Outcomeを`ready` / `in progress`へ進める。ユーザーへの応答は`EXTERNAL_BLOCKER`または依頼された作業範囲の完了時だけとする。

## Outcome / Gate closure

通常Outcomeは、最後のcode unitにFull verification、該当UI smoke、completion state、次Outcome、Gate evidenceを含め、`active outcome base commit..HEAD + frozen worktree`のfresh outcome reviewで一度に閉じる。

`UI05-T1`の有限inventoryで`BLOCKING`が0件となり、T3のFull verification、UI smoke、fresh outcome reviewが無修正で通った場合は、`UI05-T3`に限りcompletion / next-outcome stateだけのaudit/status commitを許可する。完了監査のための無意味なproduction変更を行わない。

同様に、完了条件がcode変更なしで満たされる`MIG-05`、`GATE-01`、承認済みplan rebaseline auditはaudit/status commitを許可する。

`MIG-05`はclean HEADからtemporary worktreeまたはrepository外のcopyを作り、最小TFM変更で`net10.0-windows` restore / buildを試す。probe差分、artifact、full logはproduction worktreeへ戻さずcommitしない。failureはblocker IDごとにpackage / API / TFM / runtime layout / deployment / data migration / unresolved ownershipへ分類する。

`GATE-01`だけは完了時に`gate met`とし、active outcomeを`none`にする。Release Freezeはユーザーの明示release指示まで継続する。

## Outcome state 遷移

- `not started` → `ready`: prerequisiteと依存条件が満たされた。
- `ready` → `in progress`: active outcomeとして開始し、clean commitを`active outcome base commit`に記録した。
- `ready` / `in progress` → `blocked`: ユーザー入力または外部状態変更なしには解消できない具体的な`EXTERNAL_BLOCKER`がある場合だけ。
- `in progress` → `completed`: acceptance criteria、Full verification、該当UI smoke、fresh outcome reviewが完了した。
- `completed` → `in progress`: 後続auditで、そのOutcome固有のacceptance criteriaに直接regressionが見つかり、後続reconciliation Outcomeでは扱えない場合だけ。
- `GATE-01 in progress` → `gate met`: 全Gate criteriaとquality evidenceが完了した。

内部調査、複数caller、broad route、変更量、structural size trigger超過は`blocked`理由にしない。

## 標準検証

PowerShell 7を使用する。

```powershell
pwsh -File .\scripts\verify-refactor.ps1 -Mode Quick
pwsh -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<関連 test filter>'
pwsh -File .\scripts\verify-refactor.ps1 -Mode Full
```

`Quick`はbuild、tests、whitespace、diff check、`Full`はrestore、tool restore、Roslynatorを加える。共有model、root ViewModel、DB、file system、settings、dispatcher、lock / concurrencyに触れた場合とOutcome完了時は`Full`を使う。

build / format / analyzerは完了まで待つ。testは標準入口から起動し、300秒以内に終了しなければprocess treeを停止して失敗とする。timeoutまたはtest failureはcommand outputと利用可能なartifactを確認して修正し、同scopeを再実行する。「baseline」「flaky」として放置しない。全体実行だけ失敗するtestは共有状態、順序、非同期完了、dispatcher、時刻、file / DB isolationを修正して全体実行を再確認する。

scriptが環境要因で使えない場合だけ個別commandを使い、未実施項目を明示する。

```powershell
dotnet build .\BeMusicSeeker.sln /p:Configuration=Release
dotnet test .\BeMusicSeeker.sln /p:Configuration=Release
dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal
$msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -version "[17.0,18.0)" -products * -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin"
dotnet roslynator analyze .\BeMusicSeeker.sln --msbuild-path $msbuildPath --properties Configuration=Release --severity-level warning --verbosity minimal
git diff --check
```

planning / operation docs-onlyはTOML構文、Markdown参照、相互整合、UTF-8 / whitespace、`git diff --check`だけを確認する。

### UI smoke

UI observable behaviorに触れるOutcomeの完了時は、repositoryのRelease buildだけを使う。

```text
<repo-root>\bin\x64\Release\net472\BeMusicSeeker.exe
```

executable pathが上記resolved pathと完全一致するprocessだけを操作する。インストール版や同名windowへ切り替えない。startup / shutdown、主要表示切替、sort / filter / selection / context action / drag-drop、settings、playback、progress / cancel / failure presentationのうち変更範囲を確認する。外部assetや資格情報が必要な項目だけmanual evidenceを依頼する。

## Review scope

- **unit review**: unit開始時のclean commitをbaseとし、`base..HEAD + frozen worktree`を評価する。
- **outcome review**: `active outcome base commit..HEAD + frozen worktree`と現行production codeを評価する。
- **gate review**: `code baseline commit..HEAD + frozen worktree`、現行code、Outcome state、Gate criteria、blocker registerを評価する。

reviewerは`git status --short`、tracked / staged diff、untracked filesを自ら列挙する。重大度順にfile / line付きで返し、問題がなければ「重大な指摘なし」と返す。structural triggerやevent数だけをfindingにせず、owner、dependency direction、testability、behavior contractで判定する。

## 計画資料の更新

activeな計画資料は次の4ファイルに限定する。

- `BeMusicSeekerリファクタリング計画.md`
- `00_Codex共通実行ルール.md`
- `PLAN_STATUS.md`
- `DOTNET10_MIGRATION_BLOCKERS.md`

`PLAN_STATUS.md`はbaseline、active outcome、execution package、execution anchor、active batch、Outcome states、current Gate evidence、external blockerだけを持つ。過去anchor、完了batch、テスト件数、行数推移、調査logを追記しない。

active batchのmaterializationとstate更新はcode unitの同じcommitへ含める。status-only progress commit、cursor-only commit、planner結果だけのcommitを作らない。

## 自走と escalation

Codexはactive outcomeの範囲でplanning、実装、test、review、commitを連続して行う。planner batch、unit commit、review完了はユーザーへの応答理由にしない。

ユーザーへ確認するのは次の場合だけである。

- UI observable behavior、失敗契約、persisted data、supported external contractの意味を変える必要がある。
- target architectureまたはordered backlogを実質的に変える、相互排他的で後戻り困難な選択が必要である。
- credential、外部asset、手動環境、利用不能な必須tool / modelなど、Codexだけでは取得できない外部状態が必要である。

難しい、変更量が大きい、plannerの候補が広い、broad hostやlockがある、追加調査が必要、size triggerを超えることは停止理由にしない。
