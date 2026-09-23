# ダイアログの表示境界

## 目的と適用範囲

通知、確認、ファイル選択、進捗、子ウィンドウ、重ね合わせ表示を、共通の表示窓口へ接続します。表示の親ウィンドウ、UIスレッド、完了・失敗を明示し、機能側に直接表示や隠れた代替経路を増やしません。

## 用語

共通語は[用語集](../glossary.md)を参照します。「親ウィンドウ」はWPFの `Owner`、「表示窓口」は `IUiDialogService` とその実装 `UiDialogCoordinator`、「緊急表示」は通常の表示基盤を使えない場合の `EmergencyDialog` を指します。

## 仕様

### 共通の表示経路

機能側は、通知・確認・選択・進捗・子ウィンドウの意図を型付きの要求として渡します。`MainWindowViewModel` の通常通知・確認は共通表示窓口へ接続し、画面固有の非通知操作は型付きイベントで `MainWindow` へ渡します。文字列のメッセージキーから処理内容を選ぶ経路は設けません。

`MainWindow` は進捗を `RunWithProgressAsync`、ファイル・フォルダ選択を表示窓口、子ウィンドウを `ShowWindowAsync` へ渡します。重ね合わせ表示は `ShowOverlayDialog` / `HideOverlayDialog` で管理し、初期言語画面から設定画面への遷移も明示します。

ライブラリ操作は [BmsLibraryDialogService](../../../BeMusicSeeker/Models/BmsLibraryInternal/Dialogs/BmsLibraryDialogService.cs) と[変更結果の表示](../library/mutations.md)の境界に従います。表示処理の統一を理由に、モデルの排他権を保持したまま画面の応答を待ちません。

### 直接表示を許可する実装

下表は表示部品の実装境界です。一般の機能コードが直接 `ShowDialog`、標準メッセージボックス、選択画面、進捗画面を呼ぶことは許可しません。構造検証は、本番コードで検出した直接呼出しを含むファイル集合と、この表を比較します。

| ファイル | 責務 |
| --- | --- |
| `BeMusicSeeker/Views/ThemedMessageBox.cs` | テーマに従うメッセージ画面の表示部品。 |
| `BeMusicSeeker/Views/Dialogs/EmergencyDialog.cs` | 通常の表示基盤を使えない場合の標準メッセージボックス。 |
| `BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs` | 進捗、選択、子ウィンドウ等の実表示。 |

### 要求と失敗の扱い

要求には意図、親ウィンドウ、スレッド境界、結果と失敗の表し方を定めます。通知の失敗を成功や取消へ読み替えず、別の表示手段へ隠れて切り替えません。`UiDialogRoute` は共通窓口へ接続する同期用の補助であり、表示失敗を隠しません。

通常終了時の一時コピー削除通知は共通経路を使いますが、ファイナライザーから画面を表示しません。ファイナライザーの後片付け失敗はログへ残します。`App` の緊急表示は `EmergencyDialog` に限定し、通常画面からの緊急経路の流用は認めません。

### 設定画面と子ウィンドウ

設定画面は、親を必須とする `ShowWindowAsync` のモーダル表示です。設定内の選択・通知・子ウィンドウでは、表示中の `SettingsWindow` を親とし、`MainWindow` へ勝手に戻しません。

入力検証の同期通知も、確認・非同期通知と同じ注入済みの表示窓口を使います。通知ごとに調停処理を作り直しません。表示失敗では拒否した入力を採用せず、失敗を呼出元へ伝えます。

初回設定の保存完了通知は、設定内の通知ではなく設定画面終了後の案内です。設定画面とそのモーダル表示の後片付けを終えてから `MainWindow` を親として表示します。通知終了までは初期化を開始しません。初期化などの失敗で設定を再表示する場合は、保存中状態を解除した呼出元からUIキューへ要求し、前の処理が新しい画面の終了を待たないようにします。

LR2の `song.db` と設定XMLの個別参照は一般ページで行います。各参照は、標準配置を使うよう促す確認を同じ注入済みサービスから表示し、同意された場合だけファイル選択を開きます。確認の取消・閉じる操作ではファイル選択へ進まず、編集値を変えません。一般ページのクリック処理は非同期待機し、イベントハンドラー以外へ `async void` を広げません。

LR2個別参照の確認・選択で表示失敗が起きた場合は、元の例外と内部の経路・状態をログへ残したうえで、同じ注入済みサービスと設定ウィンドウから一般向けの翻訳済み通知を一度行います。内部診断文字列を利用者向け文言へ付加しません。通知も失敗した場合はログだけに留め、再帰通知や代替経路を使いません。

バージョン情報からの `ReleaseNotesWindow` も、注入された `IUiDialogService.ShowWindowAsync` だけで開きます。親は `SettingsWindow` で、親なし・非モーダルの代替はありません。更新履歴を閉じても、設定の編集値を適用・初期化したり設定画面を閉じたりしません。

## 実装とテストの対応

| 仕様項目 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 直接表示を行うファイルの限定 | [UiDialogCoordinator](../../../BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs)、上記の表示部品 | [DialogRouteConsolidationTests](../../../BeMusicSeeker.Tests/Dialogs/DialogRouteConsolidationTests.cs) の `DirectDialogRouteFiles_MatchDocumentedBoundaries`: 検出対象のファイル集合と表が一致すること。正規表現・比較対象は実装側を参照する。 |
| 緊急表示と通常表示の分離 | [EmergencyDialog](../../../BeMusicSeeker/Views/Dialogs/EmergencyDialog.cs) | 同テスト群: コンパイル後の呼出し関係から、緊急表示を使う境界を確認する。 |
| 設定内の選択・子画面と親子関係 | [SettingsWindow](../../../BeMusicSeeker/Views/Settings/SettingsWindow.xaml)、[UiDialogCoordinator](../../../BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs) | [SettingDialogEditCompletionTests](../../../BeMusicSeeker.Tests/Settings/SettingDialogEditCompletionTests.cs)、[SettingsWindowPresentationTests](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs)、[SettingsControlPresentationTests](../../../BeMusicSeeker.Tests/Settings/SettingsControlPresentationTests.cs): 取消、確認、親ウィンドウ、公開Automationによる選択を確認する。実フォーカスは[入力操作の明示受入](../development/testing.md#入力操作の明示受入)で確認する。 |
| 設定の入力検証通知、表示失敗と入力保持 | [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) の `ShowUiMessage` | [SettingDialogEditCompletionTests](../../../BeMusicSeeker.Tests/Settings/SettingDialogEditCompletionTests.cs) の `CustomFolderOutputValidation_UsesInjectedDialogAndPreservesRejectedValue`: 注入済み窓口への一度の通知、表示成功・失敗・親なしの結果、拒否値と保存済みXMLの不変。 |
| 設定終了後の保存完了通知と再表示 | [MainWindow](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[SettingsWindow](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs) | [SettingsWindowPresentationTests](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs) の `MainWindow_InitialSettingsCloseBeforeRealCompletionMessageAndRecoverAfterInitialization`: 実通知の表示と親、旧画面の終了、新しい設定画面の編集受付。 |

## 関連資料

[ファイル選択](file-dialogs.md)、[外観](appearance.md)、[設定](../runtime/settings.md)、[ライブラリ変更](../library/mutations.md)。
