# Codex 共通実行ルール

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 原則

- 変更は Refactoring MVP active lane の ticket / slice 単位で行う。
- 1 ticket で「構造変更」と「挙動変更」を混ぜない。
- デコンパイル由来の読みにくい名前に触れた場合は、意味を推定できる範囲で命名を改善する。
- 外部仕様を変えない限り、既存の振る舞いを維持する。
- Refactoring MVP 作業では、ユーザーが実装単位ごとの commit を事前許可している。ticket / slice 完了時は標準確認とサブエージェント静的レビュー後に commit してよい。
- `git push`、Git tag、GitHub Release、Release draft、publish / release script 実行は禁止する。
- C# symbol rename は text replacement ではなく semantic rename / compiler-driven edit を使う。

## Refactoring MVP Release Freeze

Refactoring MVP Gate 通過まで、リリース作業を凍結する。重要判断は [REF-MVP Release Freeze and Gate Decision](./decisions/REF-MVP_release_freeze_and_gate.md) にも残す。

禁止:

- `Properties/AssemblyInfo.cs` の `AssemblyInformationalVersion` 更新。
- `release notes/` の新規リリース向け更新。
- `version.txt` のリリース目的更新。
- `scripts/publish.ps1` / `scripts/release.ps1` のリリース運用目的変更。
- GitHub Release、Git tag、Release draft、配布 package の作成。
- ユーザーに「リリース準備完了」と見える変更。

例外:

- build / test のためのローカル artifact 作成。
- release freeze ルール自体を文書化する docs 変更。
- 既存 release 関連コードの refactor blocker 調査。

blocker 調査から実リリース準備へ進まない。

## Refactoring MVP Gate

リリース凍結を解除できるのは、次を満たしてからとする。

- `MainWindowViewModel` が application shell / composition / lifecycle / dialog request bridge / progress bridge / UI thread 境界 / child ViewModel・coordinator 委譲に寄っている。
- main chart list、playlist detail build / apply、play history、playback、settings save、library refresh、package install workflow が root から child ViewModel / coordinator / service へ移っている。
- `MainWindow.cs` が WPF lifecycle / view-host / WPF event entry point / UI 型 adapter / command invocation bridge に寄り、処理本体を持つ `async void` と巨大 event handler が大幅に減っている。
- `BMSLibrary` が public compatibility facade として薄くなり、initialization、LR2 `song.db` sync、package install、maintenance、playlist reference、file operation、normal library refresh、source text / source file handling が service / coordinator へ移っている。
- 新規 service / coordinator に `Window`、`Control`、`MessageBox`、`Settings.Default`、`NLog`、無制御な DB connection / transaction、無制御な `Dispatcher` 直参照を増やしていない。
- `.NET 10` 移行 blocker が巨大クラス内の未整理ロジックではなく、adapter / gateway / native dependency / config / settings / external process host の課題として説明できる。
- build、test、format / whitespace check、`git diff --check` が通る。
- サブエージェント静的レビューで重大な指摘がない。

行数は guardrail として監視する。設計成否の唯一の判定にはしない。

- `MainWindowViewModel.cs`: 最終 8,000 行以下を目標にする。
- `MainWindow.cs`: 最終 5,000 行以下を目標にする。
- `BMSLibrary.cs`: 最終 12,000 行以下を目標にする。
- `BMSPlaylist.cs`: P1 対象だが、P0/P1 境界で 6,000 行以下を目標にする。

目標を超える場合は `PLAN_STATUS.md` に、残す責務、残す理由、次の extraction 候補、サブエージェントレビュー結果を記録する。

## 計画運用ルール

