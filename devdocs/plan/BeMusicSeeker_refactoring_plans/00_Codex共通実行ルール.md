# Codex 共通実行ルール

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 原則

- 変更は ticket 単位で行う。
- 1 ticket で「構造変更」と「挙動変更」を混ぜない。
- デコンパイル由来の読みにくい名前に触れた場合は、意味を推定できる範囲で命名を改善する。
- 外部仕様を変えない限り、既存の振る舞いを維持する。
- ユーザー承認前に `git commit` しない。
- C# symbol rename は text replacement ではなく semantic rename / compiler-driven edit を使う。

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

- root VM / root domain class から責務が本当に減ったか。
- 新 service が `Settings.Default`、`Window`、`Control`、`MessageBox`、`NLog` に直接依存していないか。
- migration risk を wrapper に閉じ込めたか。
- old API が test のためだけに残っていないか。
- persisted setting / DB schema / serialized name を無計画に変えていないか。
