# 譜面からの外部起動

## 目的と適用範囲

右クリック操作からWebページと外部プログラムを解決・起動する仕様です。保存する定義、単一譜面の条件、クリック時の再検証、失敗表示を定めます。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 保存形式と既定値

`Settings.RightClickActionsJson` は `webActions` と `programActions` をまとめた利用者設定です。配列順をメニュー順とし、スキーマ版は持ちません。設定値そのものに次の6件を既定値として持ち、新規設定と `既定値へ戻す` で使用します。保存済みの利用者設定へ後から追加された既定項目を自動マージしません。

| 固定ID | 名前 | URLテンプレート | 対象 |
| --- | --- | --- | --- |
| `bms-ir` | BMS-IR | `https://bms-ir.org/new/song?songmd5={md5}&view=both` | BMS |
| `mocha` | Mocha | `https://mocha-repository.info/song.php?sha256={sha256}` | 全形式 |
| `minir` | MinIR | `https://www.gaftalk.com/minir/#/viewer/song/{sha256}/0` | 全形式 |
| `rianir` | rianIR | `https://rianir.link/ranking?sha256={sha256}` | 全形式 |
| `stellaverse-ir` | STELLAVERSE IR | `https://ir.stellabms.xyz/charts/{md5}` | 全形式 |
| `kaleid-ir` | Kaleid IR | `https://kaleidir.com/charts/{sha256}` | 全形式 |

null、空文字、不正JSON、未知・重複項目、重複ID、不正な列挙値や設定値は型付きの解析失敗として返します。起動時に例外で停止させず、既定値へ黙って戻しません。明示的に修正・初期化するまで操作を公開せず、設定エラーを案内します。

### 編集と検証

設定画面は独立した編集用の値を持ちます。入力、並べ替え、実行ファイル選択は永続設定を変えません。保存で全体を一回検証・直列化し、既存の設定保存処理へ渡します。取消と通常の閉じる操作は編集値を破棄します。

既定に戻す操作は常に使用でき、確認を挟まず編集値だけを置換します。保存成功まで永続化しません。不正な保存値に対する案内は、編集値を有効にするまで残し、内部解析の生の文言は画面へ出しません。

Web操作は固定ID、空白でない名前、URLテンプレート、有効設定、対象譜面形式を持ちます。既定項目の名前も通常の設定データであり、サイト名を言語リソースから補完しません。`追加` は空の名前とURLテンプレートを持つ編集行を選択状態で作成し、両入力欄を直ちにエラー表示にします。保存は必要項目を入力するまで拒否します。Webテンプレートは絶対HTTP(S) URLで、大小文字を区別する `{md5}` または `{sha256}` を一つ以上含む必要があります。未知・閉じていない置換記号は拒否します。ハッシュは規定長の16進だけを小文字へ置換し、必要なハッシュがなければその操作を公開しません。

プログラムは固定ID、名前、絶対実行パス、有効設定、`{filePath}` を含む引数テンプレートを持ちます。Windowsの二重引用符とバックスラッシュ規則で先に引数へ分解し、その後に置換します。単一引用符は通常文字です。未知・不均衡な記号は拒否します。存在確認は定義解析時ではなくクリック時に行います。

### 単一譜面の解決

`RightClickActionResolutionInput` は、一行のMD5、SHA-256、ローカルの絶対譜面パス、判定できる場合の種類を持つ変更不能な入力です。複数選択や異種の集約を渡しません。解決は有効な定義の順を維持し、Web操作を種類と必要ハッシュで絞り、種類を判定できない入力では対象が全形式のWeb操作だけを返します。プログラムはローカル絶対パスがある場合だけ返します。URL、相対パス、履歴のハッシュからプログラム用パスを推測しません。

通常一覧、未所持のプレイリスト行、履歴行も同じ契約を使います。ハッシュだけの履歴はWeb操作だけを表示できます。LR2の未解決履歴はBMSとして扱い、BMS/bmsonをハッシュだけでは区別できないbeatorajaの未解決履歴は種類を未確定として扱います。ローカル譜面へ解決できた履歴には、既存の関連付け起動の直後にプログラムメニューを置きます。解決処理そのものはファイル、プロセス、ブラウザ、ダイアログに触れません。

### メニューとクリック時の起動

メニューを開くときに現在の設定を解決します。子項目には捕捉した行やURLではなく固定IDと操作の種類を保持します。クリック時はメニューの正確な対象行を取り直し、現在の設定で再解決してから起動境界へ渡します。古い操作、設定変更、不正設定、譜面・実行ファイルの欠落は起動せず、多言語のエラーにします。

