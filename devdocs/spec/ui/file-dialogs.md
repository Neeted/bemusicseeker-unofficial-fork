# ファイルとフォルダの選択

## 目的と適用範囲

表示窓口 `UiDialogCoordinator` を通す選択画面について、対象、既定名、拡張子、結果の反映先を定めます。表示の経路と失敗の扱いは[ダイアログの表示境界](dialogs.md)、設定値の検証・保存の単位は[設定の項目の保存](../runtime/settings.md#項目の保存)と[局所候補](../runtime/settings.md#局所候補)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。

| 用語 | 意味 |
| --- | --- |
| 形式指定 | 選択画面で選べるファイルの種類の一覧。表示名とパターンの組を並べる。 |
| 既定拡張子 | 拡張子なしで入力された名前に補う拡張子。 |

形式指定は `表示名|パターン|表示名|パターン` の形の文字列で表します。

## 仕様

### 要求の種類

| 選択 | 要求 |
| --- | --- |
| 開くファイル | `UiFilePickerRequest` |
| フォルダ | `UiFolderPickerRequest` |
| 保存先のファイル | `UiSaveFilePickerRequest` |

既定名は要求の `FileName` を使います。フォルダ選択には拡張子がありません。

### 開くダイアログの整形と補完

開くファイル・フォルダの選択では、要求の値を `UiFilePickerUtilities` で次のように整形・補完してから表示します。

- **形式指定**: 表示名が空の組はパターンを表示名にします。パターンが空の組は除き、有効な組が一つもなければ全ファイル（`*.*`）にします。
- **既定拡張子**: 要求に既定拡張子がなければ、まず既定名から、なければ最初に見つかる有効なパターンから推定します。`*.*`、`?` を含むパターン、拡張子を特定できないパターンは推定に使いません。既定拡張子が決まった場合は、拡張子の補完を有効にします。
- **初期フォルダ**: 指定したパスが存在するフォルダならそのフォルダを使います。そうでなければ、親フォルダが存在する場合だけ親フォルダを使います（パス自体は存在しなくても構いません）。どちらでもなければ指定しません。

### 保存ダイアログ

保存先の選択では、要求の形式指定、既定名、既定拡張子、拡張子の補完の有無をそのまま使い、上記の整形・補完は行いません。呼出元が完全な形式指定と既定拡張子を渡します。

### 設定画面の選択先

| 設定 | 種類・対象 | 形式と既定名 | 拡張子の根拠 | 反映先 |
| --- | --- | --- | --- | --- |
| BMSルートの追加 | 複数フォルダ | フォルダ選択または一覧へのドロップ | なし | BMS一覧の局所候補。確定時に単独動作の保存値またはLR2のjukeboxへ反映する。 |
| LR2の場所 | フォルダ | LR2ルート | なし | ルート・楽曲DB・設定XMLの編集用の組。 |
| LR2の楽曲DB | ファイル | `*.db` と全ファイル、`song.db` | 名前から `db` | `LR2SongDBPath` の編集値。 |
| LR2の設定XML | ファイル | `config.xm?` と全ファイル、`config.xml` | 名前から `xml` | `LR2ConfigXmlPath` の編集値。 |
| beatorajaの場所 | フォルダ | beatorajaルート | なし | 環境の組の `BeatorajaRootPath` 候補。解決したplayer・score.db・BMT出力先を同じ欄に表示し、組で確定する。 |
| 右クリックのプログラム | 単一ファイル | `*.exe` と全ファイル、現在の実行ファイル名 | 名前または形式から `exe` | 選択中の操作の編集値。空の表示名だけをファイル名で補う。 |
| 外観の画像 | ファイル | `*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.tif;*.tiff` と個別形式、既定名なし | 最初の形式から `bmp` | 画像の組の `StagefilePath` 候補。現在値は初期フォルダにだけ使う。 |
| uBMplay | ファイル | `uBMplay.exe` と全ファイル、`uBMplay.exe` | 名前から `exe` | 再生方式の組の `uBMplayPath`。有効な組が成立すると自動で確定する。 |
| BMIIDXView | ファイル | `BMIIDXView2015*.exe` と全ファイル、`BMIIDXView2015.exe` | 名前から `exe` | 再生方式の組の `BMIIDXViewPath`。有効な組が成立すると自動で確定する。 |
| LR2再生先 | フォルダ | LR2ルート | なし | 環境の組の `LR2RootPath` 候補。 |
| 録音エンコーダー | フォルダ | 実行ファイルの検索先 | なし | `EncoderExeDir` だけを保存して反映する。 |
| 通常のプレイリスト出力 | フォルダ | LR2のカスタムフォルダ出力先 | なし | 通常出力先を選び、既存の出力の移行確認で確定する。 |
| ルート形式の出力 | フォルダ | ルート形式の出力先 | なし | ROOT出力先を選び、既存の出力の移行確認で確定する。 |
| 新規導入先 | フォルダ | 導入先 | なし | `BMSInstallDir` だけを保存して反映する。 |
| プレイリストのバックアップ | 保存 | `*.sql`、`BeMusicSeeker_backup.sql` | 要求で `.sql` を指定、補完あり | バックアップ処理。 |
| プレイリストの復元 | ファイル | `*.sql`、`BeMusicSeeker_backup.sql` | 要求で `.sql` を指定 | 復元処理。 |
| LR2バックアップ先 | フォルダ | 保存先 | なし | `LR2BackupPath` だけを保存して反映する。 |

`config.xm?` は、XMLとXMHの両方を許す意図的な指定です。楽曲DBは表示上の代表名に限定せず、バックアップなど別名の `.db` も選択できます。

LR2の場所の選択と、`song.db`・設定XMLの個別選択の検証・採用は、[LR2の場所と設定XML](../runtime/settings.md#lr2の場所と設定xml)に従います。この文書の範囲では、次を保証します。

- LR2ルートの選択は、現在と同じルートでも、標準の子パスと読める設定XMLを先に検証します。無効なら三つの編集値をすべて保持し、有効なら三つを一括で変更します。
- `song.db` と設定XMLは、一般ページで初期状態では閉じている詳細欄から個別に選択できます。ファイル選択を開く前に標準配置の利用を促す確認を行い、同意した場合だけ選択へ進みます。
- 個別選択は対象のパスだけへ反映し、確認・選択の取消では編集値を変えません。

右クリック操作の実行ファイル選択は、存在する一つのファイルを要求し、親ウィンドウは設定画面です。取消では編集値を変えず、選択結果を `RightClickActionsJson` へ直接保存しません。編集値の保存は[外部起動の編集と検証](external-launch.md#編集と検証)に従います。

### その他の画面

| 入口 | 種類・対象 | 既定名・拡張子 | 結果 |
| --- | --- | --- | --- |
| プレイリストのJSON出力 | ヘッダーとデータを個別に保存 | 現在の入力、なければ元URLの名前または `header.json` / `data.json`。要求で `.json` を指定、補完あり | 出力処理へ渡す。 |
| 音声ファイルへの変換 | 出力フォルダ | 拡張子なし | 音声変換へ渡す。 |
| プレイリストURL読込みのローカル選択 | ヘッダーJSON | `*.json`、要求で `.json` を指定 | URI入力欄へローカルパスを追加する。 |

プレイリストURL読込みのローカル選択では、形式指定は一組の `表示名|*.json` とし、表示名は翻訳リソース `Resources.Json_file_exts` に従います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 開くダイアログの形式指定・既定拡張子・初期フォルダの整形 | [`UiFilePickerUtilities`](../../../BeMusicSeeker/Views/Dialogs/UiFilePickerUtilities.cs)、[`UiDialogCoordinator`](../../../BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs) の `CreateOpenFileDialog` / `CreateOpenFolderDialog` | [`WpfPickerBoundaryTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Dialogs/WpfPickerBoundaryTests.cs)、[`WpfPickerBoundaryTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/WpfPickerBoundaryTests.cs) |
| 保存ダイアログへの要求値の受渡し | `UiDialogCoordinator.CreateSaveFileDialog` | 要求値をそのまま渡し、整形・補完しないことは静的確認で扱う。 |
| プレイリストURL読込みのローカル選択 | [`PlaylistImportDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistImportDialog.cs) | [`PlaylistImportDialogTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistImportDialogTests.cs): 形式指定を `Resources.Json_file_exts` と比較して訳文を固定せずに確認し、JSON形式、既定拡張子、親ウィンドウ、承諾時の入力追加、取消・失敗時の入力保持、非同期の完了を確認する。 |
| LR2のルートと個別パス、確認・取消時の保持 | [`SettingsWindow`](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs) | [`SettingsDialogBehaviorTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsDialogBehaviorTests.cs)、[`SettingsWindowPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowPresentationTests.cs)、[`SettingsWindowCompiledBehaviorTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowCompiledBehaviorTests.cs) |
| 右クリック操作の編集用選択 | [`RightClickActionSettingsEditor`](../../../BeMusicSeeker/ViewModels/Settings/RightClickActionSettingsEditor.cs) | [`RightClickActionSettingsEditorTests`](../../../tests/BeMusicSeeker.Core.Tests/ExternalActions/RightClickActionSettingsEditorTests.cs)、[`SettingsWindowPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowPresentationTests.cs) |

## 関連資料

[ダイアログの表示境界](dialogs.md)、[設定の編集と反映](../runtime/settings.md)、[外部起動](external-launch.md)。
