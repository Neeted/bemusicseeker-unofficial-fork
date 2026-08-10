# BeMusicSeeker Unofficial Fork

## 基本方針

- このリポジトリは .NET 10 / C# 14 の WPF アプリケーションであり、デコンパイル由来コードを含む。依頼された挙動を守りつつ、変更した範囲では命名、責務、コメント、テスト可能性を改善する。
- 作業開始時に `git status --short` と適用範囲内の `AGENTS.md` を確認し、既存の未コミット差分や無関係なファイルを変更、破棄、整形しない。
- 差分の小ささ自体を目的にしない一方、依頼と無関係な全面整理や将来用抽象化は行わない。
- コミット、push、tag、署名、公開、version 更新は、ユーザーの明示指示または合意済みの作業手順がある場合だけ行う。

## 作業の進め方とサブエージェント

### 要件整理と実装計画

1. ルートエージェントが、ユーザー要件を目的、対象範囲、対象外、受入条件、互換性条件、既知の制約へ整理する。生の会話をそのまま planner へ渡して解釈を委ねない。
2. 永続化、fallback、failure contract、ownership、互換性など、選択によって observable behavior が変わる事項を decision list として明示する。repository の正本や既に合意した方針から一意に決まらない場合は、実装を始めずユーザー判断を得る。
3. コード、設定、スクリプトを実装する前に、`.codex/agents/unit-planner.toml` の `unit-planner` を呼び出す。現在の worktree、整理済み要件、decision list、関連する既知情報を渡し、実行可能な有限の計画を得る。
4. planner が `NEEDS_DECISION` を返した場合は、安全そうな部分だけを先行実装せず、示された未決事項を解消してから新しい計画を得る。
5. 3つを超える独立 subsystem にまたがる、またはおおむね 15 files を超える見込みの変更は、同じ受入条件へ段階的に到達する reviewable unit へ分割する。数値は停止の絶対条件ではなく、単一 snapshot の責務と検証範囲が広すぎないか判断するための signal とする。
6. feature 固有の現行仕様は `devdocs\spec`、背景・判断履歴は `devdocs\decisions`、一時的な計画は `devdocs\plan` に置く。個別機能仕様を `AGENTS.md` や汎用 agent 設定へ混ぜない。
7. `unit-planner` の実行中はルートエージェントを凍結する。repository の読み取り、検索、編集、build、test、format、stage、commit を行わず、応答を待つ。
8. planner の前提と実コードに差異が見つかった場合は、その差異だけを返して計画を補正する。同じ範囲を別 planner やルート側の全面調査で重複させない。

### その他の調査と並列作業

- planner 作成と直接関係しない調査、仕様確認、履歴調査、独立した技術観点には、必要に応じて別のサブエージェントを使ってよい。
- ルートエージェントとサブエージェントは、範囲、観点、参照対象、書込み対象が重ならない場合に限り並列で作業できる。重複調査や同一ファイルへの同時書込みは行わない。
- ルートエージェントが統合責任を持つ。書込みを委譲する場合は対象ファイルを明示し、重複しない単位に限定する。調査だけなら read-only を優先する。

### 実装後レビュー

1. 実装と標準検証を終えたら、`.codex/agents/repo-static-review.toml` の `repo-static-review` を呼び出し、凍結した snapshot をレビューさせる。
2. reviewer の実行中はルートエージェントを凍結し、repository の読み取り、検索、編集、build、test、format、stage、commit を行わない。
3. 初回 reviewer には対象 unit の intent、受入条件、base / head、検証結果を渡す。finding は P0 / P1、受入条件へ直接反する P2、pre-existing / out-of-scope、non-blocking recommendation を区別させる。P0 / P1 と直接反する P2 は修正対象とし、単なる改善提案を同じ変更へ無制限に取り込まない。
4. 指摘を修正した場合は影響範囲を再検証し、変更後の snapshot を fresh reviewer へ渡す。fresh reviewer には前回確認済み snapshot、修正差分、前回 finding を明示し、修正とそこから直接影響する invariant を主対象にさせる。
5. 同じ unit で2回の修正 review を完了した後も新しい P1 が続く場合は、指摘を順次継ぎ足さず、ownership、scope、受入条件、unit 分割を再計画する。新しい P0 / P1 を無視するための回数制限にはしない。
6. pre-existing / out-of-scope の問題は影響と根拠を記録し、現在の受入条件を阻害する場合だけ scope 変更をユーザーへ提示する。現在の変更で生じた問題として扱わない。
7. planner / reviewer が利用できない環境では、同じ read-only 契約を明示した汎用サブエージェントを代替にし、省略したことにしない。

## ドキュメント配置

