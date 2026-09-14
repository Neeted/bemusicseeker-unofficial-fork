# BeMusicSeeker Unofficial Fork

## 基本方針

- このリポジトリは .NET 10 / C# 14 の WPF アプリケーション。依頼された挙動を守りつつ、変更した範囲では命名、責務、コメント、テスト可能性を改善する。
- 作業開始時に `git status --short` と適用範囲内の `AGENTS.md` を確認し、既存の未コミット差分や無関係なファイルを変更、破棄、整形しない。
- 差分の小ささ自体を目的にしない一方、承認済みで実際に到達可能な observable behavior に不要な persistent state、retry / replay / rollback、compatibility route、抽象化や、依頼と無関係な全面整理は追加しない。
- コミット、push、tag、署名、公開、version 更新は、ユーザーの明示指示または合意済みの作業手順がある場合だけ行う。

## 性能とデータ規模

- 性能上の最優先は、同じ仕事を正しく完了するまでの処理速度（wall-clock time / throughput）とする。省メモリ性や、UI 応答のための CPU 使用量抑制を優先しない。速度向上に有効な cache 保持・一括処理・操作内の独立計算の並列化を許容するが、データ整合性、ownership、取消、shutdown、WPF thread affinity は維持する。
- 通常の設計前提は約21万譜面、約3万譜面フォルダ、800万規模の resource reverse lookup key、複数譜面・数百 resource を含み得る package とする。これは入力上限ではない。実ファイル数、directory 別 resource entry、reverse key、DB row を同じ件数として扱わない。
- DB query、cache / snapshot / receipt、index、package loop、publication、scheduler / 並列度を変更する場合は、[データ規模と性能要件](devdocs/spec/performance-and-scale.md) を先に読み、全体件数と操作差分、全件処理の呼出回数、再利用・失効範囲、必要な逐次境界を計画とレビューに含める。exact 規模と参照記録は同 spec を正本にする。
- 利用者が一回のライブラリ変更操作で複数の譜面・フォルダ・パッケージを変更する場合は、原則 `1 user operation = 1 mutation session / N changes` とする。対象ごとの判定や filesystem I/O が逐次でも、canonical DB apply、索引・cache反映、required publication を item ごとに完結させない。後続対象の判断が先行成功に依存する場合は operation-local な成功 facts / overlay で依存を満たし、例外が定義されていることだけを理由に per-item durable commit、rollback、全件再構築を正常系の既定にしない。詳細は [ライブラリ変更境界](devdocs/spec/library-mutation-boundary.md#mutation-session-契約) と [FS/DB整合](devdocs/spec/file-db-consistency.md) を正本にする。
- 少数 install / delete の内側で全 catalog / 巨大 dictionary を反復走査・コピー・sort する設計や、no-op 判定前の全 root コピーを既定にしない。immutable な契約は全件複製を要求しない。全件処理が必要な例外は理由と実測を示す。
- 性能は同条件・同じ完了範囲で操作別に比較する。別操作の短縮、低メモリ、低 CPU、first-visible だけの短縮で処理完了の退行を相殺しない。Functional 成功や小規模 corpus だけで大規模性能を検証済みとしない。未測定は明示する。

## 作業の進め方とサブエージェント

複数段階の変更、実装の委譲、並列 worker、static review を伴う作業では、`devdocs\spec\codex-agent-workflow.md` を運用の正本として先に確認する。単純な質問や軽微な文書修正まで機械的にサブエージェントへ渡さない。

### 責務と判断

- ルートは目的、制約、完了条件、決定事項、作業単位ごとの担当範囲と検証方法を整理し、設計・最終計画・統合に責任を持つ。委譲前に、実装担当が推測せず着手できる状態まで判断を閉じる。
- 実行時の状態や失敗を扱う場合は、運用契約 section 1 の到達可能性と影響の確認に従い、本番の入口から管理主体までの経路、入口の前提、利用者・永続データ・外部データへの影響を示す。
- 恒久テストの必要性は `devdocs/spec/test-authoring-contract.md` に従って先に判断する。計画点検、必要な独立テスト設計、実装担当への割当、障害解決、統合検証、凍結した変更への静的レビューは運用契約の順序と役割分担に従う。
- 担当範囲、並列度、入力不足、完了時の引継ぎ、再計画、エージェントを利用できない場合の代替も運用契約を正本とする。統合時には担当の重複、承認済みのテスト設計との一致、旧処理の退役を確認する。
- 各役割のモデルと推論強度は `.codex/agents/*.toml` の設定値、`/review` のモデルは `.codex/config.toml` を正本とする。

## ドキュメント配置

- `docs/` は利用者向け資料、`devdocs/` は開発者・保守者向け資料の正本とする。配置と日本語・用語の書き方は [開発資料の案内](devdocs/README.md) に従う。
- 現行仕様は [仕様書の書式](devdocs/spec/README.md#仕様書の書式) に従い、仕様項目ごとに実装とテストの対応を示す。仕様、対応するコード、テストの変更に合わせて更新する。
- 計画は [計画の運用](devdocs/plan/README.md#運用) に従い、現在の進捗と再開に必要な判断を示す。完了時は恒久的な契約を仕様へ統合し、実施内容の要約、残課題、反映先を残す。

## アーキテクチャ上の注意

- `MainWindow` / root ViewModel は shell と composition を担当する。feature state、domain decision、永続化、複数 service の順序制御は、既存の feature ViewModel、owner、service、gateway に置く。
- code-behind には focus、selection、scroll、hit-test、drag、WPF routed event など View 固有の terminal behavior を置いてよい。View 固有処理を隠すだけの forwarding class は作らない。
- owner 間は明示的な依存と immutable request / result / event で接続する。mutable collection、lock、private state を列挙する broad host、service locator、巨大 callback interface を追加しない。
- model lock、DB transaction、operation gate を保持したまま UI、dialog、event subscriber、別 owner の完了を同期的に待たない。UI スレッドで sync-over-async を行わず、非 event handler の `async void` を追加しない。
- 既存の owner / gateway / scheduler 境界を迂回して global state や platform API へ直接依存しない。境界を変える場合は、必要な既存テストの更新・追加または適切な実行確認で挙動、失敗、shutdown、thread affinity を確認する。

### 非同期ワークフローと並行性

- scheduler、background task、owner間callback、snapshot、version / generation token、mutation laneを変更する場合は、`devdocs\spec\workflow-concurrency-and-complexity.md`を先に確認する。
- UI responsiveness だけから mutation concurrency や後続 queue を推測しない。未承認の競合する新規変更要求は、実行 owner の入口で非待機の Busy 拒否を既定とする。受理済み処理と未受理要求を区別し、必要な終端まで論理的な操作 ownership を保持する。
- 受付の例外と維持すべき機能は上記共通 spec の section 6 に従う。追加 ZIP の導入予約、保留への追加に伴う自動推定、設定画面の利用、通信待ち中に現在許可されるライブラリ操作を、一律の global Busy 化で失わせない。確定済み表示の閲覧を維持し、その表示から変更へ進むときは受付後に現在の対象へ解決する。

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
