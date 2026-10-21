# ファイルとフォルダの選択

## 目的と適用範囲

`UiDialogCoordinator` を通す選択画面の対象、既定名、拡張子、結果の反映先を定めます。設定の検証・保存は[設定変更](../runtime/settings.md)に従います。通常項目は選択完了で当該項目だけを保存し、音声・再生方式・外部画像・スコアは有効な組が成立すると当該組だけを保存します。環境・BMS・BMT・recordは局所の明示確定を使います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 共通規則

ファイル選択には `UiFilePickerRequest`、フォルダ選択には `UiFolderPickerRequest`、保存には `UiSaveFilePickerRequest` を使います。形式指定、既定拡張子、初期ディレクトリの変換は `UiFilePickerUtilities` に集約します。

既定名は要求の `FileName` を使い、拡張子はまず名前から、なければ最初の有効な形式パターンから推定します。`*.*`、`?` を含むパターン、拡張子のないパターンは推定に使いません。保存では `DefaultExt` と `AddExtension=true` を明示します。フォルダ選択に拡張子はありません。

形式は `表示名|パターン|表示名|パターン` を `CommonFileDialogFilter` へ変換し、表示名が空ならパターンを表示名にします。初期パスが既存フォルダならそのフォルダ、既存ファイルなら親を使います。

### 設定画面の選択先

| 設定 | 種類・対象 | 形式と既定名 | 拡張子の根拠 | 反映先 |
| --- | --- | --- | --- | --- |
| BMSルートの追加 | 複数フォルダ | フォルダ選択または一覧へのドロップ | なし | BMS一覧の局所候補。確定時に単独動作の保存値またはLR2のjukeboxへ反映 |
| LR2の場所 | フォルダ | LR2ルート | なし | ルート・楽曲DB・設定XMLの編集用の組 |
| LR2の楽曲DB | ファイル | `*.db` と全ファイル、`song.db` | 名前から `db` | `LR2SongDBPath` の編集値 |
| LR2の設定XML | ファイル | `config.xm?` と全ファイル、`config.xml` | 名前から `xml` | `LR2ConfigXmlPath` の編集値 |
| beatorajaの場所 | フォルダ | beatorajaルート | なし | 環境組の`BeatorajaRootPath`候補。解決したplayer・score.db・BMT出力先を同じ欄に表示し、組で確定 |
| 右クリックのプログラム | 単一ファイル | `*.exe` と全ファイル、現在の実行ファイル名 | 名前または形式から `exe` | 選択中の操作の編集値。空の表示名だけをファイル名で補う |
| 外観の画像 | ファイル | `*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.tif;*.tiff` と個別形式、既定名なし | 最初の形式から `bmp` | 画像組の`StagefilePath`候補。現在値は初期フォルダにだけ使う |
| uBMplay | ファイル | `uBMplay.exe` と全ファイル、`uBMplay.exe` | 名前から `exe` | 再生方式組の`uBMplayPath`。有効な組の成立で自動確定 |
| BMIIDXView | ファイル | `BMIIDXView2015*.exe` と全ファイル、`BMIIDXView2015.exe` | 名前から `exe` | 再生方式組の`BMIIDXViewPath`。有効な組の成立で自動確定 |
| LR2再生先 | フォルダ | LR2ルート | なし | 環境組の`LR2RootPath`候補 |
| 録音エンコーダー | フォルダ | 実行ファイルの検索先 | なし | `EncoderExeDir`だけを保存して反映 |
| 通常のプレイリスト出力 | フォルダ | LR2のカスタムフォルダ出力先 | なし | 通常出力先を選択し既存移行確認で確定 |
| ルート形式の出力 | フォルダ | ルート形式の出力先 | なし | ROOT出力先を選択し既存移行確認で確定 |
| 新規導入先 | フォルダ | 導入先 | なし | `BMSInstallDir`だけを保存して反映 |
| プレイリストのバックアップ | 保存 | `*.sql`、`BeMusicSeeker_backup.sql` | 明示 `.sql`、拡張子補完あり | バックアップ処理 |
| プレイリストの復元 | ファイル | `*.sql`、`BeMusicSeeker_backup.sql` | 明示 `.sql` | 復元処理 |
| LR2バックアップ先 | フォルダ | 保存先 | なし | `LR2BackupPath`だけを保存して反映 |

`config.xm?` はXMLとXMHの両方を許す意図的な指定です。DBは表示上の代表名だけに限定せず、バックアップなど別名の `.db` を選択できます。

LR2ルートの選択は、現在と同じルートでも標準の子パスと読める設定XMLを先に検証します。無効なら三つの編集値を全て保持し、有効なら一括変更します。`song.db` と設定XMLは一般ページの初期閉状態の詳細欄から個別に選択できますが、ファイル選択を開く前に標準配置の利用を促す確認を行い、同意時だけ選択へ進みます。個別選択は対象パスだけへ反映し、取消では編集値を変えません。詳細は設定仕様に集約します。

右クリック操作の実行ファイル選択は、存在する一ファイルを要求し、所有ウィンドウは設定画面です。取消で編集値を変えず、選択結果を `RightClickActionsJson` へ直接保存しません。

### その他の画面

| 入口 | 種類・対象 | 既定名・拡張子 | 結果 |
| --- | --- | --- | --- |
| プレイリストのJSON出力 | ヘッダーとデータを個別に保存 | 元URLの名前または `header.json` / `data.json`、明示 `.json`、補完あり | 出力処理へ渡す |
| 音声ファイルへの変換 | 出力フォルダ | 拡張子なし | 音声変換へ渡す |
| プレイリストURL読込みのローカル選択 | ヘッダーJSON | `*.json`、明示 `.json` | URI入力欄へローカルパスを追加する |

プレイリストURL読込みのローカル選択では、形式の表示名は翻訳に従います。機能テストは要求のフィルターを `Resources.Json_file_exts` と比較し、表示名の訳文を固定しません。一方、形式は一組の `表示名|*.json` とし、既定拡張子 `.json` と合わせて翻訳とは独立に検証します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| プレイリストインポートのローカル選択 | [`PlaylistImportDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistImportDialog.cs) | [`PlaylistImportDialogTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistImportDialogTests.cs) は、翻訳リソースの選択とJSON形式、既定拡張子、所有者、承諾時の入力追加、取消・失敗時の入力保持、非同期の完了を確認する。 |
| 形式、拡張子、初期パスの共通変換 | [`UiFilePickerUtilities`](../../../BeMusicSeeker/Views/Dialogs/UiFilePickerUtilities.cs)、[`UiDialogCoordinator`](../../../BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs) | [`WpfPickerBoundaryTests`](../../../BeMusicSeeker.Tests/Dialogs/WpfPickerBoundaryTests.cs) |
| LR2のルートと個別パス、確認・取消時の保持 | [`SettingsWindow`](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs) | [`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs)、[`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs)、[`SettingsWindowCompiledBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowCompiledBehaviorTests.cs) |
| 右クリック操作の編集用選択 | [`RightClickActionSettingsEditor`](../../../BeMusicSeeker/ViewModels/Settings/RightClickActionSettingsEditor.cs) | [`RightClickActionSettingsEditorTests`](../../../BeMusicSeeker.Tests/ExternalActions/RightClickActionSettingsEditorTests.cs)、[`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs) |

## 関連資料

[ダイアログの境界](dialogs.md)、[設定変更](../runtime/settings.md)、[外部起動](external-launch.md)を参照します。
