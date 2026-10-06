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

### 初期化・探索・同期の記録

処理ごとに、開始条件と終端結果を対応付けます。既存の操作トークン、世代、要求版を持つ経路ではその識別を利用し、ログ専用の永続識別や再実行状態を追加しません。画面の表示行と処理の対象を対応させますが、行の描画や通知の排出を処理の終端条件にしません。

| 記録する事実 | 意味 |
| --- | --- |
| 処理と対象 | DB読込み、探索、差分判定、解析、反映、補完など、何を扱う処理か |
| 入力条件 | 検索ルート、拡張子、除外の種類と件数、走査方式。省略や代替走査の理由も区別する |
| 処理量 | 探索ヒット数、除外後の確認対象数、確認済み数、解析対象数、読込み行数。各対象と単位を区別する |
| 実結果 | 変更不要、生成、更新、削除、失敗、互換性による省略など。候補数や生成計画数を実DB変更数として使わない |
| 終端 | 成功、不要、入力不完全、失敗、取消を区別し、その理由を記録する |
| 時間 | 記録対象の開始から終端までの実時間、待機と処理内訳。未計測・適用対象外を実測0へ補完しない |

通常INFOの探索条件は、譜面・リソース・LR2フォルダ・補助テキスト等で同じ粒度にします。検索ルートと拡張子、除外条件の出所・種類と件数を記録し、数百ディレクトリなどの巨大な除外パス一覧と除外込みクエリ全文は省きます。条件の記録は実際の要求から行い、ログ用の再検索・DB照会・別の対象集合の構築はしません。

LR2のアプリ管理出力ディレクトリの探索除外と、組込みカスタムフォルダ設定による候補除外は別の理由として記録します。差分なしでも確認対象は存在するため、確認候補・変更不要・解析対象・実反映結果を分けます。親ディレクトリの生成・更新行数とファイル数も混ぜません。

主要な件数と結果を読める要約を置き、調査に必要な検索・読取り・解析・保存・キャッシュ・待機の詳細内訳はその処理へ接続します。対象ごとの高頻度同期ログや、同じ結果を段階ごとに巨大な文字列へ複製する記録は増やしません。

起動全体では操作可能、必須初期化完了、後続処理の収束を別の境界として維持します。入れ子や並行処理の累積時間を足して操作の実時間にせず、詳細区間と全体の範囲を揃えて比較します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| ログ構成、文字コード、回転、出力先の失敗 | [`NLogWrapper`](../../../BeMusicSeeker/Ribbit/Logging/NLogWrapper.cs) | [`NLogWrapperTests`](../../../BeMusicSeeker.Tests/Runtime/NLogWrapperTests.cs) |
| 探索条件、確認対象・解析対象・保存結果の区別 | [`RootFileEnumerationService`](../../../BeMusicSeeker/Models/Utils/Scanning/RootFileEnumeration.cs)、[`BmsLibraryInitializationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs)、[`Lr2SynchronizationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs) | [`LibraryFileScanPipelineOwnerTests`](../../../BeMusicSeeker.Tests/Scanning/LibraryFileScanPipelineOwnerTests.cs)、[`BmsLibraryInitializationFileScanTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationFileScanTests.cs)、[`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) で処理対象と実結果を確認し、条件ログが実要求から出ることを静的に確認する。ログ全文の固定検査は設けない。 |
| Taskの成功値、元の例外、取消、診断失敗 | [`TaskEx`](../../../BeMusicSeeker/Ribbit/Util/Extensions/TaskEx.cs) | [`TaskLoggingTests`](../../../BeMusicSeeker.Tests/Runtime/TaskLoggingTests.cs) |
| プレイリスト通知の順序、一次失敗、通知失敗時も再表示しない | [`PlaylistOperationNotificationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistOperationNotificationOwner.cs) | [`PlaylistOperationNotificationOwnerTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistOperationNotificationOwnerTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 画面イベントからの例外通知 | [MainWindow.cs](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の `NotifyMainWindowOperationFailureAsync` と `playlistTableDrop` | 共通サービスへの引渡しと、既存の通知境界を実装で確認する。ダイアログの受付・失敗は[ダイアログ仕様](../ui/dialogs.md)の対応表を参照する。 |

## 関連資料

[性能](performance-and-scale.md)、[ダイアログ](../ui/dialogs.md)、[プレイリスト保存](../playlist/storage-and-export.md)。