- WIP は原則 1 active lane slice にする。
- ticket 粒度は 1 workflow / 1 responsibility boundary に引き上げる。
- DTO 追加、method signature 整理、result object 導入だけを独立 ticket にしない。これらは workflow extraction の subtask として扱う。
- checkpoint 専用 ticket を連続させない。
- decision record は、永続化、DB schema、public API、concurrency / lock ordering、UI observable behavior、.NET migration policy に関わる場合だけ作る。
- docs-only commit は原則禁止する。ただし、計画修正、gate 定義、重要 decision record は許可する。
- active plan は短く保ち、完了履歴は別ファイルまたは Appendix へ移す。
- ticket 完了時は「実装結果」「実行したテスト」「未解決の blocker」「次にやる 1 件」だけを更新する。
- 計画書は作業指示書であり、長い調査ログ置き場にしない。
- `.tmp` にだけ重要判断を残さない。継続判断は `devdocs` 側へ移す。
- 行数は観測値であり、設計の成否判定は責務境界、依存方向、テスト容易性、UI / DataContext 境界で判断する。

## Active Lane

`PLAN_STATUS.md` に次の active lanes を置き、Codex は毎回 Refactoring MVP Gate に最も近づく slice を選ぶ。P0-01 だけに閉じない。

- Lane A: MainWindowViewModel shell 化。
- Lane B: MainWindow code-behind / XAML MVVM 移行。
- Lane C: BMSLibrary domain facade 化。
- Lane D: .NET 10 migration readiness。

## 自走実装サイクル

1. `PLAN_STATUS.md` の active lanes を確認する。
2. Refactoring MVP Gate に最も近づく slice を選ぶ。
3. slice の目的、対象ファイル、禁止事項を短く整理する。
4. 実装する。
5. build / test / format / `git diff --check` を実行する。
6. サブエージェントに未コミット差分の静的レビューを依頼する。
7. 重大指摘があれば修正する。
8. 重大指摘がなくなるまで再レビューする。
9. `PLAN_STATUS.md` を必要最小限更新する。
10. ticket ID を含む commit message で commit する。
11. 次の slice へ進む。

迷った場合の優先順位:

1. release freeze を破らない。
2. 挙動変更、DB schema 変更、serialized value 変更、UI 文言変更を避ける。
3. root ViewModel / code-behind / facade から責務が減る方向を選ぶ。
4. private 実装配置を固定する test を増やさない。
5. `.NET 10` 移行 blocker を増やさない。
6. それでも迷う場合は、より小さい slice に分けて `PLAN_STATUS.md` に理由を記録する。

## 着手前チェック

PowerShell で実行する。

```powershell
git status --short
dotnet tool restore
dotnet restore .\BeMusicSeeker.sln
```

確認すること。

- 未コミット差分がある場合、今回の作業対象と無関係な差分かどうかを記録する。
- 既存失敗がある場合は、変更前の failure と変更後の failure を区別できるようにする。

## 標準確認コマンド

大きな refactor ticket の完了時は原則として次を実行する。

```powershell
dotnet build .\BeMusicSeeker.sln /p:Configuration=Release
dotnet test .\BeMusicSeeker.sln /p:Configuration=Release
dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal
$msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -version "[17.0,18.0)" -products * -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin"
dotnet roslynator analyze .\BeMusicSeeker.sln --msbuild-path $msbuildPath --properties Configuration=Release --severity-level warning --verbosity minimal
```

通常開発 SDK は `global.json` で .NET SDK 10 系に固定する。SDK 10 環境では Roslynator 0.12.0 が SDK 同梱 MSBuild 18 / VS 2026 MSBuild 18 で失敗するため、Roslynator だけは Visual Studio 2022 / MSBuild 17 の path を明示する。Roslynator が MSBuild 18 に対応したら見直す。

小さい ticket では関連テストを先に回してよい。ただし、共有 model / ViewModel / DB / file system / settings / dispatcher に触れた場合は最終的に全体 test を回す。

## テスト移行ルール

### source-text tests

巨大ファイルを `File.ReadAllText` しているテストは、ファイル分割で壊れやすい。次のような helper を先に入れる。

候補:

- `BeMusicSeeker.Tests/SourceTextTestHelper.cs`

責務:

- `ReadProductionSourceText(params string[] relativePathParts)`
- `ReadProductionSourceTextGlob(string baseDirectory, string includePattern)`
- `ReadMainWindowViewModelSourceText()`
- `ReadMainWindowSourceText()`
- `ReadBmsLibrarySourceText()`

