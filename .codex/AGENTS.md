# BeMusicSeeker Unofficial Fork

---

## 0. 基本原則（デコンパイル由来コード）

本プロジェクトはデコンパイル由来コードを含む。  
**保守性の改善はすべての変更作業に含まれる必須事項である。**

コードを修正・追加する場合は、機能変更だけでなく以下も同時に行うこと：

- 意味の失われたローカル変数名・引数名の改善
- XMLドキュメントコメントの追加・更新
- 難解箇所への「理由コメント」の追記

外部仕様を変更しない限り、動作の振る舞いは変えてはならない。

---

## 1. コミット戦略

- 通常作業で機能を実装・修正した場合、**絶対にユーザーの承認を得る前に自動で `git commit` を実行してはならない**。
- 通常作業では、必ずユーザーに動作確認を依頼し、OKの回答を得た後にコミットのコマンドを提案し、承認を得てから実行（またはユーザー自身に実行）してもらうこと。
- 例外: `devdocs/plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md` の active outcome 作業では、ユーザーが implementation unit ごとの commit を事前許可している。Codex は unit 完了後、標準確認とサブエージェント静的レビューで重大指摘がないことを確認してから、ユーザー承認待ちなしで `git commit` してよい。
- active outcome の implementation commit message には outcome ID を含める。ユーザーが明示依頼した計画自体の変更はこの限りではない。
  - 例: `refactor(ui): UI-01 move chart-list terminal apply ownership`
  - 例: `refactor(app): APP-01 route settings save through configuration owner`
  - 例: `refactor(domain): LIB-01 move scan commit into the initialization pipeline`
- **注意**: 本プロジェクト（ブランチ）はリモート未登録のため、コミット後の `git push` は不要（実行不可）である。
- `git push`、GitHub Release、tag 作成、release draft 作成、publish / release script 実行は、ユーザーから明示依頼があっても Refactoring Completion Gate 通過前は禁止する。

---

## 1.1 Refactoring Completion Release Freeze

[リファクタリング完了計画](../devdocs/plan/BeMusicSeeker_refactoring_plans/BeMusicSeekerリファクタリング計画.md)の Refactoring Completion Gate 通過まで、リリース作業を凍結する。Release Freeze は `## 5. バージョン更新作業時の手順` より優先する。Gate 通過後も、ユーザーの明示指示なしにリリース作業へ進まない。

禁止:

- `Properties/AssemblyInfo.cs` の `AssemblyInformationalVersion` 更新
- `release notes/` の新規リリース向け更新
- `version.txt` のリリース目的更新
- `scripts/publish.ps1` / `scripts/release.ps1` のリリース運用目的変更
- GitHub Release / Git tag / Release draft の作成
- 配布 package の作成
- ユーザーに「リリース準備完了」と見える変更

例外:

- build / test のためのローカル artifact 作成
- release freeze ルール自体を文書化する docs 変更
- 既存 release 関連コードの refactor blocker 調査
- active outcome に必要な release / updater / output-layout code の構造整理。ただし version、配布挙動、公開手順は変えない

blocker の調査・境界整理から実リリース準備へ進んではならない。

---

## 2. ログ出力の作法

- `NLog` を直接呼び出してはならない。
- 必ず **`Ribbit\Logging\NLogWrapper.cs`** を経由すること。
  - 推奨:

    ```csharp
    Ribbit.Logging.NLogWrapper.FileLogger?.Info("...");
    ```

- 実装・改修時は、想定外の不具合や性能劣化の検証を容易にするため、**`[INFO]` レベルのログを積極的に出力**すること。
- ログメッセージは挙動の説明を含め、将来の解析に有用な情報を残すこと。

---

## 3. 多言語対応・リソース管理

- ユーザーが通常操作で読む UI 文言を `.cs` や `.xaml` に直接ハードコードしてはならない。
- 新規 UI 文言追加時は必ず以下を実施すること：

1. `BeMusicSeeker\Properties\Resources.resx`
2. `BeMusicSeeker\Properties\Resources.cs`
3. `lang` フォルダ内の各言語 JSON (ja-JP.json, en-US.json 等)

キー追加 → 既定値定義 → 各言語翻訳追記 の順で行うこと。

- UI 文言キーを追加・削除した場合は、`BeMusicSeeker.Tests\LocalizationResourceParityTests.cs` の
  `Resources.resx` / `Resources.cs` / `lang\*.json` キー一致テストが通る状態にすること。

- ログ、診断、性能計測、開発者向け progress/debug 表示、テスト用の期待文字列は、多言語リソース化の対象外としてよい。
- ただし、エラーダイアログ、設定画面、メニュー、ボタン、通常のステータスバー文言など、ユーザー向け UI として安定表示する文字列はリソース化すること。

