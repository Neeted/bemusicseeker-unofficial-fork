# ダイアログの表示境界

## 目的と適用範囲

通知、確認、ファイル選択、進捗、子ウィンドウ、重ね合わせ表示を、共通の表示窓口へ接続する境界を定めます。各要求について、親ウィンドウ、UIスレッド、完了と失敗の表し方を明示し、機能側が直接表示や隠れた代替経路を持たないようにします。

選択画面の対象・既定名・拡張子は[ファイルとフォルダの選択](file-dialogs.md)、表示部品の外観は[外観](appearance.md)に従います。

## 用語

共通語は[用語集](../glossary.md)を参照します。

| 用語 | 意味 |
| --- | --- |
| 親ウィンドウ | WPFの `Owner`。モーダル表示の所有元。 |
| 表示窓口 | `IUiDialogService` と、その実装 `UiDialogCoordinator`。 |
| 緊急表示 | 通常の表示基盤を使えない場合に `App` だけが使う `EmergencyDialog`。 |
| 所有する入口 | 確認や通知を伴う操作を受け付け、その結果と失敗の案内に責任を持つ画面側の入口。 |

## 仕様

### 共通の表示経路

機能側は、通知・確認・選択・進捗・子ウィンドウの意図を型付きの要求として表示窓口へ渡します。文字列のメッセージキーから処理内容を選ぶ経路は設けません。

| 呼出元 | 経路 |
| --- | --- |
| `MainWindowViewModel` の通常の通知・確認 | 表示窓口。画面固有の非通知操作は、型付きのイベントで `MainWindow` へ渡す。 |
| `MainWindow` の進捗 | `RunWithProgressAsync` |
| `MainWindow` のファイル・フォルダ選択 | 表示窓口の選択要求 |
| モーダルの子ウィンドウ | `ShowWindowAsync` |
| 重ね合わせ表示 | `ShowOverlayDialog` / `HideOverlayDialog`。初期言語の画面から設定画面への遷移も、この経路で明示的に扱う。 |

ライブラリ操作の表示は、[BmsLibraryDialogService](../../../BeMusicSeeker/Models/BmsLibraryInternal/Dialogs/BmsLibraryDialogService.cs)と[変更結果の表示](../library/mutations.md)の境界に従います。表示経路を統一するために、モデルの排他権を保持したまま画面の応答を待つことはしません。

### 直接表示を許可する実装

次の表は直接表示を許可する範囲です。一般の機能コードは、`ShowDialog`、標準のメッセージボックス、選択画面、進捗画面を直接呼びません。構造検証は、本番コードで直接呼出しを含むファイルの集合を、この表と比較します。

| ファイル | 責務 |
| --- | --- |
| `BeMusicSeeker/Views/ThemedMessageBox.cs` | テーマに従うメッセージ画面の表示部品。 |
| `BeMusicSeeker/Views/Dialogs/EmergencyDialog.cs` | 通常の表示基盤を使えない場合の標準メッセージボックス。 |
| `BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs` | 進捗、選択、子ウィンドウなどの実際の表示。 |

### 要求と失敗の扱い

表示要求ごとに、意図、親ウィンドウ、スレッド境界、結果と失敗の表し方を定めます。表示の失敗を成功や取消に読み替えず、別の表示手段へ暗黙に切り替えません。`UiDialogRoute` は共通窓口へ接続するための同期用の補助であり、表示の失敗を隠しません。

#### 確認の結果

| 条件 | 扱い |
| --- | --- |
| 確認の表示に失敗した | 承認にも利用者の取消にも置き換えず、その操作を開始しない。 |
| 列の初期化 | 明示的なOKだけで実行する。 |
| フォルダー全体の確認 | Yesは全体、Noは譜面のみ、Cancel・閉じる・Escは全操作の取消。 |

#### 失敗の案内

確認または本体の失敗は、所有する入口で一度だけ案内します。この案内は任意通知であり、案内自体が失敗しても、元の結果、確定済みの変更、編集値の意味を変えません。

所有する入口は、確認や表示の呼出しより前から、開始した実際のTaskの終端までを保護します。通知の監視で開始済みのTaskの終端を代用しません。この規則は次の入口にも適用します。

- 検索ルートの選択
- 子の編集画面
- ランプビューアの初回表示
- サマリーの一括編集で行う外部同期の確認。確認、実際の適用（`applyTask`）、必要な回収、再有効化までを画面の閉鎖抑止の範囲に含める。