Webは既存のブラウザ・シェル境界へ渡します。プログラムはクリック時に実行ファイルと譜面の絶対パスを確認し、`UseShellExecute=false`、作業ディレクトリは実行ファイルの親、引数は一つずつ `ArgumentList` に設定します。一回開始して待たず、プロセスを保持しません。標準入出力の転送、生の `Arguments`、関連付け起動への代替は使いません。

### LR2試聴時の設定保全

LR2bodyの試聴が一時変更するXML項目は、`system/windowsize_x`、`system/windowsize_y`、`system/screenmode`、`sound/volumemaster`、`sound/volumeflag` の五つです。最新XMLを読み、原子的に公開してからプロセスを開始します。

試聴中に作る設定オブジェクトは、最新XMLのその他の値と、保存しておいた五項目から構成し、一時値を編集画面へ混ぜません。通常の設定保存と検索ルート保存も同じ短い保存範囲に参加します。保存済みの五項目は元の欠落も再現し、その他の編集値を反映して公開成功後に保存値を同期します。試聴用の一時公開では保存値を変えません。

ウィンドウ設定後と終了時は、後から保存した他の値を保ったまま五項目を元の値・欠落へ戻します。開始前の失敗では `HasExited`、終了要求、強制終了を呼ばず参照と購読だけを片付けます。開始済みで終了できない場合は、明示的な終了再試行のため所有状態を保持します。開始・復元の主失敗は対象設定パスと副次失敗を含め、既存のプレイヤー失敗通知へ返します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 既定値、厳密な解析、順序・種類・ハッシュの解決 | [`RightClickActionSettingsStore`](../../../BeMusicSeeker/Models/RightClickActionSettingsStore.cs) | [`RightClickActionSettingsStoreTests`](../../../BeMusicSeeker.Tests/RightClickActionSettingsStoreTests.cs)、[`ApplicationSettingsMetadataTests`](../../../BeMusicSeeker.Tests/ApplicationSettingsMetadataTests.cs) |
| Windowsの引数分解と正確な引数列 | [`ExternalProgramArgumentTemplate`](../../../BeMusicSeeker/Models/ExternalProgramArgumentTemplate.cs) | [`ExternalProgramArgumentTemplateTests`](../../../BeMusicSeeker.Tests/ExternalProgramArgumentTemplateTests.cs) |
| 編集、既定への復帰、取消、保存と閉じる操作 | [`RightClickActionSettingsEditor`](../../../BeMusicSeeker/ViewModels/RightClickActionSettingsEditor.cs) | [`RightClickActionSettingsEditorTests`](../../../BeMusicSeeker.Tests/RightClickActionSettingsEditorTests.cs)、[`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsDialogBehaviorTests.cs)、[`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs)、[`SettingsWindowCompiledBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsWindowCompiledBehaviorTests.cs) |
| クリック時の再解決とプロセス起動条件 | [`SelectedChartExternalActionWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/SelectedChartExternalActionWorkflowOwner.cs)、[`ExternalProgramLaunchGateway`](../../../BeMusicSeeker/Models/Utils/ExternalProgramLaunchGateway.cs) | [`SelectedChartExternalActionWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/SelectedChartExternalActionWorkflowOwnerTests.cs)、[`ExternalProgramLaunchGatewayTests`](../../../BeMusicSeeker.Tests/ExternalProgramLaunchGatewayTests.cs) |
| 単一行のメニュー、履歴・未所持の区別 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow.cs) | [`MainWindowSelectedChartContextMenuWpfTests`](../../../BeMusicSeeker.Tests/MainWindowSelectedChartContextMenuWpfTests.cs)、[`MainWindowPlayHistoryWpfTests`](../../../BeMusicSeeker.Tests/MainWindowPlayHistoryWpfTests.cs) |
| 型付きの起動失敗メッセージ、多言語リソースの整合 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow.cs) | [`MainWindowContextMenuResourceTests`](../../../BeMusicSeeker.Tests/MainWindowContextMenuResourceTests.cs)、[`LocalizationResourceParityTests`](../../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs) |
| LR2試聴の五項目、他のXML値、設定編集、開始・終了失敗 | [`ExternalPlayerProcessGateway`](../../../BeMusicSeeker/Models/Utils/ExternalPlayerProcessGateway.cs) | [`ExternalPlayerProcessGatewayTests`](../../../BeMusicSeeker.Tests/ExternalPlayerProcessGatewayTests.cs)、[`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsDialogBehaviorTests.cs) |
| ubmplayの一時INI生成、値の制限、設定読み戻し | [`uBMplay`](../../../BeMusicSeeker/Models/uBMplay.cs) | [`UbmplaySettingsTests`](../../../BeMusicSeeker.Tests/UbmplaySettingsTests.cs) |

## 関連資料

[設定変更](../runtime/settings.md)、[ファイル選択](file-dialogs.md)、[プレイ履歴](../playlist/play-history.md)、[原子的な保存](../library/file-db-consistency.md)を参照します。
