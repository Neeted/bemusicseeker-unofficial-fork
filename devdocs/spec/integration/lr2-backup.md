# LR2バックアップ

## 目的と適用範囲

起動時に選択されたLR2の設定・楽曲DB・スコアDBを保存し、世代を管理する契約を定めます。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 対象の選択

LR2連携の起動処理は、設定フラグと元パスを `Backup.SaveSelectedBackupsWithResult` に渡し、バックグラウンド処理の完了を待ちます。対象は `Target.Config`、`Target.SongDB`、`Target.ScoreDB` のフラグだけで選びます。選択済みパスが存在しないことを理由に対象から外しません。`Target.None` は保存しません。

対象コレクションがnullまたは空の場合は保存しません。空白パスは無視しますが、選択された非空パスが一つでも欠落していれば保存全体を失敗とします。未選択対象の欠落は無関係です。コピー開始前後の消失やコピーエラーも失敗として返します。

起動側のDB存在確認・列挙は、保存成功後の最適化用です。バックアップ対象の選別に流用しません。戻り値の `Saved` が最適化の条件であり、起動側は警告と `FailureException` を通知します。

### 保存と公開

保存先ディレクトリは事前に存在している必要があります。世代名は文化圏に依存しない `yyyy-MM-dd` です。最新の日付世代から保存間隔を判定し、間隔は最低1日、同日保存済みなら上書きせず省略します。

長いパスに対応した既存のファイル操作を使い、すべての対象を最終世代と同じ親の一意な作業ディレクトリへコピーします。全コピー成功後だけ `MoveDirectory(overwrite: false)` で日付世代として公開します。最終パスにファイル・ディレクトリが存在する場合は、競合先を保持して失敗とします。

公開前の失敗では `Saved=false` と主例外を返し、既存世代を変更しません。今回作った作業ディレクトリの削除は一度だけ試み、存在確認または削除に失敗した場合はその正確なパスを警告へ追加し、元の後片付け例外と処理段階を診断します。存在確認には失敗を保持する属性取得を使い、`FileNotFoundException` と `DirectoryNotFoundException` だけを不存在として扱います。同名のファイルは削除対象へ広げません。その他の確認失敗も後片付けの診断へ渡し、後片付けや任意診断の例外で主原因を置き換えません。

### 世代の整理と保証範囲

公開成功後に、古い日付世代から削除します。保持数 `genNum` は新世代を含み、最低1に補正します。一つの旧世代の削除失敗は他の旧世代の削除を止めず、失敗した正確なパスを警告と元例外の診断へ渡します。削除失敗と任意診断の失敗では `Saved=true`、`FailureException=null` を維持します。そのため設定数を超える世代が残る場合があります。

残留した作業ディレクトリは世代に含めず、再利用や一括削除もしません。以前に作られた日付世代は、そのまま世代として扱います。内容が不完全な旧世代を検出・移行する機能ではありません。

保存成功はコピー呼出しの正常終了を示します。各DBが同一時点の内容であることやチェックサム一致までは保証しません。再試行、全体の巻き戻し、復旧用の管理ファイル、属性の自動補正を追加しません。互換API `SaveBackups` は主例外を再スローし、それ以外は `Saved` を返します。

### 公開後のDB最適化と任意診断

起動側は保存が公開された後だけ、既存の楽曲・スコアDBへ `VACUUM` と `REINDEX` を実行します。`RebuildDatabase` は存在する対象を順に処理し、失敗した対象の正確なDBパスと元例外を診断して従来の `false` を返します。列挙自体の失敗を、既に処理済みのDBの失敗として扱いません。既に公開したバックアップを撤回したり保存失敗へ変えたりしません。

起動側の最適化用スコアDB列挙と最適化呼出しの例外も、既知の対象パスを添えた局所診断に留めます。列挙できないことを理由に選択済みの保存対象を外しません。通常のログ取得・出力失敗はその場で抑止し、追加診断・再通知・再試行の連鎖を作りません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| DB整理の実VACUUM・REINDEX、schema・値と次保存の保全 | [`Backup.RebuildDatabase`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests.RebuildDatabase_PreservesRealSongAndScoreSchemaValuesAndAllowsNextSave`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) は小さい実song・score DBの表・索引・値と対象外ファイルを保持し、整理後の次書込み・新readonly読戻しを確認する。DBの物理bytes・容量は固定しない。 |
| 選択済み対象の欠落・未選択対象・対象なし | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) |
| 長いパス・コピー失敗・最終パス衝突・既存内容の保持 | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) |
| 世代数・同日省略・作業領域の残留、削除失敗後の他世代回収 | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests.SaveBackupsWithResult_PruneFailureWarnsAfterSuccessfulPublication`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) は最古世代の削除失敗後も他の旧世代を回収し、新世代バイト列・警告の正確なパス・保存成功を保持する。コピー失敗時の主原因、部分作業領域の回収と次の明示保存、欠落・公開先衝突の保証は同クラスの既存ケースへ分担する。 |
| 起動経路の実保存と最適化失敗の独立性 | [`StartupLibraryConstructionOwner.PrepareBackupAsync`](../../../BeMusicSeeker/ViewModels/Startup/StartupLibraryConstructionOwner.cs) | [`StartupLibraryFailureContractTests.PrepareBackupAsync_OptimizationFailurePreservesPublishedScoreBytesWithoutSaveFailureNotification`](../../../BeMusicSeeker.Tests/Startup/StartupLibraryFailureContractTests.cs) は正常な楽曲DBと、コピー可能だがSQLite形式ではない `score.db`を選択し、公開済みスコアファイルの内容と保存失敗通知なしを確認する。 |
| 後片付けの存在確認、元例外・正確なDBパスの診断、NLogの故障の分離 | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs) の `TryDeleteBackupDirectory` / `RebuildDatabase`、[`StartupLibraryConstructionOwner.PrepareBackupAsync`](../../../BeMusicSeeker/ViewModels/Startup/StartupLibraryConstructionOwner.cs) | 存在確認と削除の捕捉範囲、処理中DBパスの設定、ログ取得・出力の失敗後も本体結果を保持する制御を静的に確認する。診断先やスコアDB列挙の故障は自動テストでは未確認。 |

## 関連資料

三表のSQLバックアップ・復元、値とSQLite型の往復は[プレイリスト保存](../playlist/storage-and-export.md#json出力sqlバックアップと復元)を参照します。

[起動](../runtime/startup.md)、[設定](../runtime/settings.md)、[未完了の安全性改善](../../plan/safety-improvements-plan.md)を参照します。