---

## 4. ビルド環境とコマンド

実行環境は Windows PowerShell。

ローカルツールは `.config\dotnet-tools.json` で管理する。初回、ツール manifest 変更時、またはツール状態が不明な場合は以下を実行する。

```powershell
dotnet tool restore
```

- Linux系コマンドは禁止
  - grep → rg または Select-String
  - touch → New-Item
- 検索は `rg` を優先し、必要に応じて PowerShell の `Select-String` を使う。

### 標準確認手順

コード変更時は、変更範囲に応じて以下を実行すること。

```powershell
dotnet restore BeMusicSeeker.sln
dotnet build BeMusicSeeker.sln /p:Configuration=Release
dotnet test BeMusicSeeker.sln /p:Configuration=Release
dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal
$msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -version "[17.0,18.0)" -products * -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin"
dotnet roslynator analyze BeMusicSeeker.sln --msbuild-path $msbuildPath --properties Configuration=Release --severity-level warning --verbosity minimal
```

- `dotnet restore` は初回、パッケージ・ツール・プロジェクト構成変更時、または restore 状態が不明な場合に実行する。
- 通常開発 SDK は `global.json` で .NET SDK 10 系に固定する。Visual Studio 2026 (18.x) と .NET Framework 4.7.2 Developer Pack が必要。
- `dotnet tool restore` と `dotnet restore` は別物として扱う。`roslynator` が見つからない場合は `dotnet tool restore` を実行する。
- SDK 10 環境では Roslynator 0.12.0 が SDK 同梱 MSBuild 18 / VS 2026 MSBuild 18 で失敗するため、上記のように VS 2022 / MSBuild 17 の path を明示する。Roslynator が MSBuild 18 に対応したら見直す。
- 小さな変更では関連テストを優先してよいが、共有モデル・ViewModel・永続化・リソース・起動処理に触れた場合は原則として `dotnet test BeMusicSeeker.sln /p:Configuration=Release` を実行する。全体テストは数分かかるため、実行ツール側のタイムアウトは 15 分以上を目安にする。
- 全体テスト失敗時は、失敗テストを `--filter` で個別再実行する。個別では成功する場合は並列実行時の共有状態干渉を疑い、`Settings.Default`、環境変数、`CultureInfo.CurrentCulture`、静的キャッシュ、共有ファイル/DB、WPF dispatcher を変更するテストへ `[DoNotParallelize]` を付ける。個別でも失敗する場合は、並列問題ではなく通常の回帰または既存期待値ドリフトとして扱う。
- format の標準は SDK 付属の `dotnet format` とし、通常確認では whitespace のみを `--verify-no-changes` で検査する。古い local tool の `dotnet-format` は SDK 付属版と判定差が出やすいため、リポジトリ標準にはしない。
- whitespace 以外の `.editorconfig` スタイル確認が必要な場合は、必要に応じて `dotnet format style BeMusicSeeker.sln --verify-no-changes --no-restore --severity warn --verbosity minimal` を追加で実行する。Analyzer 修正候補を確認する場合は `dotnet format analyzers BeMusicSeeker.sln --verify-no-changes --no-restore --severity warn --verbosity minimal` を補助的に使う。
- 整形のみの変更が必要な場合は `dotnet format whitespace BeMusicSeeker.sln --no-restore --verbosity minimal` を実行し、機能修正とは差分を分けて扱う。
- `roslynator analyze` は当面レポート用途とし、既存警告を理由に通常の修正を止めない。ただし、今回の変更で新たに発生した警告は原則として同じ変更内で解消する。棚卸し時は `--verbosity normal` または `--output <path>` を併用し、診断 ID と場所が残る形で確認する。

### .editorconfig と警告の扱い

- `.editorconfig` は整形・コードスタイル・Analyzer severity の基準を置く場所とする。
- 既存の Roslynator / Analyzer 警告は、修正前に以下へ分類する。
  1. 安全に解消できる機械的な警告
  2. 設計判断が必要な警告
  3. デコンパイル由来または互換性維持のため当面残す警告
- 警告を抑制する場合は、場当たり的に `#pragma` や属性を追加せず、まず `.editorconfig` でリポジトリ全体の方針として扱えるか検討する。
- 特定箇所でのみ抑制する場合は、そのコード固有の理由コメントを残す。
- Analyzer severity を `warning` / `error` に上げるのは、対象診断の既存警告を解消または意図的に文書化した後に行う。

## 5. バージョン更新作業時の手順

バージョン変更依頼を受けた場合、必ず以下を更新・確認すること：

1. `Properties\AssemblyInfo.cs`
   - `AssemblyInformationalVersion`。リリースパッケージ名、`update.json`、公開用 `version.txt` の正本として扱う
