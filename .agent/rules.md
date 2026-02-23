# BeMusicSeeker AI Agent Rules

本プロジェクトにおいて AI エージェント（アシスタント）が作業を行う際の基本ルールです。これらを遵守して開発・検証を進めてください。

## 1. コミット戦略
- 機能を実装・修正した場合、**絶対にユーザーの承認を得る前に自動で `git commit` を実行してはならない**。
- 必ずユーザーに動作確認を依頼し、OKの回答を得た後にコミットのコマンドを提案し、承認を得てから実行（またはユーザー自身に実行）してもらうこと。
- **注意**: 本プロジェクト（ブランチ）はリモート未登録のため、コミット後の `git push` は不要（実行不可）である。

## 2. ログ出力の作法
- `NLog` をそのまま直接呼び出すことは避ける。
- アプリ全体の動作・挙動と整合性を取るため、必ず **`Ribbit\Logging\NLogWrapper.cs`** を経由して出力すること。
  - 推奨: `Ribbit.Logging.NLogWrapper.FileLogger?.Info("...");` 
- 実装・改修時は、後から想定外の不具合や性能の劣化を検証しやすくするため、**`[INFO]` レベルのログを積極的に出力**すること。

## 3. 多言語対応・リソース管理
- とりあえずの機能追加など特別な理由がない限り、**ソースコード (.cs や .xaml) 内に直接表示用文字列をハードコードしない**こと。
- 新しく文字列を追加する場合は以下の手順を踏む:
  1. `BeMusicSeeker\Properties\Resources.resx` および対応する `BeMusicSeeker\Properties\Resources.cs` にキーとデフォルト文字列を定義する。
  2. `lang` フォルダ内にある各言語の JSON ファイル (ja-JP.json, en-US.json 等) に追加したキーと翻訳テキストを追記する。

## 4. ビルド環境とコマンド
- 本プロジェクトのビルドは以下のコマンドを実行することで行うこと。
  ```powershell
  dotnet build BeMusicSeeker-decomp.sln /p:Configuration=Release
  ```
- 実行環境は **Windows PowerShell** であるため、ターミナルコマンド実行時は必ず PowerShell ベースの構文を用いること（`grep` ではなく `Select-String`、`touch` ではなく `New-Item` を使用し、Linux用のコマンド・オプションは排除する）。

## 5. バージョン更新作業時の手順
アプリのバージョン変更依頼（リリース・アップデート準備）を受けた場合は、必ず以下の **3箇所** すべてを書き換えること。
1. **`Properties\AssemblyInfo.cs`**: `AssemblyInformationalVersion` の値を新しいバージョンに書き換える。（※`BeMusicSeeker`フォルダ内ではなくルート直下のProperties）
2. **`BeMusicSeeker\Views\SettingDialog.xaml`**: `<GroupBox DockPanel.Dock="Bottom" Header="{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Update_history, Mode=OneWay}">` のセクション内の中身（更新履歴テキスト）に最新バージョンの情報を書き換える（追加する）。
3. **`version.txt`**: ファイルの中身を新しいバージョン文字列に書き換える。

## 6. 本プロジェクト特有の実装ルール (UIと多言語連携)
- **WPF ContextMenu / MenuItem 等のバインディングに関する注意点**: 
  多言語対応の仕組みとして `ResourceService.ChangeCulture` を動的に呼び出している。この際、子項目（サブメニュー）を持つ親の `MenuItem` に対し、`Header="{Binding ...}"` を直接適用すると、バインディング更新の際に内部 VisualTree の `Role` が破損し、サブメニューが消失・展開不可能になるWPF既知の不具合がある。
  **【対策】** 親となる `MenuItem` の `Header` は必ず以下のように `<TextBlock>` でラップし、`MenuItem.Header` のオブジェクトインスタンスが差し替わることを防ぐこと。
  ```xml
  <MenuItem>
    <MenuItem.Header>
      <TextBlock Text="{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Remove, Mode=OneWay}" />
    </MenuItem.Header>
    <!-- 子 MenuItem  -->
  </MenuItem>
  ```
