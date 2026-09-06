# BeMusicSeeker Unofficial Fork

## 基本方針

- このリポジトリは .NET 10 / C# 14 の WPF アプリケーション。依頼された挙動を守りつつ、変更した範囲では命名、責務、コメント、テスト可能性を改善する。
- 作業開始時に `git status --short` と適用範囲内の `AGENTS.md` を確認し、既存の未コミット差分や無関係なファイルを変更、破棄、整形しない。
- 差分の小ささ自体を目的にしない一方、承認済みで実際に到達可能な observable behavior に不要な persistent state、retry / replay / rollback、compatibility route、抽象化や、依頼と無関係な全面整理は追加しない。
- コミット、push、tag、署名、公開、version 更新は、ユーザーの明示指示または合意済みの作業手順がある場合だけ行う。

## 作業の進め方とサブエージェント

複数段階の変更、実装の委譲、並列 worker、static review を伴う作業では、`devdocs\spec\codex-agent-workflow.md` を運用の正本として先に確認する。単純な質問や軽微な文書修正まで機械的にサブエージェントへ渡さない。

### durable な運用契約

- ルートはユーザー要件を Goal、Context、Constraints、Done when、対象外、互換性条件、decision list、unit ごとの ownership と verification へ整理し、設計・最終計画・統合に責任を持つ。生の会話や未決 semantics を worker へ委ねない。
- runtime state、failure、invariant を対象にする前に、workflow の reachability / impact gate に従い、canonical production ingress から production owner までの route、入口 assumption、user-observable または durable / external-data impact を立証する。private API、reflection、fake、code representability だけを根拠にしない。
- 実装委譲前に `plan-clarifier` を原則一度だけ使い、repo で解けた事実、必要な質問、test-design gate、並列境界、replan trigger を計画へ反映する。先に `devdocs/spec/test-authoring-contract.md` に従って変更分類と恒久テストの必要性を判断し、workflow section 3 の適用対象だけで `test-contract-designer` と承認済み `Test Contract Packet` を使う。テスト不要・代替不要の削除のみ・assertion semantics 不変の機械的変更には packet を要求しない。
- bounded implementation は `implementation-worker` へ任せる。書込み worker の既定は1つ、writable path、生成物、schema / migration、shared fixture、依存順が重ならない独立した unit に限り同時実行は最大2つとし、同じ unit の比較、shadow 評価、二重実装を行わない。
- worker は指定 path と unit だけを変更し、UI、persisted data、file / protocol compatibility、threading、shutdown、failure invariant と packet semantics を維持する。worker が起動できる nested agent は `issue-resolver` だけで、対象 blocker に限り一度、編集を止めて呼び、resolver から再帰しない。commit 等は禁止する。
- worker の開始確認、packet 不足時の `NEEDS_ROOT_INPUT`、red / negative-control、filtered Quick、verification failure の分類、完了時の `IMPLEMENTED` / `FILES` / `TEST CONTRACT` / `TEST COVERAGE` / `TEST SAFETY` / `VERIFICATION` / `HANDOFF` は workflow の共通契約に従う。root は handoff、path / Contract ID ownership、旧 route の退役を統合時に確認する。
- 実装と標準検証後は implementation agent を閉じ、凍結 snapshot を `repo-static-review` へ渡す。review 中は root の read / search / edit / build / test / format / stage / commit と重複 review を停止する。blocking finding は classification、authority、reachability、assumption、observable impact、evidence を揃え、修正後は影響範囲を検証して fresh review を行う。2回の修正 review 後も新しい P1 が続けば、finding を継ぎ足さず再計画する。
- custom agent が利用できない場合は、同じ model、permission、role contract を明示した built-in / generic agent へ代替する。multi-agent 機能自体が使えない場合も、planning、独立 oracle、implementation、verification、fresh review の境界を順番に再現し、代替と未実施 evidence を明記する。
- 各 role の model / reasoning effort の正本は `.codex/agents/*.toml` の設定 field、`/review` の model の正本は `.codex/config.toml` とする。具体的なモデル名をこの文書、workflow、description へ重複記載しない。

## ドキュメント配置

