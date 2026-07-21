# BeMusicSeeker Unofficial Fork

## 正本と作業範囲

- リファクタリング中は `devdocs/plan/BeMusicSeeker_refactoring_plans/BeMusicSeekerリファクタリング計画.md`、`PLAN_STATUS.md`、`00_Codex共通実行ルール.md` を正本とする。
- `PLAN_STATUS.md` の active outcome を、完了条件を満たすまで連続して進める。implementation unit の commit は内部 checkpoint であり、ユーザーへの応答境界にしない。細かな seam、helper、DTO、調査資料を独立した成果にしない。
- 差分の小ささではなく、state と behavior が同じ owner に収まり、旧経路を削除できる実装単位を選ぶ。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約、明示的にサポートする SDK / plugin / CLI / IPC / COM / automation contract の意味を変える必要がある場合だけ、実装前にユーザーへ確認する。
- 意味の変わる fallback を追加しない。失敗を隠さず、既存の失敗契約を維持する。

## 互換性契約

- C# の `public` / `protected` 修飾子だけでは互換性契約とみなさない。
- 同一リポジトリ内の production caller、test project、XAML binding、resource lookup、reflection string は内部実装 consumer とする。全 consumer を同じ implementation unit で更新し、build / test / UI smoke check を通す場合は call shape を変更してよい。
- 旧 public member、旧 nested enum、旧 forwarding property を、test または過去の内部 call shape のためだけに残さない。`[Obsolete]` wrapper も追加しない。
- 互換性を理由に escalation する前に、具体的な supported out-of-repository consumer を特定する。特定できなければ内部リファクタリングとして進める。

## Git と Release Freeze

- active outcome の implementation unit は、検証とサブエージェント静的レビューで重大指摘がなくなった後、ユーザー承認待ちなしで commit してよい。commit subject に outcome ID を含める。
- ユーザーが明示依頼した計画・運用資料と agent 構成だけの変更は、適用対象の構文・参照・whitespace・`git diff --check` を確認して独立 commit にしてよい。production / test / build / resource / verification script に差分がなければ build、test、analyzer、code static review は行わない。
- implementation unit の code / test / 関連資料を review 済みで重大指摘がない場合、同じ commit に入れる `PLAN_STATUS.md` の sequence cursor 前進だけを機械的に確認してよい。cursor-only 差分のために build / test や再レビューを追加しない。
- 上記以外の通常作業は、ユーザーの動作確認と明示承認前に commit しない。
- unrelated な既存差分を変更、stage、commit しない。
- Refactoring Completion Gate 前は `git push`、tag、release、publish、version / release notes のリリース目的変更、配布 package 作成を禁止する。Gate 後もユーザーの明示指示なしには開始しない。

## 実装原則

- production の通常経路へ接続し、同じ unit で旧 owner、旧 route、旧 binding、callback host、test-only production seam のいずれかを削除する。
- partial split、interface / adapter / host の追加だけで責務移管を完了扱いにしない。
- active outcome に不可欠な platform / composition contract は、production 経路へ即時接続する最小単位に限り前倒ししてよい。未使用 facade や将来用 abstraction は追加しない。
- C# symbol rename、API/signature refactor、type/member moveでは `.agents/skills/csharp-semantic-refactor/SKILL.md` を使用し、identifier の一括 text replacement を行わない。
- 命名、XML documentation、理由コメントは、変更した責務の理解または非自明な contract / invariant の説明に必要な範囲で改善する。無関係な cleanup を同じ commit に混ぜない。
- `NLog` を直接参照せず、既存の logging boundary を使う。INFO log は lifecycle、boundary、性能計測、異常回復に必要な場合だけ追加する。
- 通常操作で表示する新規 UI 文言は既存の resource / localization 手順に従う。
- 静的レビューでは public modifier の差分自体を指摘事項にせず、supported external contract、persisted data、UI observable behavior、失敗契約の破壊だけを互換性指摘として扱う。

## 検証

