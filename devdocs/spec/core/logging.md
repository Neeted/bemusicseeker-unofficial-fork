# ログ出力

## 目的と適用範囲

利用者報告、起動・導入等の性能、外部連携の失敗を調べるためのログ仕様です。診断の失敗で、設定保存・DB更新・ファイル操作の成功、元の例外、取消の意味を変えません。

## 用語

[共通用語集](../glossary.md)の診断・計測点・終端を使います。`LoggingAndPropagate` は結果を保持するTask拡張、`ObserveFault` は結果を利用しないTaskの例外を観測する拡張です。

## 仕様

### 出力の管理主体

`Ribbit.Logging.NLogWrapper` がNLogの出力先、規則、ローテーションを管理します。機能側は `FileLogger` または `GetLogger(name)` を使い、ログファイルへ直接追記したりNLogの構成を変更したりしません。

### Taskの結果

結果を返す処理は `LoggingAndPropagate` を使い、元のTask完了後に診断し、成功値・元の例外・取消をそのまま呼出元へ伝えます。診断失敗で結果を置換しません。結果を利用しない処理の `ObserveFault` は待機可能な結果を返さず、失敗時だけ例外を観測します。取消をエラー記録せず、診断コールバックの失敗も未観測例外にしません。ログの継続処理が完了しただけで本体成功と見なす経路は設けません。

### 画面操作の失敗

画面イベントは元のTaskを待ち、成功後の表示・選択復元は成功時だけ行います。共通の失敗通知を使うイベントでは、未加工のTaskを待ち、元の例外を `NotifyMainWindowOperationFailureAsync` へ渡します。この処理が `ObserveFault` で診断するため、同じ例外へ `LoggingAndPropagate` を重ねません。既に同拡張を使う処理と、合成Task固有の診断はその責務を維持します。

`OperationCanceledException` は通知せず、イベントを終了します。他の例外はログと既存の優先エラー表示へ渡し、イベントの外へ漏らしません。表示は `IUiDialogService` を待ち、`Msg_error_unexpected` と `Error` を使います。表示不可、`UiDialogResult.Failed`、表示例外は診断だけとし、別画面への切替えや再試行はしません。部分成功後も必要な選択整理等は `finally` で行います。

プレイリスト表へのドロップでは、作業領域の通知セッションが返す結果記録を唯一の通知境界とします。既存の警告・エラーを優先し、一次失敗が未報告の場合だけ一般エラーを一件追加します。情報通知だけでは失敗報告済みとはしません。`playlistTableDrop` は一次結果を保って診断し、同じ失敗を別ダイアログで重複表示しません。通知先の失敗も診断に留めます。

### ファイルとレベル

実行ファイルと同じ場所の `log/` に、BOMなしUTF-8で出力します。ローテーション済みファイルも同じ文字コードです。既存ログの古い部分の再変換は行わず、過去の文字コードと追記部分の混在は許容します。

| ファイル | 内容 |
| --- | --- |
| `log/application.log` | 通常診断。警告・エラーと有用な情報を含む。 |
| `log/install-performance.log` | 起動、DB、走査、推定、同期などの性能・段階診断。 |

`InstallPerformance*` の出力は性能ログへ分離し、通常ログへ重複出力しません。既定は `INFO` です。`--log-level=Info` は既定と同じ互換引数、`Warn`・`Warning`・`Error` は明示的な抑制指定として扱います。不正な値は互換規則により `Warn` とし警告します。INFOを抑制する設定では、INFOの性能ログも出力しません。

### ローテーションと性能

NLogの `FileTarget` で、一ファイル20 MiB超過時にローテーションし、種類ごとに最大5世代を `log/archive/` に保持します。独自の移動・削除処理は持ちません。

計測範囲、対象件数、欠測は[性能仕様](performance-and-scale.md)に従います。集約した件数と時間を使い、リソースごとの同期ログや巨大集合の文字列化は追加しません。すべての推奨診断が既存の経路に実装済みという意味ではありません。

通常起動でINFOが出るため、配布物に `LaunchWithInfoLog.bat` は含めません。既に配布済みの同ファイルが呼ぶ `--log-level=Info` は引き続き受け付けます。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| ログ構成、文字コード、回転、出力先の失敗 | [`NLogWrapper`](../../../Ribbit/Logging/NLogWrapper.cs) | [`NLogWrapperTests`](../../../BeMusicSeeker.Tests/Runtime/NLogWrapperTests.cs) |
| Taskの成功値、元の例外、取消、診断失敗 | [`TaskEx`](../../../Ribbit/Util/Extensions/TaskEx.cs) | [`TaskLoggingTests`](../../../BeMusicSeeker.Tests/Runtime/TaskLoggingTests.cs) |
| プレイリスト通知の順序、一次失敗、通知失敗時も再表示しない | [`PlaylistOperationNotificationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistOperationNotificationOwner.cs) | [`PlaylistOperationNotificationOwnerTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistOperationNotificationOwnerTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 画面イベントからの例外通知 | [MainWindow.cs](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の `NotifyMainWindowOperationFailureAsync` と `playlistTableDrop` | 共通サービスへの引渡しと、既存の通知境界を実装で確認する。ダイアログの受付・失敗は[ダイアログ仕様](../ui/dialogs.md)の対応表を参照する。 |

## 関連資料

[性能](performance-and-scale.md)、[ダイアログ](../ui/dialogs.md)、[プレイリスト保存](../playlist/storage-and-export.md)。