- `docs\` は利用者向け資料の正本とする。README の補足、導入・操作手順、画面説明、公開時に利用者が読む資料を置く。内部実装契約、開発手順、設計判断履歴の正本は置かない。
- `devdocs\` は開発者・保守者向け資料の正本とする。
  - `devdocs\spec\`: 現在の実装が満たす現行仕様、契約、受入条件、テスト戦略。
  - `devdocs\decisions\`: ADR、採用理由、検討した代替案、判断履歴。
  - `devdocs\plan\`: 実行中の移行・作業計画と、固有の検証証跡を残す完了記録。完了後の恒久契約は現行仕様へ統合し、重複する本文は削除または履歴として整理する。
- 設計判断が現行の実装契約になった場合、正本を `devdocs\spec\` に統合し、旧配置には移動案内だけを残す。内容を複数箇所で重複管理しない。
- 仕様を変えるコード変更では、対応する `devdocs\spec\` を更新する。テストは目的と影響から恒久的な保証の必要性を判断し、`変更なし`、必要な既存テストの更新、追加、削除を選ぶ。

## アーキテクチャ上の注意

- `MainWindow` / root ViewModel は shell と composition を担当する。feature state、domain decision、永続化、複数 service の順序制御は、既存の feature ViewModel、owner、service、gateway に置く。
- code-behind には focus、selection、scroll、hit-test、drag、WPF routed event など View 固有の terminal behavior を置いてよい。View 固有処理を隠すだけの forwarding class は作らない。
- owner 間は明示的な依存と immutable request / result / event で接続する。mutable collection、lock、private state を列挙する broad host、service locator、巨大 callback interface を追加しない。
- model lock、DB transaction、operation gate を保持したまま UI、dialog、event subscriber、別 owner の完了を同期的に待たない。UI スレッドで sync-over-async を行わず、非 event handler の `async void` を追加しない。
- 既存の owner / gateway / scheduler 境界を迂回して global state や platform API へ直接依存しない。境界を変える場合は、必要な既存テストの更新・追加または適切な実行確認で挙動、失敗、shutdown、thread affinity を確認する。

### 非同期ワークフローと並行性

- scheduler、background task、owner間callback、snapshot、version / generation token、mutation laneを変更する場合は、`devdocs\spec\workflow-concurrency-and-complexity.md`を先に確認する。
- UI responsiveness だけから mutation concurrency を推測しない。対象 feature spec と承認済み decision がない競合 mutation は、UI を応答可能に保つ直列化案または replan とする。

## ログ

- production code から NLog を直接構成・取得・呼び出さない。logger の取得と出力は `Ribbit\Logging\NLogWrapper.cs` を経由する。
- 通常ログは `NLogWrapper.FileLogger` 等を使い、名前付き channel は `NLogWrapper.GetLogger(name)` を使う。`LogManager`、target、rule の直接操作は `NLogWrapper` 実装内に限定する。
- ログには原因調査に必要な文脈を含めるが、UI thread の hot path や per-item loop に無制限の文字列生成・同期 I/O を追加しない。機密情報や不要な個人データを出力しない。

## 多言語リソース

- ダイアログ、メニュー、ボタン、設定、エラー、通常ステータスなど、ユーザーが目にする新しい文字列は必ず多言語リソース化する。`.cs` / `.xaml` へ新規の固定文言を直接追加しない。
- resource key を追加・削除する場合は、次を同じ変更で揃える。
  - `BeMusicSeeker\Properties\Resources.resx`
  - `BeMusicSeeker\Properties\Resources.cs`
  - `lang\en-US.json`, `fr-FR.json`, `ja-JP.json`, `ko-KR.json`, `zh-CN.json`, `zh-TW.json`
- 全言語へ意味のある値を追加し、空文字や一時的な placeholder を残さない。`LocalizationResourceParityTests` を更新・実行する。
- ログ、開発者向け診断、性能 marker、テスト専用文字列、内部 protocol 名は UI リソース化の対象外としてよい。

## 変更時の保守性

- 新規または変更する public / protected / internal API には、契約と存在理由が分かる XML documentation を追加・更新する。
- 触れた範囲のデコンパイル由来名は、挙動を変えずに domain 用語へ改善する。ただし命名だけの広範な差分を混ぜない。
- 互換性維持、性能最適化、外部仕様、回避策など、コードだけでは理由が分からない箇所には「何をしているか」ではなく「なぜ必要か」をコメントする。
- test を追加・変更・削除するときは、目的と影響から恒久テストの必要性を先に判断し、必要な assertion semantics の追加・変更・置換だけを `BeMusicSeeker.Tests\AGENTS.md` と `devdocs\spec\test-authoring-contract.md` に従って行う。必要と判断した場合は承認済み `Test Contract Packet`、近傍の既存 coverage、共通 infrastructure を確認する。expected value を current implementation、current output、翻訳文言、既存 snapshot、repository prose からコピーしない。source / exact copy 自体が明示された contract でない限り、observable behavior、永続データ、threading、failure contract、placeholder / schema parity を検証する。テスト不要の判断は検証不要を意味しない。

## 標準検証

テスト lane、時間予算、並列化、固定待ち、opt-in fixture の正本は `devdocs\spec\testing-strategy.md` とする。PowerShell 7 から `scripts\verify-refactor.ps1` を標準入口として使う。

```powershell
# 反復中の関連テスト
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<MSTest filter>'

