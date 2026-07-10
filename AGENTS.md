# BeMusicSeeker Unofficial Fork

## 正本と作業範囲

- リファクタリング中は `devdocs/plan/BeMusicSeeker_refactoring_plans/BeMusicSeekerリファクタリング計画.md`、`PLAN_STATUS.md`、`00_Codex共通実行ルール.md` を正本とする。
- `PLAN_STATUS.md` の active outcome を、完了条件を満たすまで連続して進める。細かな seam、helper、DTO、調査資料を独立した成果にしない。
- 差分の小ささではなく、state と behavior が同じ owner に収まり、旧経路を削除できる実装単位を選ぶ。
- DB schema、setting key、serialized value、外部ファイル形式、UI observable behavior、public compatibility API の意味を変える必要がある場合だけ、実装前にユーザーへ確認する。
- 意味の変わる fallback を追加しない。失敗を隠さず、既存の失敗契約を維持する。

## Git と Release Freeze

- active outcome の implementation unit は、検証とサブエージェント静的レビューで重大指摘がなくなった後、ユーザー承認待ちなしで commit してよい。commit subject に outcome ID を含める。
- ユーザーが明示依頼した計画・運用構成の変更も、検証とレビュー後に独立 commit にしてよい。
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

## 検証

- PowerShell 7 と `rg` を使用する。明示的に PowerShell を起動する場合は `pwsh` を使う。
- 標準入口は `scripts/verify-refactor.ps1` とする。unit 中は `-Mode Quick`、outcome 完了候補および共有 ViewModel / DB / settings / dispatcher / concurrency 変更では `-Mode Full` を使う。
- script が環境要因で実行できない場合だけ、`00_Codex共通実行ルール.md` の個別コマンドを実行し、未実施項目を明示する。
- behavior test を優先し、private method 名や一時的な配置を固定する source-text / reflection test を完成後の主要保証にしない。

## サブエージェント

- 調査とレビューにサブエージェントを活用する。root agent だけを writer / stager / committer とし、並列作業は読み取り専用の調査・静的レビューに限定する。
- review 中は対象 snapshot を変更せず、修正した場合は新しい snapshot として再レビューする。
- サブエージェントは原則として履歴を fork しない。
- 静的レビュー担当には編集、build、test、format、analyzer、commit を禁止し、読み取りだけで重大度順に報告させる。
- 実装後は重大指摘がなくなるまで修正と再レビューを行う。レビュー完了後の検証結果を最終結果とする。
