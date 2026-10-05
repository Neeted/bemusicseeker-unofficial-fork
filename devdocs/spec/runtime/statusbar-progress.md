# ステータスバーに表示する処理

## 目的と適用範囲

ステータスバーにどの処理が進捗を出すか、どの単位を数えるかを概観する。表示行の共通規則と初期化の計算は[進捗仕様](progress.md)、実行・受付・保存・取消の契約は各機能仕様を正本とする。この一覧を実行順や操作可否の別の管理表にはしない。

## 用語

[共通用語集](../glossary.md)を参照する。「親行」は操作全体、「子行」はその操作で実行中の処理を示す。子行数と親の完了段階数は別の単位である。

## 仕様

### 表示対象の概観

| 処理 | 表示の所属 | 件数・ゲージの意味 | 正本 |
| --- | --- | --- | --- |
| 起動、全再初期化、ファイル差分・スコア・プレイリスト再読込み | 操作ごとの親行 | 開始時に予定した段階のうち、完了条件が成立した数。ファイル数や所要時間の割合ではない | [進捗](progress.md#操作ごとの予定)、[起動](startup.md) |
| 目録DBの読込み | 初期化・再読込みの子行 | 読込み中。確定した総数がない場合は不定 | [起動](startup.md#目録とファイルの読込み) |
| 譜面・音声・画像・動画・テキストの探索 | 初期化・再読込みの子行 | 探索中。結果件数を未確定の総数として使わない | [起動](startup.md#目録とファイルの読込み) |
| 譜面ファイルの差分判定・解析・反映 | 初期化・差分再読込みの子行 | 判定と解析を区別し、解析は今回のBMS/BMSON処理対象数を使う。DB更新行数とは別 | [ファイルとDBの整合性](../library/file-db-consistency.md)、[譜面ファイルの読込み](../library/chart-file-reading.md) |
| LR2カスタムフォルダファイルの変更確認・反映 | ファイル差分の子処理 | 管理外の確認対象ファイル数。譜面の差分解析件数や実DB変更数とは別 | [LR2楽曲DB同期](../integration/lr2-song-db.md)、[進捗](progress.md) |
| スコア・譜面情報・プレイリスト項目の読込み、ハッシュを含む譜面情報の補完 | 初期化・再読込みの子行 | 実行中の各処理を区別する。補完はその処理の対象譜面数、既知総数のない読込みは不定。ハッシュの独立段階が不要と確定した場合は別の子行を出さない | [起動](startup.md)、[譜面情報](../library/chart-info.md) |
| 順位の更新 | 親がその要求を必要とする場合は子行、それ以外は独立行 | 要求ごとの実行中を不定ゲージで示す。独立処理の終了を初期化・後続グループの終了条件へ追加しない | [起動](startup.md)、[進捗](progress.md#件数と通知) |
| 起動後の保守、出力修復、参照反映、索引・表示順の事前計算 | 起動後処理の親と実行中の子行 | 初期化の固定分母へ足さない。専用行が同じ仕事を表示している場合は重複させない | [起動](startup.md#起動の流れと完了境界)、[進捗](progress.md#表示と後続処理の区別) |
| パッケージ・フォルダの導入キュー | 独立行 | 完了入力パス数、総入力パス数、待機バッチ数を区別する。現在処理の詳細も同じ仕事に属する | [ドロップ入力からの導入](../library/drop-install.md#追加受付と画面の結果) |
| 保留パッケージの導入先推定 | 独立行 | 推定する作業数。キューの概要と実行中の推定詳細を二重表示しない | [導入先推定](../library/install-estimation.md#自動推定と並列処理) |
| プレイリストURLからの取得 | 独立行 | 完了URL数と総URL数。取得の取消をこの行から行う | [パッケージの取得](../playlist/downloads.md) |
| 外部プレイリストの同期 | 機能専用行。対応する起動後処理では子行 | 同期対象表数と現在の表名。同じ同期の専用行がある場合だけスケジューラー行を統合する | [プレイリストの保存と更新](../playlist/storage-and-export.md) |
| 選択プレイリストの再同期 | 独立した機能行 | 選択した対象表数と現在の表名 | [プレイリストの保存と更新](../playlist/storage-and-export.md) |
| 通常の表URL取込み | 独立した機能行 | 入力URL数と登録・参照反映などの後続段階を含む既存の複合進捗 | [プレイリストの保存と更新](../playlist/storage-and-export.md) |
| beatorajaの表URL取込み | 独立した機能行 | 設定の入力URL数と登録・参照・順序反映などの後続段階を含む既存の複合進捗 | [表URLの取込み](../playlist/table-url-import.md) |
| プレイリストのプロパティ更新・サマリー一括編集 | 処理ごとの独立した機能行 | 対象表数と現在の表名。別の同期・取込みの通知で進捗を上書きしない | [プレイリストの保存と更新](../playlist/storage-and-export.md) |
| LR2カスタムフォルダ出力の修復 | 機能専用行、または対応する起動後処理の子行 | 修復対象表数。探索・検証した物理ファイル数や出力件数とは別 | [LR2カスタムフォルダ](../playlist/lr2-custom-folders.md) |
| beatoraja BMTの出力 | 機能専用行、または対応する起動後処理の子行 | 出力対象表数。書込み・省略の実結果はログへ記録する | [BMT出力](../playlist/bmt-export.md) |
| 保守情報の再検査 | 独立行 | 処理対象譜面数。既存の取消をこの行から行う | [警告と保守情報](../library/warnings.md) |
| フォルダ名の自動変更 | 独立行 | 今回の変更対象フォルダ数 | [ライブラリ変更](../library/mutations.md) |
| LR2楽曲DBの同期準備・全体同期・再試行 | 専用行。起動から実行する処理は対応する後続グループの子行 | 準備は不定、同期は専用状態の段階・対象・保存結果を使い、初期化親の分母や成功へ混ぜない。未完了・失敗は再試行とともに残す | [LR2楽曲DB同期](../integration/lr2-song-db.md#同期状態と起動) |

### 一覧の維持

ステータスバーへ新しい処理を接続・統合・削除した変更では、この表と各機能仕様の対応も更新する。表示名の翻訳や内部ワーカーの増減だけで行を増やさない。個々の要求や過去の完了を恒久的な台帳へ保存しない。

モーダル進捗、画面内の局所的な読込み表示、再生位置の表示はこの一覧の対象外とし、ステータスバーへ自動的に移さない。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 操作・機能ごとの表示行と同時表示 | [`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`OperationProgressHubViewModelTests`](../../../BeMusicSeeker.Tests/MainWindow/OperationProgressHubViewModelTests.cs) |
| 初期化の親計算と子処理 | [`StartupProgressWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs) | [`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) |
| 左ゲージ、伸縮するラベル、複数行と行固有の操作 | [`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) | [`MainWindowProgressStatusBarWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowProgressStatusBarWpfTests.cs) |
| 表示対象と機能仕様の対応 | 本文の概観と各機能仕様 | 同じ変更で参照先・実際の生産経路を静的に確認する。文書の文言固定テストは作らない |

## 関連資料

[進捗](progress.md)、[起動](startup.md)、[ログ](../core/logging.md)、[画面の表示境界](../ui/dialogs.md)。