# 通常の全体確認
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional

# restore、tool、analyzer、publish / update acceptance を含む高リスク確認
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

- 実装中は関連 test filter の `Quick` を優先し、小さな修正ごとに full suite を繰り返さない。
- 最終 acceptance の lane、Functional の実行回数、300秒 hard budget、180秒 reporting target、timeout retry、failure classification は `devdocs\spec\testing-strategy.md` に従う。通常のコード変更では、最終 snapshot の `Functional` を原則一回実行し、180秒を超えて成功した場合は actual elapsed をユーザーへの報告に含める。
- `Full` は publish / updater / distribution、release 手順、Full runner の変更、release 前の受入に使う。その他の変更は filtered `Quick`、`Functional`、必要な opt-in lane を組み合わせる。
- review 修正後は影響範囲の filtered `Quick` を先に行い、通常機能検証または release lane の前提が変わった場合だけ該当する統合 lane を再実行する。
- deterministic failure や再発する flaky / 長時間化は、共有 state、fixture ownership、待機、競合、I/O、input 量、timeout 根拠を調査する。timeout 延長や worker / shard 低下だけで症状を隠さない。
- test-only の修正で閉じる場合は、現在の unit と同じ invariant を検証するものなら同じ commit、横断的または既存の test infrastructure 問題なら独立 commit とする。修正と該当 test の検証後、本筋へ戻る。
- script が環境上利用できない場合だけ個別 command へ分解し、未実施項目と理由を明示する。標準入口を黙って省略しない。
- prose / Markdown / TOML だけの変更では、構文、参照、UTF-8 / LF、whitespace、`git diff --check` を確認する。build 手順や agent behavior を変える設定変更は、必要な追加検証も行う。

## UI確認と computer use

- UI 確認では、computer use のアプリ検索や起動操作を使わない。同名のインストール版が優先されるため、先に PowerShell 等で repository 内の正確な executable path を指定して起動する。

```powershell
$exe = (Resolve-Path .\bin\x64\Release\net10.0-windows\BeMusicSeeker.exe).Path
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
```

- publish artifact を確認する場合は、その artifact の絶対 path を同じ方法で起動する。computer use は起動済みの対象 process / window の操作だけに使い、可能なら process path が期待値と一致することを確認する。
- ユーザー操作によって computer use が中断された場合は、最後の安全な地点から操作を再取得してリトライする。一度の中断を理由に作業全体を終了しない。ただし、ユーザーが要件を変更した場合は新しい指示を優先する。
- UI確認後は対象アプリを閉じ、computer use の session も終了する。残留 process を放置しない。

## バージョン更新とリリース

バージョン更新の依頼を受けた場合は、次を同じ変更で揃える。

1. `Properties\AssemblyInfo.cs`
   - `AssemblyInformationalVersion` を更新する。package 名、tag、`update.json`、公開用 `version.txt` の正本である。
   - `AssemblyVersion` は互換性上の理由または明示指示がない限り変更しない。
2. `BeMusicSeeker\Views\ReleaseNotesWindow.xaml`
   - `Update_history` に対象 version の履歴を追加する。
   - 新しい説明文は多言語リソースを追加せず日本語ベタ書きで良い。
3. `release notes\vX.X.X.X リリースノート.md`
   - 対象 version の release notes を作成・更新し、GitHub Release 本文として使える状態にする。
4. `ReleaseScriptVersionSourceTests` と `LocalizationResourceParityTests` を含む関連検証を行い、release 前に `verify-refactor.ps1 -Mode Full` を通す。

`version.txt` と `update.json` は手動編集しない。`scripts\publish.ps1` / `scripts\release.ps1` が `AssemblyInformationalVersion` から生成する。package 作成、draft、tag、push、公開は、ユーザーがその release 操作を明示した場合だけ実行する。
