# ファイル選択ダイアログ仕様

この資料は、現行実装でファイル/フォルダ選択ダイアログを開く画面と、既定拡張子の扱いを一覧化する。

対象は `UiDialogCoordinator` の `UiFilePickerRequest` / `UiFolderPickerRequest` / `UiSaveFilePickerRequest` route である。フォルダ選択ダイアログはファイル拡張子を持たないため、既定拡張子は `N/A` とする。

## 共通ルール

file picker は `UiDialogCoordinator` が処理し、filter / default extension / initial directory の変換は `UiFilePickerUtilities` に集約する。

- `DefaultFileName` は `UiFilePickerRequest.FileName` をそのまま使う。
- `DefaultExtension` は `FileName` の拡張子から推定する。
- `FileName` から推定できない場合は、最初の有効な filter pattern から推定する。
- `*.*`、`?` を含む wildcard、拡張子なし pattern は既定拡張子として使わない。
- `Filter` は `display|pattern|display|pattern` 形式を `CommonFileDialogFilter` に変換する。display が空の場合は pattern を表示名にも使う。
- `InitialDirectory` は、既存 directory ならその directory、既存 file path なら親 directory を使う。

`Default Extension Source` は次のどれかを記載する。

- `Direct`: `DefaultExt` / `DefaultExtension` をコードで直接指定している。
- `Computed from FileName`: `UiFilePickerRequest.FileName` から `UiFilePickerUtilities` が算出する。
- `Computed from Filter`: `FileName` が無く、filter pattern から算出する。
- `N/A`: folder picker など、拡張子を持たない。

## 設定ダイアログ

### 一般

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 一般 | BMS ディレクトリ追加 | `UiFolderPickerRequest` / list drop | Folder, multi-select | BMS root directories | N/A | N/A | N/A | N/A | standalone: `StandaloneBmsRootPathList`, LR2 linked: LR2 `config.xml` jukebox paths |
| 一般 | LR2 ディレクトリ参照 | `UiFolderPickerRequest` | Folder | LR2 root directory | N/A | N/A | N/A | N/A | `settingDialog.LR2RootPath` |
| 一般 | `song.db` 参照 | `UiFilePickerRequest` | File open | LR2 song DB (`*.db`) | `song.db (*.db)`, all files | `song.db` | `db` | Computed from FileName | `settingDialog.LR2SongDBPath` |
| 一般 | `config.xml` 参照 | `UiFilePickerRequest` | File open | LR2 `config.xml` / `config.xmh` | `config.xm?`, all files | `config.xml` | `xml` | Computed from FileName | `settingDialog.LR2ConfigXmlPath` |
| 一般 | beatoraja ディレクトリ参照 | `UiFolderPickerRequest` | Folder | beatoraja root directory | N/A | N/A | N/A | N/A | `settingDialog.BeatorajaRootPath` |

LR2 root picker は、現在値と同じrootを再選択した場合も、候補 root、標準配置の `LR2files\Database\song.db`、`LR2files\Config\config.xml|config.xmh`、読み込み可能な config を先に単一 tuple として解決する。候補が LR2 player root として無効、config が存在しない、または config を解析できない場合は root / song / config の3 raw draftを一切変更せず、画面に validation failure を表示する。有効な候補だけを3値へ一括反映し、以前の root に属する `song.db` を暗黙に残さない。同じrootにcustom childがある場合は標準tupleへ戻す。期待される song.db がまだ存在しない場合も新rootの標準pathを設定し、linked modeの保存validationでmissingを明示する。

標準配置から外れた既存の child path は raw value のまま互換維持し、通常画面では read-only status として表示する。advanced dialog の song/config path は親ViewModelにbindingせずdialog-local draftとし、直接入力とpickerの両方で同じlocal valueを編集する。pickerは現在のtyped valueをinitial directoryに使い、accepted candidateを同じeditorへ反映するが親draftは更新しない。Done / Enterは両editorの現在textを必ず再検証し、songとparse可能なconfigのtuple全体が有効な場合だけatomicに親draftへ反映する。advanced picker が missing / unreadable / malformed file を返した場合は、そのchildの以前のlocal valueと親draftを保持してfailureを表示する。Cancel / Esc / native closeはlocal draftを捨てるだけで親draftを変更しない。Doneは永続化せず、親設定画面の Save だけが user.config を保存する。modalとpicker requestのownerは同じ `SettingsWindow` とする。
手動編集入口は標準配置・custom配置のどちらでも常に表示し、LR2 linked modeのときだけ有効にする。初期raw値がmissing / malformedで未編集でもDoneは受理せず、dialogを開いたまま拒否されたeditorへfocusを戻す。

### 右クリック設定

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 右クリック設定 | プログラム実行ファイル参照 | `UiFilePickerRequest` | File open | selected right-click program executable | `*.exe`, all files | current executable basename or empty | `exe` when a current basename is present | Computed from FileName | selected program-action draft; a blank name is filled from the accepted executable basename |

The page owns only the selected row draft. A cancelled picker leaves that row unchanged, and an accepted path never writes `Settings.RightClickActionsJson` directly. The picker is single-select, requires an existing file and path, and uses the hosting `SettingsWindow` as owner.

`config.xm?` は LR2 互換上の意図的な filter で、`config.xml` と `config.xmh` を許可する。既定拡張子は `FileName=config.xml` から算出されるため `xml` になる。

DB 系の filter は exact filename ではなく `*.db` を使う。表示上は `song.db` / `score.db` を案内するが、LR2 の空 DB や backup DB など、ファイル名が完全一致しない `.db` も選択できるようにする。