- PowerShell 7 と `rg` を使用する。明示的に PowerShell を起動する場合は `pwsh` を使う。
- production / test / build / resource / verification script を変更する implementation unit の標準入口は `scripts/verify-refactor.ps1` とする。unit 中は `-Mode Quick`、outcome 完了候補および共有 ViewModel / DB / settings / dispatcher / concurrency 変更では `-Mode Full` を使う。
- planning / operation Markdown、`AGENTS.md`、`.codex/config.toml`、`.codex/agents/*.toml` だけを変更する場合は、TOML 構文、文書参照、whitespace、`git diff --check` など差分に直接対応する軽量検証を行う。
- review 済み implementation unit の cursor-only 更新は、`PLAN_STATUS.md` の変更が次の cursor 一行に限定されることと `git diff --check` を確認する。
- script が環境要因で実行できない場合だけ、`00_Codex共通実行ルール.md` の個別コマンドを実行し、未実施項目を明示する。
- build / format / analyzer には固定の実行時間制限を設けず、プロセスの完了まで待つ。test は root を拘束する直接実行ではなく、`scripts/verify-refactor.ps1` が Windows Job Object 内で起動する監視付き test process として実行する。開始から 180 秒で未完なら CPU 時間・memory を含む process tree snapshot を保存して調査を開始し、正当な test 分量として観測中の実行は継続する。現行 Full suite の実測に基づく異常閾値は 300 秒とし、到達時は testhost を停止して blame sequence を確定する。診断開始後も runner が戻らない場合と診断処理自体が失敗した場合はjob全体を停止し、active memberが0になるまで確認する。300 秒到達後に runner が終了コード 0 を返しても検証は失敗とする。120 秒 timeout は使用しない。
- Codex から検証を起動するときは、実行プロセスを継続したまま 10 秒以内に制御が戻る resumable execution cell を使い、終了まで 60 秒以内の間隔でポーリングする。一つの同期 tool call で完了まで待たない。resumable execution cell を使えない場合は、標準出力と終了状態を artifact へ保存する background process として起動し、60 秒以内の間隔で監視する。このポーリングは test process の timeout ではなく、Codex が 180 秒到達時に観測結果を回収して調査へ移るための制御手順とする。
- 180 秒の観測で進捗停止または異常な resource state が見つかった test と、300 秒で診断へ移った test は失敗として扱う。保存した sequence と process snapshot から実行中 test、process、dispatcher / scheduler / async wait、共有状態、I/O、重複・無駄な test work を特定し、原因を修正してから同じ test scope を再実行する。active unit と直接関係しない test でも対象とし、修正は発見した implementation unit の差分と commit に含める。
- 300 秒の診断段階で process 列挙、diagnostic artifact 保存、testhost 停止のいずれかが失敗した場合は診断成功として扱わず、monitored jobを停止してactive memberが0になったことを確認し、診断開始失敗を明示する。330 秒fallbackを含む全診断経路でblame sequenceの有無を共通確認する。180 秒の観測snapshotだけが失敗した場合は警告を記録し、60秒以内のpollingでprocess状態の確認を継続する。
- 異常閾値の超過が hang、isolation 不良、無駄な待機ではなく、必要な test 件数・処理量による正当な所要時間だと command output と test breakdown で確認できた場合は、実測根拠に基づいて閾値を見直す。個別実行の成功や単なる再実行だけで閾値を延長しない。
- UI smoke の対象は、Release build が完了したリポジトリ内の `bin\x64\Release\net472\BeMusicSeeker.exe` だけとする。インストール版の executable path を取得・列挙・起動してはならない。起動後は対象 process の executable path がこの resolved path と一致することを確認し、一致を確認できない場合は UI 操作を行わず smoke 未実施として扱う。
- targeted / Full test で失敗を検出した場合、直接の変更箇所と無関係に見えても「既知」「baseline」「flaky」として放置しない。コンテキスト圧縮前の変更で発生した可能性を前提に、Git 履歴と現行実装を確認し、実装または期待値を修正して最終差分で再検証する。
- 全体実行で失敗し個別実行で成功する test は、成功した個別再実行を根拠に合格扱いしない。共有状態、実行順、非同期完了条件、dispatcher / scheduler、時刻・ファイル・DB isolation を調査し、observable completion を待つ、専用状態へ隔離するなど test 構造を改善してから全体実行を再確認する。
- behavior test を優先し、private method 名や一時的な配置を固定する source-text / reflection test を完成後の主要保証にしない。

## サブエージェント

- planner と reviewer は single-flight で使い、同時に active にするサブエージェントは 1 つだけとする。
- planner または reviewer を起動してから結果を受け取るまで、root agent は repository の読み取り調査、検索、編集、build、test、format、analyzer、stage、commit を凍結する。root による「独立調査」、第 2 planner、consensus 取得、同じ scope の並列読み直しを行わない。
- planner は active execution package を実装可能な順序列へ分解する責任を持つ。複数 caller、broad host、lock ordering、DB / live atomicity、後続 owner の residual が絡むことを停止理由にせず、責務 corridor ごとに再分解する。
- root agent だけを writer / stager / committer とする。planner の結果後は、指定された route / symbol / test に対する bounded feasibility check と実装に進み、別の architecture survey をやり直さない。
- code / test / build / resource の変更と、それに伴う資料更新を unit / outcome / gate review の対象とする。planning / operation docs-only と、review 後の cursor-only 前進は通常の code review 対象にしない。
- reviewer は frozen worktree の `git status`、tracked / staged diff、untracked file を read-only で自ら列挙して review scope を確定する。親から untracked file 一覧を渡すことを開始条件にしない。
- review 中は対象 snapshot を変更せず、重大指摘の修正後は新しい snapshot として fresh reviewer に再レビューする。サブエージェントは履歴を fork しない。
- 静的レビュー担当には編集、build、test、format、analyzer、commit を禁止し、指定 scope の読み取りだけで重大度順に報告させる。
- 実装後は重大指摘がなくなるまで修正と再レビューを行う。review 後に code / test / reviewed docs を修正した場合だけ影響範囲の検証を再実行し、cursor-only 前進はその結果を無効にしない。