- `docs\` は利用者向け資料の正本とする。README の補足、導入・操作手順、画面説明、公開時に利用者が読む資料を置く。内部実装契約、開発手順、設計判断履歴の正本は置かない。
- `devdocs\` は開発者・保守者向け資料の正本とする。
  - `devdocs\spec\`: 現在の実装が満たす現行仕様、契約、受入条件、テスト戦略。
  - `devdocs\decisions\`: ADR、採用理由、検討した代替案、判断履歴。
  - `devdocs\plan\`: 未完了の移行・作業計画。完了後は現行仕様へ統合するか、削除または履歴として整理する。
- 設計判断が現行の実装契約になった場合、正本を `devdocs\spec\` に統合し、旧配置には移動案内だけを残す。内容を複数箇所で重複管理しない。
- 仕様を変えるコード変更では、対応する `devdocs\spec\` とテストを同じ変更で更新する。

## アーキテクチャ上の注意

- `MainWindow` / root ViewModel は shell と composition を担当する。feature state、domain decision、永続化、複数 service の順序制御は、既存の feature ViewModel、owner、service、gateway に置く。
- code-behind には focus、selection、scroll、hit-test、drag、WPF routed event など View 固有の terminal behavior を置いてよい。View 固有処理を隠すだけの forwarding class は作らない。
- owner 間は明示的な依存と immutable request / result / event で接続する。mutable collection、lock、private state を列挙する broad host、service locator、巨大 callback interface を追加しない。
- model lock、DB transaction、operation gate を保持したまま UI、dialog、event subscriber、別 owner の完了を同期的に待たない。UI スレッドで sync-over-async を行わず、非 event handler の `async void` を追加しない。
- 既存の owner / gateway / scheduler 境界を迂回して global state や platform API へ直接依存しない。境界を変える場合は挙動、失敗、shutdown、thread affinity をテストする。

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
- source text だけを確認する脆いテストより、observable behavior、永続データ、threading、failure contract を確認するテストを優先する。

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
- 通常のコード変更は、レビュー前に原則一度 `Functional` を行う。`Functional` は command 全体で 180 秒以内を受入条件とし、個別 testhost / shard ごとに時間予算をリセットしない。
- `Full` は publish / updater / distribution、release 手順、または Full runner 自体を変更した場合と release 前に使う。settings、startup、共有 model などの変更だけを理由に、通常機能テストと release acceptance を毎回まとめて実行しない。対象に応じて filtered `Quick`、`Functional`、明示的な opt-in lane を組み合わせる。
- review 修正後は、まず影響範囲の filtered `Quick` を行う。修正が通常機能検証の前提を変えた場合だけ最終 `Functional` を再実行し、release lane を変えた場合だけ `Full` も再実行する。
- timeout 時は process tree を停止し、active または last observed test、経過時間、console progress / TRX / blame artifact を残す。timeout を延長したり同じ run を無制限に再試行したりせず、`artifacts\verification` の出力を確認して原因を直す。
- runner、lane、並列化、fixture 配置を変更した場合は、同一条件の `Functional` を3回連続で実行し、各 command が180秒以内、tracked file が不変、残留 test process がないことを確認する。
- test はマシンの CPU / I/O を安定性が許す範囲で利用し、wall-clock time を短縮する。負荷抑制だけを理由に shard / worker を制限せず、競合で不安定になる場合は共有 state、fixture ownership、固定待ち、process / file / port の競合を修正する。
- flaky test、timeout、または従来より明白に長時間化した test を発見した時点で、本筋を一旦止めて原因を調査する。現在の変更範囲外に見えても放置せず、並列実行、共有 state、固定待ち時間、競合、I/O、fixture / input 量、監視側の timeout 根拠を確認する。
- test の見直しでは、可能なら同期 barrier や決定的な fake で安定化し、不要な固定待ちや過大な入力を削減する。必要な処理量として妥当な長時間 test は、実測と失敗検出能力を根拠に timeout / shard 設計を変更してよい。timeout 延長だけで不安定性を隠さない。
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
2. `BeMusicSeeker\Views\SettingsWindow.xaml`
   - `Update_history` に対象 version の履歴を追加する。
   - 新しい説明文は多言語リソースを追加せず日本語ベタ書きで良い。
3. `release notes\vX.X.X.X リリースノート.md`
   - 対象 version の release notes を作成・更新し、GitHub Release 本文として使える状態にする。
4. `ReleaseScriptVersionSourceTests` と `LocalizationResourceParityTests` を含む関連検証を行い、release 前に `verify-refactor.ps1 -Mode Full` を通す。

`version.txt` と `update.json` は手動編集しない。`scripts\publish.ps1` / `scripts\release.ps1` が `AssemblyInformationalVersion` から生成する。package 作成、draft、tag、push、公開は、ユーザーがその release 操作を明示した場合だけ実行する。