`ReadMainWindowViewModelSourceText()` は、少なくとも次を連結して読む。

- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel*.cs`
- `BeMusicSeeker/ViewModels/MainWindow/**/*.cs`

`ReadMainWindowSourceText()` は、少なくとも次を連結して読む。

- `BeMusicSeeker/Views/MainWindow.cs`
- `BeMusicSeeker/Views/MainWindow*.cs`
- `BeMusicSeeker/Views/MainWindow/**/*.cs`

`ReadBmsLibrarySourceText()` は、少なくとも次を連結して読む。

- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/Models/BMSLibrary*.cs`
- `BeMusicSeeker/Models/BmsLibrary/**/*.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/**/*.cs` は必要な test のみ対象にする。

### private reflection tests

private member を直接参照している test は、次の順で置き換える。

1. 抽出予定の pure service を先に作る。
2. service の public/internal API を test する。
3. 既存 private reflection test は同じ挙動を検証できることを確認してから削除する。
4. 互換 wrapper は production code が使わなくなった段階で削除する。

## 新規 service の置き場所

目安:

- UI presentation / ViewModel 補助: `BeMusicSeeker/ViewModels/MainWindow/`
- WPF behavior / command adapter: `BeMusicSeeker/Views/Behaviors/` または `BeMusicSeeker/Views/MainWindow/`
- domain service: `BeMusicSeeker/Models/BmsLibraryInternal/`
- playlist domain service: `BeMusicSeeker/Models/PlaylistInternal/` を新設してよい
- migration / settings facade: `BeMusicSeeker/Properties/` または `BeMusicSeeker/Models/Configuration/`

既存 namespace 互換を優先する。フォルダを変えても namespace は必要に応じて維持する。

## XML コメントと理由コメント

public / protected / internal 型・メンバーには XML コメントを付ける。特に以下は理由コメントを残す。

- lock 順序
- UI thread / Dispatcher 前提
- LR2 / beatoraja / song.db 互換のための特殊処理
- file system mutation の失敗許容
- performance 最適化
- .NET Framework 互換のために当面残す処理

## 変更後レビュー観点

サブエージェントには、未コミット差分の静的レビューだけを依頼する。依頼文は次を基本形にする。

```text
あなたはサブエージェントです。
このターンでは実装・ファイル編集・ビルド・テスト・format・roslynator・commit を禁止します。

目的は「現在の未コミット差分の静的レビューのみ」です。
許可する操作は git diff / git status / rg / Get-Content などの読み取り調査だけです。

レビュー観点:
- 今回 ticket の目的に対して責務が本当に root から減っているか
- 新しい service / coordinator が View / Settings / NLog / DB / Dispatcher に不適切に依存していないか
- 挙動変更、永続化 schema 変更、serialized name 変更、UI 文言変更が紛れ込んでいないか
- root から child への依存方向が悪化していないか
- test が private 実装配置をさらに固定していないか
- .NET 10 移行阻害要因を増やしていないか

重大度順に、ファイル/行参照付きで返してください。
問題がなければ「重大な指摘なし」と短く返してください。
```

重大指摘には少なくとも次を含む。

- build / test を壊す可能性が高い。
- 実行時挙動を変える可能性が高い。
- DB schema / serialized value / setting key / file format を意図せず変える。
- View / ViewModel / service の責務分離を悪化させる。
- 新規 service / coordinator に UI 型や global singleton の直依存を増やす。
- `MainWindowViewModel`、`MainWindow.cs`、`BMSLibrary` の巨大化をさらに進める。
- `.NET 10` 移行 blocker を増やす。
- release freeze を破る。
- private 実装配置を固定する brittle test を増やす。

- root VM / root domain class から責務が本当に減ったか。
- 新 service が `Settings.Default`、`Window`、`Control`、`MessageBox`、`NLog` に直接依存していないか。
- migration risk を wrapper に閉じ込めたか。
- old API が test のためだけに残っていないか。
- persisted setting / DB schema / serialized name を無計画に変えていないか。
