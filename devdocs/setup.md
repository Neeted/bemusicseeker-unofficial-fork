# 開発環境の構築

Windows 11の新しい開発環境を準備するための手順です。初回構築時、または依存ツールの不足によってビルド・検証に失敗したときに参照してください。構築済みの環境で、通常の開発作業のたびに環境確認やインストールを実行する必要はありません。

本書は公開リポジトリの `dev` を対象とします。リリースZIPではなく、アプリのソース一式、`BeMusicSeeker.sln`、`global.json`、`scripts/verify-refactor.ps1` を含むチェックアウトを使用します。

```powershell
git clone --branch dev https://github.com/Neeted/bemusicseeker-unofficial-fork.git
cd bemusicseeker-unofficial-fork
```

既存のcloneでは、作業中の変更を整理してから `git fetch origin` と `git switch dev` を実行します。公開サイトと更新情報は `main` から配信されます。開発のために別の非公開リポジトリを準備する必要はありません。

## 前提

- Windows 11 x64を使用します。現在のアプリとネイティブ依存関係はx64を対象としています。
- Gitでリポジトリをクローン済みとします。Gitがない場合は、先に[Git for Windows](https://gitforwindows.org/)を導入してください。
- ソースを置くフォルダには書込み権限が必要です。以下のコマンドはリポジトリルートで実行します。
- ツールの導入と、初回のNuGetパッケージ復元にはインターネット接続が必要です。

通常のC#・WPF開発に必要なのは、Git、PowerShell 7、対応する.NET SDKです。エディターやIDEは任意に選べます。コマンドラインでのビルドにVisual Studio本体は必須ではありません。

## 初回構築

### 1. PowerShell 7を導入する

Windows標準のWindows PowerShell 5.1とは別に、PowerShell 7を導入します。WinGetを利用できる環境では、Windowsのターミナルで次を実行できます。

```powershell
winget install --id Microsoft.PowerShell --exact --source winget
```

WinGetがない場合やインストーラーを使う場合は、[Microsoftの導入手順](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows)を参照してください。

インストール後はターミナルを開き直し、PowerShell 7を選択します。以降の作業はPowerShell 7で行います。

### 2. global.jsonに対応する.NET SDKを導入する

必要なSDKのバージョンは、リポジトリルートの[global.json](../global.json)を正本とします。[.NETの公式ダウンロードページ](https://dotnet.microsoft.com/download/dotnet)で、指定に対応するWindows x64版の**SDK**をインストールしてください。実行専用のRuntimeだけではビルドできません。

`version`と`rollForward`を合わせて確認します。例えば、`version`が`10.0.302`、`rollForward`が`latestPatch`なら、10.0.302以上の10.0.3xx系列が対象です。10.0.4xxだけを導入しても条件を満たしません。「最新の.NET 10」を無条件に選ぶのではなく、チェックアウトした版の指定に合わせます。詳細は[SDKの選択規則](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)を参照してください。

インストール後はターミナルを開き直します。SDKに合わせるために`global.json`やパッケージのロックファイルを変更する必要はありません。

### 3. 標準検証を実行する

リポジトリルートで次を実行します。

```powershell
git --version
pwsh --version
dotnet --version
```

各コマンドが成功し、PowerShellが7系で、`dotnet`が`global.json`に対応するSDKを選択できることを確認します。その後、標準の検証入口を実行します。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

`Functional`は、ロックファイルに従ったパッケージ復元、整形検査、Release/x64ビルド、出力確認、通常の機能テストを行います。初回はパッケージの取得が発生します。別途同じ復元・ビルド・テストを重ねて実行する必要はありません。

成功・失敗の判定、実行期限、再実行の条件は[テストの実行と受入検証](spec/development/testing.md)を参照してください。失敗した場合は、表示された失敗段階と診断先を確認します。診断用の出力は`artifacts/verification/`に生成されます。

### 4. 生成したアプリを確認する

標準検証が成功したら、生成されたアプリを起動します。次の例は、出力フォルダを作業ディレクトリとして明示します。

```powershell
$appDirectory = Join-Path (Get-Location).Path 'bin\x64\Release\net10.0-windows'
$appProcess = Start-Process -FilePath (Join-Path $appDirectory 'BeMusicSeeker.exe') `
    -WorkingDirectory $appDirectory -PassThru
```

初回設定では、検証用の小さいBMSライブラリを指定します。単体動作モードを使えば、LR2のDBを準備せずに確認できます。既存のBeMusicSeeker設定が自動移行される場合があるため、実際に選ばれている動作モードと参照先を確認してから読み込んでください。初期設定の詳細は[日本語マニュアル](../docs/manual.ja.md)を参照します。

画面の表示、初期設定、検証用ライブラリの読込みを確認したら、起動したアプリを通常の終了操作で閉じます。既存のインストール版と取り違えないよう、上記の生成先を使ってください。AIエージェントによる画面確認では、[画面確認の規則](spec/development/testing.md#画面確認)にも従います。

`Functional`が成功し、生成したアプリで初期設定と読込みを確認できれば、通常開発を始められる状態です。

## 用途別の追加準備

### Everything連携を確認する場合

[Everything 1.5 x64](https://www.voidtools.com/everything-1.5/)を導入し、検証用ライブラリの索引作成が完了してから確認します。大規模ライブラリでの動作・性能確認に推奨します。Everything本体は通常のビルドの必須条件ではありません。

Everything本体と、アプリが読み込むSDK・ブリッジDLLは別のものです。後者の`native/Everything3_x64.dll`と`native/EverythingBridge_x64.dll`、音声処理用の`vendor/native/x64/`内のDLLはリポジトリに含まれています。通常のC#開発では、これらの手動ダウンロードやネイティブブリッジの再ビルドは不要です。7-ZipやSQLiteのパッケージ由来の依存関係は、プロジェクトのNuGet復元・ビルドで用意します。

### ネイティブブリッジを変更する場合

Visual Studio Installerから、Visual StudioまたはBuild ToolsのC++開発環境を追加します。[ブリッジのプロジェクト](../native/EverythingBridge/EverythingBridge.vcxproj)が指定するMSVC v143のx64/x86ビルドツール、Windows SDK、MSBuildが必要です。.NET SDKに含まれる`dotnet build`だけでは、このC++プロジェクトのビルド環境は揃いません。

```powershell
pwsh -NoProfile -File .\native\EverythingBridge\build-x64.ps1 -Configuration Release
```

この操作はGit管理されている`native/EverythingBridge_x64.dll`を更新します。その後、標準検証でアプリとの組合せを確認します。詳細は[ブリッジの説明](../native/EverythingBridge/README.md)を参照してください。

### マニュアルのHTMLを生成する場合

生成スクリプトはPython 3.12以上を要求します。既存の配布処理は`uv run`を使用しているため、[uv](https://docs.astral.sh/uv/getting-started/installation/)を導入し、[build-doc-html.py](../scripts/build-doc-html.py)に宣言されたPythonと依存パッケージを使います。通常のC#開発や`Functional`のためにPython環境を用意する必要はありません。

### 配布・更新に関わる場合

[リリース手順](spec/development/release.md)と[テスト検証](spec/development/testing.md)を参照し、`Full`や作業対象に必要な準備を行います。初回構築の確認にリリース作成・タグ・送信・公開は含めません。

## 初回構築でつまずいた場合

| 症状 | 確認と対処 |
| --- | --- |
| `pwsh`や`dotnet`が見つからない | インストール後にターミナルを開き直します。対応するツールを導入したか、PATHに追加されているかを確認します。 |
| 対応する.NET SDKが見つからない | リポジトリルートの`global.json`と`dotnet --list-sdks`を比較し、対応するx64 SDKを追加します。指定の変更で回避しません。 |
| PowerShellがスクリプトの実行を拒否する | `Get-ExecutionPolicy -List`で原因を確認します。組織のポリシーがない個人環境では、内容を確認したスクリプトに対し、現在のPowerShell 7セッションで`Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned`を実行し、そのセッションで検証コマンドを再実行できます。ダウンロード済みファイルのブロックは別途確認し、組織のポリシーは管理者に相談します。 |
| パッケージの復元に失敗する | エラーに記載されたNuGetソースへの接続、プロキシ、認証を確認します。ロック不一致なら、依存関係のファイルがチェックアウトした版と一致するかを確認します。 |
| ネイティブDLLが不足している | `native/`と`vendor/native/x64/`を含む開発用ソース一式を取得したか確認します。別バージョンのDLLを出力フォルダへコピーして回避しません。 |
| ビルドは成功するがテストが失敗する | 診断出力を確認し、[検証仕様](spec/development/testing.md)に沿って原因を切り分けます。テストの除外や成功するまでの反復で構築完了としません。 |

## AIエージェントを活用する

コードの調査、変更案の検討、実装、検証には、CodexなどのAIエージェントの活用を推奨します。利用は任意で、手作業でも同じコマンドと資料を使えます。ツールの導入・認証は各ツールの公式資料を参照してください。

リポジトリルートを作業対象にし、[AGENTS.md](../AGENTS.md)と関連する[開発資料](README.md)に従うよう依頼します。複数段階の開発の進め方は[エージェントによる開発作業](spec/development/agent-workflow.md)を参照してください。

初回構築を依頼する例:

> devdocs/setup.mdに従って、このWindows環境を開発可能な状態にしてください。既存のツールは利用し、不足するものを導入して、標準検証まで行ってください。

構築後の開発では、変更したい挙動、期待する結果、検証に使えるデータや条件を伝えます。環境構築を毎回依頼する必要はありません。変更結果は差分と検証結果で確認してください。

## 開発を始める

`dev` を更新してから作業ブランチを作ります。例えば `git switch dev`、`git pull --ff-only`、`git switch -c codex/変更内容` の順に実行します。GitHubの既定ブランチは `main` なので、通常のプルリクエストでは対象を `dev` に変更してください。正式リリース、緊急修正、TSVだけの更新は[ブランチ運用とリリース手順](spec/development/release.md)に従います。

仕様や担当領域の入口は[開発資料の案内](README.md)です。反復中の対象確認、通常の最終検証、配布・更新の検証の使い分けは[テスト検証](spec/development/testing.md)を正本とします。本書では検証ルールを重複して定義しません。
