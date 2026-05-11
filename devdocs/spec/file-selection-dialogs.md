# ファイル選択ダイアログ仕様

この資料は、現行実装でファイル/フォルダ選択ダイアログを開く画面と、既定拡張子の扱いを一覧化する。

対象は `CommonOpenFileDialogInteractionMessageAction`、WPF 標準 `OpenFileDialog` / `SaveFileDialog`、および直接生成している `CommonOpenFileDialog`。フォルダ選択ダイアログはファイル拡張子を持たないため、既定拡張子は `N/A` とする。

## 共通ルール

`OpeningFileSelectionMessage` 経由のファイル選択は `CommonOpenFileDialogInteractionMessageAction.ShowFileSelectionDialog()` が処理する。

- `DefaultFileName` は `OpeningFileSelectionMessage.FileName` をそのまま使う。
- `DefaultExtension` は `FileName` の拡張子から推定する。
- `FileName` から推定できない場合は、最初の有効な filter pattern から推定する。
- `*.*`、`?` を含む wildcard、拡張子なし pattern は既定拡張子として使わない。
- `Filter` は `display|pattern|display|pattern` 形式を `CommonFileDialogFilter` に変換する。display が空の場合は pattern を表示名にも使う。
- `InitialDirectory` は、既存 directory ならその directory、既存 file path なら親 directory を使う。

`Default Extension Source` は次のどれかを記載する。

- `Direct`: `DefaultExt` / `DefaultExtension` をコードで直接指定している。
- `Computed from FileName`: `OpeningFileSelectionMessage.FileName` から `CommonOpenFileDialogInteractionMessageAction` が算出する。
- `Computed from Filter`: `FileName` が無く、filter pattern から算出する。
- `N/A`: folder picker など、拡張子を持たない。

## 設定ダイアログ

### 一般

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 一般 | スタンドアローン BMS ディレクトリ追加 | `CommonOpenFileDialog` direct | Folder, multi-select | BMS root directories | N/A | N/A | N/A | N/A | `StandaloneBmsRootPathList` |
| 一般 | LR2 ディレクトリ参照 | `FolderSelectionMessage` | Folder | LR2 root directory | N/A | N/A | N/A | N/A | `settingDialog.LR2RootPath` |
| 一般 | `song.db` 参照 | `OpeningFileSelectionMessage` | File open | LR2 song DB (`*.db`) | `song.db (*.db)`, all files | `song.db` | `db` | Computed from FileName | `settingDialog.LR2SongDBPath` |
| 一般 | `config.xml` 参照 | `OpeningFileSelectionMessage` | File open | LR2 `config.xml` / `config.xmh` | `config.xm?`, all files | `config.xml` | `xml` | Computed from FileName | `settingDialog.LR2ConfigXmlPath` |
| 一般 | beatoraja `score.db` 参照 | `OpeningFileSelectionMessage` | File open | beatoraja score DB (`*.db`) | `Resources.FileDialogFilter_scoreDB` = `score.db (*.db)`, all files | `score.db` | `db` | Computed from FileName | `settingDialog.BeatorajaScoreDbPath` |

`config.xm?` は LR2 互換上の意図的な filter で、`config.xml` と `config.xmh` を許可する。既定拡張子は `FileName=config.xml` から算出されるため `xml` になる。

DB 系の filter は exact filename ではなく `*.db` を使う。表示上は `song.db` / `score.db` を案内するが、LR2 の空 DB や backup DB など、ファイル名が完全一致しない `.db` も選択できるようにする。

### 外観

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 外観 | 画像ファイル参照 | `OpeningFileSelectionMessage` | File open | stage image | `*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.tif;*.tiff`, per-format filters | none | `bmp` | Computed from Filter | `settingDialog.StagefilePath` |

既定拡張子は filter 先頭の `*.bmp` から推定される。既存 `StagefilePath` は initial directory の決定にだけ使う。

### 再生

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 再生 | `uBMplay.exe` 参照 | `OpeningFileSelectionMessage` | File open | uBMplay executable | `uBMplay.exe`, all files | `uBMplay.exe` | `exe` | Computed from FileName | `settingDialog.uBMplayPath` |
| 再生 | `BMIIDXView2015.exe` 参照 | `OpeningFileSelectionMessage` | File open | BMIIDXView executable | `BMIIDXView2015*.exe`, all files | `BMIIDXView2015.exe` | `exe` | Computed from FileName | `settingDialog.BMIIDXViewPath` |
| 再生 | LR2 参照 | `FolderSelectionMessage` | Folder | LR2 root directory | N/A | N/A | N/A | N/A | `settingDialog.LR2RootPath` |