2. `BeMusicSeeker\Views\SettingDialog.xaml`
   - Update_history セクションの更新履歴追記
   - リリース概要として読める短い日本語文をベタ書きする。多言語キーや `lang\*.json` はユーザーから明示依頼がない限り増やさない
3. `release notes\vX.X.X.X リリースノート.md`
   - 対象バージョンのリリースノートが存在し、GitHub Release 本文として使える状態まで整備済みであることを確認する
   - `scripts\release.ps1` はこのファイルを release body として参照するため、少なくとも draft 作成前に存在確認を行う

`version.txt` は手動更新しない。旧クライアント向けに公開リポジトリへ置く場合も、`scripts\publish.ps1` / `scripts\release.ps1` が `AssemblyInformationalVersion` から生成する。

---

## 6. ドキュメントコメント（XML必須）

### 対象

以下すべてに XML ドキュメントコメントを記述すること：

- public
- protected
- internal

対象メンバー：

- class / struct / interface / enum
- method
- property
- event
- field

### 必須タグ

- `<summary>`（必須）
- `<param>`（引数がある場合すべて）
- `<returns>`（void以外）
- `<exception>`（明確に投げる場合）

### 記述方針

- シグネチャの言い換えは禁止
- 「何をするか」だけでなく「なぜそうなっているか」を含める
- 不明な仕様は推測しない（観測事実として記述）

---

## 7. 命名改善（変更時は必須）

コードを編集する場合、触れたスコープ内で以下を行うこと：

### 置き換えるべきデコンパイル由来名

- `num*`, `flag*`, `text*`, `obj*`, `item*`, `list*`
- `a`, `b`, `c`
- `V_*`
- `CS$<>8__locals*`

### 改善方針

- ドメイン語彙を使う
- 単位を含める（例: `milliseconds`, `byteCount`）
- bool は `is` / `has` / `can` など意味を明確化
- マジックナンバーは `const` または `static readonly` に置き換える

---

## 8. 理由コメント（重要）

XMLコメントとは別に、以下の場合は **理由コメントを必ず追加すること**：

- デコンパイル特有の不自然な構造
- 意図的に残している冗長処理
- パフォーマンス最適化のための特殊処理
- 既知のバグ回避コード
- 外部仕様依存の実装

例：

```csharp
// NOTE:
// この一見冗長なチェックは、元のバイナリが境界検証を2回実行しているために存在します。
// これを削除すると動作が変わる可能性があります。
```

理由コメントは「なぜ必要か」を説明すること。

---

## 9. サブエージェント静的レビュー依頼ルール

コードレビュー用サブエージェントを起動する場合、次の順で安全側に寄せること。

1. `.codex/agents/repo-static-review.toml` で定義された `repo-static-review` agent が利用できる環境では、それを使う。
2. `repo-static-review` が利用できない場合だけ `explorer` agent を使う。
3. どちらの場合も履歴 fork は使わない。`fork_context=false` とし、依頼文に repo path / レビュー対象 / 禁止事項を明示する。

依頼文の冒頭には必ず以下の趣旨を含めること。

```text
あなたはサブエージェントです。
このターンでは実装・ファイル編集・ビルド・テスト・format・roslynator・commit を禁止します。

目的は「現在の未コミット差分の静的レビューのみ」です。
許可する操作は git diff / git status / rg / Get-Content などの読み取り調査だけです。
dotnet build / dotnet test / dotnet format / dotnet roslynator analyze は絶対に実行しないでください。

標準確認手順は親エージェントが実行します。
レビュー結果だけを重大度順に、ファイル/行参照付きで返してください。
問題がなければ「重大な指摘なし」と短く返してください。
```

- 「サブエージェント側では」ではなく、「あなたはサブエージェントです」「このターンでは」「あなた自身は実行しない」と読める文言にする。
- 履歴 fork を使うと、サブエージェントが親エージェントの作業サイクルを継続する誤解が起きるため、静的レビュー依頼では禁止する。
- `repo-static-review` が使えない場合の fallback は `explorer` とし、worker / default agent へ静的レビューを依頼しない。
- レビューエージェントの待機では明示的な短い timeout を設定せず、完了まで待つ。
- レビュー完了後は `close_agent` で閉じる。

---

## 10. 変更時の必須手順

1. 機能修正
2. 命名改善
3. XMLドキュメント追加・更新
4. 必要な理由コメント追加
5. `## 4. ビルド環境とコマンド` に従った確認
6. サブエージェント静的レビュー
7. ユーザー承認待ち（通常作業のコミット前）
8. PLAN_STATUS の active outcome 作業では、重大指摘なし・標準確認完了後にユーザー承認待ちなしで commit してよい
