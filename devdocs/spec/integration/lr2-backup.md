# LR2バックアップ

## 目的と適用範囲

起動時に選択されたLR2の設定・楽曲DB・スコアDBを保存し、世代を管理する契約を定めます。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 対象の選択

LR2連携の起動処理は、設定フラグと元パスを `Backup.SaveSelectedBackupsWithResult` に渡し、バックグラウンド処理の完了を待ちます。対象は `Target.Config`、`Target.SongDB`、`Target.ScoreDB` のフラグだけで選びます。選択済みパスが存在しないことを理由に対象から外しません。`Target.None` は保存しません。

対象コレクションがnullまたは空の場合は保存しません。空白パスは無視しますが、選択された非空パスが一つでも欠落していれば保存全体を失敗とします。未選択対象の欠落は無関係です。コピー開始前後の消失やコピーエラーも失敗として返します。

起動側の最適化用のDB存在確認・スコアDB列挙は保存成功後だけに行います。対象なし、同日保存済み、間隔未到達、公開前失敗の場合は最適化準備へ進みません。バックアップ対象の選別に流用しません。戻り値の `Saved` が最適化の条件であり、起動側は警告と `FailureException` を通知します。保存失敗の元例外は、保存先と選択対象を添えて既存の局所診断へ一度渡します。

### 保存と公開

保存先ディレクトリは事前に存在している必要があります。世代名は文化圏に依存しない `yyyy-MM-dd` です。ローカルの今日を一回捕捉し、世代選別・間隔・公開名・保持と削除に共用します。日付名を `ParseExact` で解析した後、今日以前に絞った同じ世代一覧で保存間隔と保持枠を判定します。間隔は最低1日、同日保存済みなら上書きせず省略します。形式だけが一致する無効な日付名の解析失敗は従来どおり保存失敗とします。

未来日付の世代は内容と名前を保持し、今日がその日付に追いつくまで間隔・保持枠・削除の対象外とします。未来世代だけなら今日を保存し、過去と混在する場合は今日以前の最新世代から間隔を判定します。自動改名・削除・警告・最終取得日時の保存は追加しません。

長いパスに対応した既存のファイル操作を使い、すべての対象を最終世代と同じ親の一意な作業ディレクトリへコピーします。全コピー成功後だけ `MoveDirectory(overwrite: false)` で日付世代として公開します。最終パスにファイル・ディレクトリが存在する場合は、競合先を保持して失敗とします。

コピーで各ディレクトリへ入る際、その宛先の作成・内容列挙より前に属性を取得し、ディレクトリ再解析ポイント（ジャンクションなど）を拒否します。`Score` 自身と子孫を含み、リンク先の内外を解決せず、該当パスとリソース化した拒否理由で保存全体を失敗とします。属性取得失敗もコピー失敗です。部分のスキップや事前の全木検査は行いません。共通 `CopyDirectory` の省略可能な拒否オプションをバックアップだけが有効にし、他用途の既定動作を維持します。

公開前の失敗では `Saved=false` と主例外を返し、既存世代を変更しません。今回作った作業ディレクトリの削除は一度だけ試み、存在確認または削除に失敗した場合はその正確なパスを警告へ追加し、元の後片付け例外と処理段階を診断します。存在確認には失敗を保持する属性取得を使い、`FileNotFoundException` と `DirectoryNotFoundException` だけを不存在として扱います。同名のファイルは削除対象へ広げません。その他の確認失敗も後片付けの診断へ渡し、後片付けや任意診断の例外で主原因を置き換えません。

### 世代の整理と保証範囲

公開成功後に、古い日付世代から削除します。保持数 `genNum` は新世代を含み、最低1に補正します。一つの旧世代の削除失敗は他の旧世代の削除を止めず、失敗した正確なパスを警告と元例外の診断へ渡します。削除失敗と任意診断の失敗では `Saved=true`、`FailureException=null` を維持します。削除失敗や未来世代の保持により、保存先の総フォルダ数が設定数を超える場合があります。

残留した作業ディレクトリは世代に含めず、再利用や一括削除もしません。以前に作られた今日以前の日付世代は、作成元と内容に関係なく世代整理の対象とします。画面の保存先説明と日英マニュアルでは、専用フォルダの指定とこの削除範囲を案内します。内容が不完全な旧世代を検出・移行する機能ではありません。

保存成功はコピー呼出しの正常終了を示します。各DBが同一時点の内容であることやチェックサム一致までは保証しません。再試行、全体の巻き戻し、復旧用の管理ファイル、属性の自動補正を追加しません。通常ファイルのsymlink・hardlink、`Score` より上位の祖先、保存先・既存世代のリンク、処理中の外部差替えへの完全防御は対象外です。互換API `SaveBackups` は主例外を再スローし、それ以外は `Saved` を返します。

### 公開後のDB最適化と任意診断

起動側は保存が公開された後だけ、選択済みの楽曲・スコアDBの最適化準備を行います。スコアDBは `*.db` の再帰列挙を全件実体化してから、最初のDBの最適化を始めます。準備・列挙失敗を空一覧へ置き換えて続けません。

`RebuildDatabase` は存在する対象を順に処理し、各DBで `VACUUM` → `REINDEX` の順に実行します。最初の失敗で停止し、正確なDBパスと元例外を診断して従来の `false` を返します。列挙自体の失敗を、既に処理済みのDBの失敗として扱いません。

最適化準備・列挙・実行の例外または `false` は、保存済み・最適化未完了を伝える専用の警告を一回返します。公開済みバックアップと保存成功を保持し、保存失敗通知へ変換しません。世代削除警告も保持します。再試行・巻き戻しは行わず、保存結果型の責務は広げません。