一時試聴のコピーが残った場合の通知と局所診断は、[音声の一時導入試聴のコピーと回収](../runtime/audio.md#コピーと回収)に従います。

#### 緊急表示

`App` の緊急表示は `EmergencyDialog` に限ります。通常画面から緊急経路を流用しません。

### 独立した非モーダルウィンドウ

ランプビューアと[譜面プロパティ](chart-properties.md)は、画面側の専用の管理主体（`PlaylistLampViewerWindowManager`、`ChartPropertiesWindowManager`）が、`Show` の呼出し、窓の寿命、開始済みの処理の寿命を所有します。共通のモーダル表示APIに非モーダルの意味を混ぜません。

| 画面 | 管理主体が所有するもの |
| --- | --- |
| ランプビューア | 集計と、その取得処理の回収。 |
| 譜面プロパティ | 要求ごとの新しい窓と一回の読取り。初期配置のときだけ一時的な親ウィンドウを使い、表示後は親を外してタスクバーに個別表示する。 |

譜面プロパティの表示に失敗した場合は、作成済みのWindowを閉じて回収し、終了の受付前に限り、画面の枠組みへ一度だけ通知します。終了後の遅れた通知を抑止しても、元の原因のログと開始済みの処理の回収は省きません。取得・解析の失敗は、プロパティ画面の中で扱います。

### 確認ボタンの言語と応答

通常の確認画面（`ThemedMessageBox`）のOK・Yes・No・Cancelは、表示時点の言語リソースを実際のボタンの文言に使います。

- OK・Yes・Noは確認ボタン専用のリソース、Cancelは共通の取消のリソースを使います。
- 言語を変更すると、次に表示する確認画面から新しい文言になります。表示中の確認画面は言語変更を購読せず、翻訳のキャッシュも持ちません。
- 翻訳は、既定の応答、明示的な応答、Enter・Esc・閉じる操作の意味を変えません。

表プロパティと進捗ダイアログの取消ボタンは `Resources.Cancel` を参照し、新しく開いた画面で現在の言語を表示します。取消の動作、`AutomationId`、Esc、通常の閉鎖は言語に依存しません。

### 保留復元の失敗時の選択画面

保留の復元で未確定の失敗が起きた場合は、専用の `PendingPackageRestoreWindow` を `ShowWindowAsync` で表示し、「再試行」「保留一覧を初期化して続行」「終了」を明示的な値として区別します。

- 本文で、すべての保留登録を消去することと、元のファイルおよび導入済みの楽曲をこの操作では削除しないことを示します。
- 一覧の初期化は、`IsAccepted` と明示的なResetの両方を要求します。×とEscには承認の値を与えません。
- 復元と一覧初期化の元の原因は、画面と既存のログへ渡します。DB確定後の障害や表示不能を、この選択へ再分類しません。
- 選択の範囲と終了の引渡しは、[起動の保留復元](../runtime/startup.md#保留復元の失敗時選択)に従います。

### 操作中に確定した通知の言語

プレイリスト操作の途中で確定し、操作の終端で初めて表示する通知は、確定時点ではなく表示直前の言語で本文と見出しを整形します。対象は、リコメンドの実力値の更新、カスタムフォルダ出力の警告、JSON出力・バックアップ・復元・BMT出力・ドロップ・Level入力の結果などです。

通知は次の値を変更不能な事実として保持し、表示窓口が一件ごとに表示直前に整形します。

- 重要度
- 見出しと本文のリソース名
- 翻訳の対象でない書式引数（パス、表名、Invariant書式の数値）
- 例外の本文などの詳細

例外の本文などの診断は翻訳せず、作成時点の文字列のまま保持します。そのため、失敗から表示までの間に言語を変えると、外側の文は表示時の言語、例外の本文は作成時の言語になることがあります。表示中のモーダルは言語の変更に追従せず、通知のために元の処理を再実行しません。

### 設定画面と子ウィンドウ

設定画面は、親を必須とする `ShowWindowAsync` のモーダル表示です。設定画面内の選択・通知・子ウィンドウは、表示中の `SettingsWindow` を親とし、`MainWindow` を親に戻しません。

| 表示 | 契約 |
| --- | --- |
| 入力検証の同期通知 | 確認や非同期の通知と同じ、注入済みの表示窓口を使い、通知のたびに調停処理を作り直さない。表示に失敗した場合は、拒否した入力を採用せず、失敗を呼出元へ伝える。 |
| LR2の `song.db` と設定XMLの個別参照 | 一般ページで行う。標準の配置を使うよう促す確認を同じ注入済みの表示窓口から表示し、同意された場合だけファイル選択を開く。確認の取消と閉じる操作では選択へ進まず、編集値を変えない。 |
| バージョン情報からの `ReleaseNotesWindow` | 注入された `IUiDialogService.ShowWindowAsync` だけで開き、親は `SettingsWindow`。親なし・非モーダルの代替はない。閉じても、設定の編集値の適用・初期化や設定画面の閉鎖を行わない。 |

LR2の個別参照で確認や選択の表示に失敗した場合は、元の例外と内部の経路・状態をログへ残し、同じ表示窓口と設定ウィンドウから、翻訳済みの一般向けの通知を一度だけ行います。内部の診断文字列を利用者向けの文言に付加しません。この通知も失敗した場合はログだけに留め、再帰的な通知や代替経路は使いません。一般ページのクリック処理は非同期で待機し、イベントハンドラー以外へ `async void` を広げません。

初回設定と修復の流れは、[設定の初回と修復](../runtime/settings.md#初回と修復)と[起動の完了境界](../runtime/startup.md#起動の流れと完了境界)に従います。

## 実装とテストの対応

| 仕様項目 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 直接表示を行うファイルの限定 | [UiDialogCoordinator](../../../BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs)、上記の表示部品 | [DialogRouteConsolidationTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogRouteConsolidationTests.cs) の `DirectDialogRouteFiles_MatchDocumentedBoundaries`: 直接呼出しを含むファイルの集合が表と一致すること。検出規則は実装側を参照する。 |
| 緊急表示と通常表示の分離 | [EmergencyDialog](../../../BeMusicSeeker/Views/Dialogs/EmergencyDialog.cs) | 同クラスの `StandardMessageBox_IsLimitedToEmergencyDialog`: コンパイル後の呼出し関係で、標準メッセージボックスの呼出しが `EmergencyDialog` だけにあり、その呼出元が `App` だけであること。 |
| 確認ボタンの翻訳と応答の意味 | [ThemedMessageBox](../../../BeMusicSeeker/Views/ThemedMessageBox.cs) | [DialogPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs): 6言語の実際のボタン文言、言語変更後の次の表示、4構成の明示応答・Enter・Esc。[ThemedMessageBoxTests](../../../tests/BeMusicSeeker.Core.Tests/Dialogs/ThemedMessageBoxTests.cs): 既定値と結果の変換。[UiDialogCoordinatorWpfTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/UiDialogCoordinatorWpfTests.cs) の `WholeFolderConfirmation_RealMessageProducesDistinctNoCancelAndClose`: フォルダー全体の確認でNo・Cancel・閉じる・Escを区別すること。[LocalizationResourceParityTests](../../../tests/BeMusicSeeker.Core.Tests/Localization/LocalizationResourceParityTests.cs): 辞書・getter・6言語の整合。 |
| 設定画面内の選択・子画面と親子関係 | [SettingsWindow](../../../BeMusicSeeker/Views/Settings/SettingsWindow.xaml)、[UiDialogCoordinator](../../../BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs) | [SettingDialogEditCompletionTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogEditCompletionTests.cs)、[SettingDialogCategoryPersistenceTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogCategoryPersistenceTests.cs)、[SettingsWindowPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowPresentationTests.cs)、[SettingsControlPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsControlPresentationTests.cs): 取消、確認、親ウィンドウ、公開Automationによる選択。実際のフォーカスは[利用者に推奨する実機確認](../development/testing.md#利用者に推奨する実機確認)へ分ける。 |
| 設定の入力検証通知、表示失敗と入力保持 | [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) の `ShowUiMessage` | [SettingDialogEditCompletionTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogEditCompletionTests.cs) の `CustomFolderOutputValidation_UsesInjectedDialogAndPreservesRejectedValue`: 注入済み窓口への一度の通知、表示成功・失敗・親なしの結果、拒否値と保存済みXMLの不変。 |
| 一括編集の確認の寿命、任意通知の失敗、更新リンクの失敗 | [`PlaylistSummaryBulkEditDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistSummaryBulkEditDialog.cs)、[`UpdateAvailableDialog`](../../../BeMusicSeeker/Views/Dialogs/UpdateAvailableDialog.xaml.cs) | [`MainWindowPlaylistWorkspaceWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `MainWindowPlaylistDialogs_UseOwnedNativeModalLifetimeAndCleanup`: 確認中の `applyTask` と閉鎖抑止、確認失敗時の未適用と編集値保持、出力確定後の通知失敗と後片付け。[`DialogPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs) の `UpdateLinkLaunchFailureKeepsWindowAndDrainsCompiledEvent`: 起動失敗の通知Taskが終わっても成功として閉じないこと。 |
| 進捗ダイアログと表プロパティの取消ボタンの翻訳 | `ProgressDialog.xaml`、`PlaylistPropertyDialog.xaml` | [DialogPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs) の `CancelButtons_NewDialogsUseCurrentLanguageAndKeepCancellation`: 6言語で新しく開いた画面の文言が `Resources.Cancel` と一致し、進捗の取消が処理側へ届いて `CancelledByUser` で閉じること。 |
| 操作中に確定した通知の初回表示時の言語 | [PlaylistOperationNotificationOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistOperationNotificationOwner.cs)、[MainWindow](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [PlaylistOperationNotificationLanguageTests](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistOperationNotificationLanguageTests.cs): 各生成元の通知を言語Aで確定して言語Bで整形し、本文・見出し・重要度を確認する。[MainWindowPlaylistWorkspaceWpfTests](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `RecommendationReload_LanguageChangeBeforeFirstDisplayUsesDisplayLanguage`: 再読込みの途中で言語を変えると、終端で表示するリコメンドの通知が表示時の言語になり、読取りを再実行しないこと。同クラスの `PlaylistBackupFailure_ActualMainWindowShowsErrorWithHandIcon`: 重要度とアイコンの対応。[PlaylistRecommendedTableOwnerTests](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs) の `AppliedSkillNotification_UsesOrgNameNumbersAndSettingWithoutDate`: 数値のInvariant書式。 |

## 関連資料

[ファイル選択](file-dialogs.md)、[外観](appearance.md)、[設定](../runtime/settings.md)、[ライブラリ変更](../library/mutations.md)。