### 外観

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 外観 | 画像ファイル参照 | `UiFilePickerRequest` | File open | stage image | `*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.tif;*.tiff`, per-format filters | none | `bmp` | Computed from Filter | `settingDialog.StagefilePath` |

既定拡張子は filter 先頭の `*.bmp` から推定される。既存 `StagefilePath` は initial directory の決定にだけ使う。

### 再生

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 再生 | `uBMplay.exe` 参照 | `UiFilePickerRequest` | File open | uBMplay executable | `uBMplay.exe`, all files | `uBMplay.exe` | `exe` | Computed from FileName | `settingDialog.uBMplayPath` |
| 再生 | `BMIIDXView2015.exe` 参照 | `UiFilePickerRequest` | File open | BMIIDXView executable | `BMIIDXView2015*.exe`, all files | `BMIIDXView2015.exe` | `exe` | Computed from FileName | `settingDialog.BMIIDXViewPath` |
| 再生 | LR2 参照 | `UiFolderPickerRequest` | Folder | LR2 root directory | N/A | N/A | N/A | N/A | `settingDialog.LR2RootPath` |

LR2 player path は実行ファイル選択ではなく LR2 root folder 選択として扱う。

### 録音

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 録音 | encoder 実行ファイル検索先参照 | `UiFolderPickerRequest` | Folder | encoder directory | N/A | N/A | N/A | N/A | `settingDialog.EncoderExeDir` |

### プレイリスト

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| プレイリスト | 通常出力先追加 | `UiFolderPickerRequest` | Folder | LR2 custom folder output directory | N/A | N/A | N/A | N/A | `settingDialog.LR2CustomFolderOutputDir` |
| プレイリスト | ルートフォルダ出力先参照 | `UiFolderPickerRequest` | Folder | LR2 custom root output directory | N/A | N/A | N/A | N/A | `settingDialog.LR2CustomFolderAsRootOutputDir` |

### インストール

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| インストール | 新規インストール先追加 | `UiFolderPickerRequest` | Folder | install destination directory | N/A | N/A | N/A | N/A | `settingDialog.BMSInstallDir` |

### バックアップ

| Tab | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| バックアップ | プレイリストのバックアップ | `UiSaveFilePickerRequest` | File save | playlist backup SQL | `*.sql` | `BeMusicSeeker_backup.sql` | `.sql` | Direct | `PlaylistWorkspace.BackupPlaylistAsync(result.FileName)` |
| バックアップ | プレイリストの復元 | `UiFilePickerRequest` | File open | playlist backup SQL | `*.sql` | `BeMusicSeeker_backup.sql` | `.sql` | Direct | `PlaylistWorkspace.RestorePlaylistBackupAsync(result.FileName)` |
| バックアップ | LR2 backup 保存先参照 | `UiFolderPickerRequest` | Folder | LR2 backup directory | N/A | N/A | N/A | N/A | `settingDialog.LR2BackupPath` |

backup save は `AddExtension = true` を明示する。restore は existing file open なので補完目的ではないが、file name / filter と合わせて `DefaultExt = ".sql"` を設定する。

## メインウィンドウ

| Screen | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| ライブラリ root context menu | ルートフォルダ追加 | `UiFolderPickerRequest` | Folder | BMS root directory | N/A | N/A | N/A | N/A | `AddBmsSearchRootPathFromMainWindowPicker` |
| プレイリスト tree context menu | 一覧をエクスポート JSON: header | `UiSaveFilePickerRequest` | File save | playlist header JSON | `Resources.Json_file_exts` | header URL basename or `header.json` | `.json` | Direct | `PlaylistWorkspace.ExportPlaylistTableAsync(bmsTable)` |
| プレイリスト tree context menu | 一覧をエクスポート JSON: data | `UiSaveFilePickerRequest` | File save | playlist data JSON | `Resources.Json_file_exts` | data URL basename or `data.json` | `.json` | Direct | `PlaylistWorkspace.ExportPlaylistTableAsync(bmsTable)` |
| 譜面 table context menu | 音声ファイルへ変換 | `UiFolderPickerRequest` | Folder | audio export directory | N/A | N/A | N/A | N/A | `ConvertBMSToAudioFiles(..., saveDir, ...)` |

JSON export save は header/data の両方に `DefaultExt = ".json"` と `AddExtension = true` を設定する。

## プレイリスト URL 読み込みダイアログ

| Screen | UI | Dialog | Kind | Target | Filter | Default FileName | Default Extension | Default Extension Source | Selection Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| プレイリスト URL 読み込み | ローカルファイルを開く | `UiFilePickerRequest` | File open | local playlist header JSON | `*.json` | none | `.json` | Direct | selected local file path appended to URI input text |

## 既定拡張子の点検観点

新しいファイル選択 UI を追加するときは、次を満たすこと。

- Save dialog は `DefaultExt` と `AddExtension = true` を明示する。
- Open dialog でも固定 file name がある場合は、`DefaultExt` / `DefaultExtension` が実ファイル種別と一致すること。
- `UiFilePickerRequest.FileName` に代表的なファイル名を入れると `DefaultExtension` はそこから推定される。
- `FileName` がない `UiFilePickerRequest` は、filter の最初の pattern が既定拡張子になる。先頭 filter が総称 filter の場合は、その総称 filter の最初の拡張子が使われる。
- filter に `?` を含む wildcard は既定拡張子推定には使わない。必要なら `FileName` 側に代表名を入れる。
- Folder dialog は既定拡張子を持たない。