例外の元原因と既知の対象パスは既存 `NLogWrapper` へ局所診断します。`false` を受けただけの重複診断は行いません。通常のログ取得・出力失敗はその場で抑止し、保存結果・通知を変更せず、追加診断・再通知・再試行の連鎖を作りません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| DB整理の実VACUUM・REINDEX、schema・値と次保存の保全 | [`Backup.RebuildDatabase`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests.RebuildDatabase_PreservesRealSongAndScoreSchemaValuesAndAllowsNextSave`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) は小さい実song・score DBの表・索引・値と対象外ファイルを保持し、整理後の次書込み・新readonly読戻しを確認する。DBの物理bytes・容量は固定しない。 |
| 選択済み対象の欠落・未選択対象・対象なし | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) |
| 長いパス・コピー失敗・最終パス衝突・既存内容の保持 | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) |
| 世代数・同日省略・作業領域の残留、削除失敗後の他世代回収 | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests.SaveBackupsWithResult_PruneFailureWarnsAfterSuccessfulPublication`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) は最古世代の削除失敗後も他の旧世代を回収し、新世代バイト列・警告の正確なパス・保存成功を保持する。コピー失敗時の主原因、部分作業領域の回収と次の明示保存、欠落・公開先衝突の保証は同クラスの既存ケースへ分担する。 |
| 起動経路の実保存と最適化失敗の独立性 | [`StartupLibraryConstructionOwner.PrepareBackupAsync`](../../../BeMusicSeeker/ViewModels/Startup/StartupLibraryConstructionOwner.cs) | [`StartupLibraryFailureContractTests.PrepareBackupAsync_OptimizationFailurePreservesPublishedScoreBytesWithoutSaveFailureNotification`](../../../BeMusicSeeker.Tests/Startup/StartupLibraryFailureContractTests.cs) は正常な楽曲DBと、入れ子のコピー可能だがSQLite形式ではない `score.db` を選択し、実保存・再帰列挙・最適化失敗を通して公開bytes・元Score保持、専用警告一回と保存失敗通知なし、削除警告との共存を確認する。`PrepareBackupAsync_SuccessAndSameDaySkipDoNotWarn` は小さい実DBの正常保存と、同日保存済みの不正SQLiteの省略・内容保持・警告なしを確認する。 |
| 未来のみ・過去との混在、同日・間隔省略、無効日付 | [`Backup.SaveBackupsWithResult`](../../../BeMusicSeeker/Models/LR2/Backup.cs) | [`BackupTests`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) の未来世代テストは保持数0→1・1・複数で今日の保存、残る・削除する過去、未来内容保持と総数超過を確認する。同日・間隔ケースは未来を混在させ、無効日付は解析失敗と旧世代不変を確認する。 |
| Score自身・深い子孫の内向き／外向きディレクトリリンク、未選択と省略 | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs)、[`LongPathFileSystem.CopyDirectory`](../../../BeMusicSeeker/Models/Utils/FileOperations/LongPathFileSystem.cs) | [`BackupTests`](../../../BeMusicSeeker.Tests/Settings/BackupTests.cs) はPowerShell 7で実ジャンクションを作成し、属性と参照先を確認してから本番処理へ渡す。理由・パス、全体非公開、旧世代・リンク・先sentinel保持、stage回収を確認する。同日・間隔未到達とConfigのみ保存を別条件で確認する。[`LongPathFileSystemTests`](../../../BeMusicSeeker.Tests/FileOperations/LongPathFileSystemTests.cs) は既定コピーが非循環ジャンクションを辿る互換性を確認する。外部プロセスの期限・失敗・出力回収は共通 `PowerShellTestProcess`、リンク自体の除去と専用領域の回収は各ケースが所有する。シンボリックリンク固有の差分は未自動確認。 |
| 専用保存先の説明と表示リソース | [`BackupSettingsPage`](../../../BeMusicSeeker/Views/Settings/Pages/BackupSettingsPage.xaml)、[`表示リソース`](../../../BeMusicSeeker/Properties/Resources.resx) | [`SettingsWindowPresentationTests.SettingsWindow_OperationAndPlayerDescriptionsAppearOnTheirOwningPages`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs) は実Backupページの保存先controlへの説明Bindingを確認する。辞書キー・書式・日本語全文一致は [`LocalizationResourceParityTests`](../../../BeMusicSeeker.Tests/Localization/LocalizationResourceParityTests.cs) に分担し、日英マニュアルの意味対応とリンク検証有効のHTML生成は文書検証で確認する。 |
| 後片付けの存在確認、保存失敗の主原因と対象・正確なDBパスの診断、NLogの故障の分離 | [`Backup`](../../../BeMusicSeeker/Models/LR2/Backup.cs) の `TryDeleteBackupDirectory` / `RebuildDatabase`、[`StartupLibraryConstructionOwner.PrepareBackupAsync`](../../../BeMusicSeeker/ViewModels/Startup/StartupLibraryConstructionOwner.cs) | 失敗結果から診断への受渡し、存在確認と削除の捕捉範囲、処理中DBパスの設定、ログ取得・出力の失敗後も本体結果を保持する制御を静的に確認する。最適化準備・途中列挙故障、全件実体化の先行、Saved判定後だけの準備、例外とfalseの警告合流、各ディレクトリの内容列挙前の属性確認、ログ故障の分離は静的確認へ分担し、自動故障注入は行わない。診断先やスコアDB列挙の故障は自動テストでは未確認。 |

## 関連資料

三表のSQLバックアップ・復元、値とSQLite型の往復は[プレイリスト保存](../playlist/storage-and-export.md#json出力sqlバックアップと復元)を参照します。

[起動](../runtime/startup.md)、[設定](../runtime/settings.md)を参照します。