LR2 player path は実行ファイル選択ではなく LR2 root folder 選択として扱う。

### 録音

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 録音 | encoder 実行ファイル検索先参照 | `FolderSelectionMessage` | Folder | encoder directory | N/A | N/A | N/A | N/A | `settingDialog.EncoderExeDir` |

### プレイリスト

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| プレイリスト | 通常出力先追加 | `FolderSelectionMessage` | Folder | LR2 custom folder output directory | N/A | N/A | N/A | N/A | `settingDialog.LR2CustomFolderOutputDir` |
| プレイリスト | ルートフォルダ出力先参照 | `FolderSelectionMessage` | Folder | LR2 custom root output directory | N/A | N/A | N/A | N/A | `settingDialog.LR2CustomFolderAsRootOutputDir` |

### インストール

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| インストール | 新規インストール先追加 | `FolderSelectionMessage` | Folder | install destination directory | N/A | N/A | N/A | N/A | `settingDialog.BMSInstallDir` |

### バックアップ

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| バックアップ | プレイリストのバックアップ | WPF `SaveFileDialog` | File save | playlist backup SQL | `*.sql` | `BeMusicSeeker_backup.sql` | `.sql` | Direct | `BackupBMSTables(fileDialog.FileName)` |
| バックアップ | プレイリストの復元 | WPF `OpenFileDialog` | File open | playlist backup SQL | `*.sql` | `BeMusicSeeker_backup.sql` | `.sql` | Direct | `RestoreBMSTables(fileDialog.FileName)` |
| バックアップ | LR2 backup 保存先参照 | `FolderSelectionMessage` | Folder | LR2 backup directory | N/A | N/A | N/A | N/A | `settingDialog.LR2BackupPath` |

backup save は `AddExtension = true` を明示する。restore は existing file open なので補完目的ではないが、file name / filter と合わせて `DefaultExt = ".sql"` を設定する。

## メインウィンドウ

| Screen | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| ライブラリ root context menu | ルートフォルダ追加 | `FolderSelectionMessage` | Folder | BMS root directory | N/A | N/A | N/A | N/A | `AddBMSDirCommandFromMainWindow` |
| プレイリスト tree context menu | 一覧をエクスポート JSON: header | WPF `SaveFileDialog` | File save | playlist header JSON | `Resources.Json_file_exts` | header URL basename or `header.json` | `.json` | Direct | `ExportBMSTable(headerPath, dataPath)` |
| プレイリスト tree context menu | 一覧をエクスポート JSON: data | WPF `SaveFileDialog` | File save | playlist data JSON | `Resources.Json_file_exts` | data URL basename or `data.json` | `.json` | Direct | `ExportBMSTable(headerPath, dataPath)` |
| 譜面 table context menu | 音声ファイルへ変換 | `CommonOpenFileDialog` direct | Folder | audio export directory | N/A | N/A | N/A | N/A | `ConvertBMSToAudioFiles(..., saveDir, ...)` |

JSON export save は header/data の両方に `DefaultExt = ".json"` と `AddExtension = true` を設定する。

## プレイリスト URL 読み込みダイアログ

| Screen | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| プレイリスト URL 読み込み | ローカルファイルを開く | WPF `OpenFileDialog` | File open | local playlist header JSON | `*.json` | none | `.json` | Direct | selected local file path appended to URI input text |

## 既定拡張子の点検観点

新しいファイル選択 UI を追加するときは、次を満たすこと。

- Save dialog は `DefaultExt` と `AddExtension = true` を明示する。
- Open dialog でも固定 file name がある場合は、`DefaultExt` / `DefaultExtension` が実ファイル種別と一致すること。
- `OpeningFileSelectionMessage` を使う場合、`FileName` に代表的なファイル名を入れると `DefaultExtension` はそこから推定される。
- `FileName` がない `OpeningFileSelectionMessage` は、filter の最初の pattern が既定拡張子になる。先頭 filter が総称 filter の場合は、その総称 filter の最初の拡張子が使われる。
- filter に `?` を含む wildcard は既定拡張子推定には使わない。必要なら `FileName` 側に代表名を入れる。
- Folder dialog は既定拡張子を持たない。
