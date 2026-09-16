# LR2の楽曲DB生成と同期

## 目的と適用範囲

LR2連携モードで、現在のBMS譜面・検索ルート・プレイリスト出力から `song.db` の `song` と `folder` を生成する契約を定めます。LR2自身の起動時自動更新には依存しません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 管理範囲と設定

`song` と `folder` の生成列は再生成可能なデータです。ただし `song` の行の追加・削除はファイル差分検出が担当し、全体同期はその所属を変更しません。利用者が編集する `favorite`、`tag`、`adddate` は明示的な保持対象です。

BMSONはLR2へ出力せず、本アプリの `bmson_song` 等で管理します。BMSONにLR2互換性警告を出しません。スタンドアロンモードでは、LR2用のフォルダ行、`.lr2folder` 探索、`folderinfo.txt`、組込みカスタムフォルダの階層を生成しません。

LR2連携を初めて選択するとき、設定保存時、起動時の設定読込みで、`config.xml` の `<autoreload>` を `0`（手動更新のみ）にします。BMS検索ルートは本アプリの共通画面から管理します。追加操作そのものでは再帰走査せず、通常の差分更新に含めます。入力は存在するディレクトリかつShift_JISで表せるパスとし、相対パスや同一・親子関係の重複を正規化・検証します。

### 同期への入力

現在のBMSパス、検索ルート、必要な祖先ディレクトリと更新時刻、`folderinfo.txt`、探索済みの管理外 `.lr2folder`、アプリ出力、LR2組込みフォルダ、設定値、解析結果・互換性警告・リソース調査結果を受け取ります。不完全な入力を、同期内の広範な再探索で補って成功扱いにしません。

LR2に見せるjukeboxルートと、本アプリが譜面・リソースを走査するルートは用途が異なります。通常出力先、追加出力先、ルート出力先の扱いは[設定](../runtime/settings.md#カスタムフォルダと検索対象)を参照します。

### songの生成

ファイル差分検出は、信頼できる走査範囲の現在パスをDBの完全一致キーで照合します。行の収束、大小文字だけ異なるパス、一対一・同一MD5での保存値引継ぎは[パスの同一性](../core/path-identity.md)に従います。全体同期は存在する行の生成列だけを更新し、不要行の削除、管理用補助行やダイジェストの整理を担当しません。

`date` は譜面ファイルの更新時刻をUnix秒で保存します。新しい行の `adddate` が未設定なら、同期時点のLR2形式時刻で初期化します。`level`、`judge`、`date`、`folder`、`parent` 等は現在の入力から生成します。日時の0や負値も有効なUnix秒として扱います。

`folder` と `parent` はLR2互換のCRC32です。対象ディレクトリまたはその親のパスに末尾の `\` とNULを付け、Shift_JISのバイト列から計算します。パスをShift_JISで表せない、またはWindowsパスとして解釈できない場合も管理対象のBMS行は削除せず、両列を `NULL` にして互換性警告を付けます。計算は `LR2CRC32` と `Lr2SongFolderParentNormalizer` を正本とします。

新規追加と `UpsertSongs` では、この正規化を保存前に行います。相対パスの補正は、絶対パス化とCRC計算の両方が成功した場合だけ `song.path` に反映します。計算できないパスだけを中途半端に更新したり、警告対象を次の走査で削除・再追加したりしません。

### folderの全体生成

`folder.path` が行のキーです。通常ディレクトリは `type=1` とし、親のCRC32を `parent` に設定します。LR2ルート直下として扱う行は `ROOT` とNULのCRC32を親にします。自身のCRC32を保存する専用列はありません。`date` はディレクトリまたは `.lr2folder` の更新時刻であり、ルート行も実際の時刻と一致させます。

期待する集合は検索ルート、BMSのあるディレクトリとルートまでの祖先、`folderinfo.txt` のある場所、テキストグループ、`.lr2folder` に必要な親階層から作ります。同一パスの優先度は、組込みカスタムフォルダ、`.lr2folder`、`folderinfo.txt` のあるディレクトリ、通常ディレクトリの順です。同順位で同内容なら順序に依存せず一つにまとめ、異なる内容なら衝突です。

全入力の探索・取得・解析と必要な親行の構成が成功してから、一つのトランザクションでフォルダ表全体を置き換えます。不完全な入力や同順位の衝突では表を変更せず、`Incomplete` または `Failed` を返します。通常のプレイリスト・設定・譜面変更は、明示された局所範囲だけを同期します。

### カスタムフォルダの階層

通常の `.lr2folder` は `type=2` です。特殊な種別は組込みフォルダだけに付け、`newsong.lr2folder` は3、`course1.lr2folder` から `course3.lr2folder` は6です。それ以外は本文に `#COMMAND` や `#TAG` があっても2です。

管理外ファイルは実際の探索ルート配下の階層を維持します。ルート自身だけを `ROOT` の直下とし、子ディレクトリやファイルを平坦化しません。アプリの通常出力先は出力基点をルートとして扱い、ルート出力先では各プレイリストのディレクトリをルートとして扱います。連番ファイルはそのプレイリストディレクトリを親とし、ルート出力基点の直下に置く単独ファイルだけを `ROOT` の直下にします。

保存時は `0000.lr2folder` からの連番をShift_JISで出力し、同じ生成内容からDB行を同期します。ルート出力の設定変更はLR2設定の検索ルートにも反映します。全体同期では更新時刻が一致しても本文を再解析します。全体同期の事前準備中に行うファイル作成・検証と、フォルダ表の確定を分けます。

組込みフォルダの場所はLR2実行ファイル配下の `LR2files/CustomFolder` です。`<customfolder>` のビットマスクが `RANDOM`、`favorite.lr2folder`、`TOP10.lr2folder`、`PLAYLEVEL`、`CLEAR`、`RANK`、`ignore.lr2folder`、`INSANE01`、`INSANE02` の有効化を制御します。コースは常に扱い、新曲フォルダは `titleflash` と `song.adddate` から生成対象を決めます。DBにはLR2ルートからの相対パスで保存します。

### 同期状態と起動

同期状態は `lr2_song_db_sync_status` の `name=default` 行に保持します。状態、入力署名、実行識別子、処理位置、総数、段階、エラー、更新・完了時刻を記録します。状態には `NotNeeded`、`Needed`、`Running`、`Completed`、`Failed`、`Cancelled`、`Incomplete` があります。

`Completed` は、完全な生成入力、フォルダ表の確定、楽曲の生成列更新、入力の現行性確認、状態の保存が成功したことを示します。同じ入力署名で完了済みなら全体同期を省略します。これは `chart_info` の存在・完全性・現行性の証明ではありません。譜面情報の補完は[譜面情報の管理](../library/chart-info.md)に従います。

通常起動では `startup_initialization_complete` の正常記録後に一度だけ自動同期を登録します。初回完了ダイアログがある場合も登録を先に行い、閉じるまで同期を待たせません。同じ起動世代の重複登録を防ぎ、起動失敗・中断では登録しません。起動進捗とは別の専用状態として表示し、起動の分母や成功結果へ混ぜません。

未完了状態、失敗後の再試行、手動の強制同期は先頭から実行します。保存した処理位置から再開しません。利用者向けの同期取消操作は設けず、アプリ終了の内部取消だけを区別します。安全に記録できる場合は `Incomplete` と `shutdown_interrupted` を残します。旧DBの `Cancelled` は読込み・表示・再試行判定に対応しますが、新しい通常実行では作りません。終了以外の取消は失敗として表面化します。

同期中も読み取り操作を許可し、DB変更を伴う操作は制限します。主な順序は通常フォルダ、`.lr2folder`、フォルダ表の検証・確定、楽曲の生成列、入力の現行性確認、完了です。観測用の進捗は[進捗管理](../runtime/progress.md)に従い、通知先の例外でDB結果を変えません。

### 読込みと確定結果の再利用

楽曲行の処理開始時に、現行パーサー版の譜面情報と、再試行期限を考慮した解析失敗MD5集合を取得します。各ワーカーは既存の `ChartFileSnapshot` とこれらの値を共通評価器へ渡します。ワーカー内で追加の譜面読込みやDB照会を行いません。不足・古い譜面情報も、専用パーサーを新設せず共通の補完処理を使います。

直前のファイル差分処理が全体として確定したBMSパスは、一度だけ使えるメモリー上の結果として全体同期へ渡せます。`BmsRowsVersion` の一致を必要とし、`OwnedChartCollectionVersion` には依存しません。対象パスの譜面読込みとDB現行性照会を省けますが、消費・失敗・再試行・手動実行・設定起因実行・終了・破棄を越えて再利用や永続化はしません。

確定パスも現行の譜面処理対象数・進捗の分母に含め、読取り・解析を省いた結果として1,000件単位（末尾は残件数）で回収します。全件が確定パスでも専用の一括スキップ経路は設けません。

複数の読み手は、順序回収までの先行数を制限する枠を取得してから対象を採番します。採番済みで未回収の項目は必ず枠を確保済みとし、先頭の不足番号が後続結果で埋まった枠を待つ循環を防ぎます。受渡し前の末尾到達・終了取消・中断では読み手が取得済みの枠だけを返し、受渡し後は書き手が番号順に回収した時点で返します。枠は一度だけ返し、過剰返却の例外を無視しません。

走査結果の有効性は `ScanSurfaceGeneration`、BMS/BMSONの保存行の版、BMS検索ルート、LR2フォルダ探索ルートで確認します。操作全体の入力現行性の確認では所持譜面集合の版も使い、別用途の版を同一視しません。

### 局所変更と性能

導入・削除・移動・リネーム・統合では、確定結果から追加・削除・旧新BMSパスを捕捉します。追加だけなら現在BMSの再検索は不要です。削除・移動では変更ディレクトリ内と、登録ルートまでの祖先のBMS件数だけを既存索引から取得します。BMSONを残存BMSに数えず、削除範囲をルート全体へ広げません。

値は所持集合のロックと対応する版の下で固定し、確定結果に可変な検索関数を持たせません。自動リネームも共通の変更セッション内で局所値を取得し、LR2同期を一回終えます。取得・同期の失敗は失敗として返し、既に確定したファイル・DB処理を再実行・巻き戻ししません。

ディレクトリ比較とDBの完全一致キーを区別します。通常の局所変更で全BMSの列挙・整列・全祖先索引の再構築を行いません。全体同期では一回の完全なフォルダ生成と確定を行い、楽曲行の所属は変更しません。探索が不完全なら広範囲の不要行削除を行いません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 生成入力と走査なしの候補構成 | [`Lr2SongDbSyncInputBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncInputBuilder.cs) | [`Lr2SongDbSyncInputBuilderTests`](../../../BeMusicSeeker.Tests/Lr2SongDbSyncInputBuilderTests.cs) |
| 完全なフォルダ集合・衝突・一括確定 | [`Lr2FolderTableReconciliationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2FolderTableReconciliationService.cs) | [`Lr2FolderTableReconciliationServiceTests`](../../../BeMusicSeeker.Tests/Lr2FolderTableReconciliationServiceTests.cs)、[`Lr2FolderRowGeneratorTests`](../../../BeMusicSeeker.Tests/Lr2FolderRowGeneratorTests.cs) |
| 同期状態と生成列の永続化 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncService.cs)、[`Lr2SongDbWriter`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbWriter.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/BmsLibraryLr2SongDbSyncTests.cs)、[`Lr2SongDbSyncServiceTests`](../../../BeMusicSeeker.Tests/Lr2SongDbSyncServiceTests.cs)、[`Lr2SongDbWriterTests`](../../../BeMusicSeeker.Tests/Lr2SongDbWriterTests.cs) |
| 確定パスの一回限りの利用 | [`Lr2SongDbSyncCommittedPathReceipt`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncCommittedPathReceipt.cs) | [`Lr2SongDbSyncCommittedPathReceiptTests`](../../../BeMusicSeeker.Tests/Lr2SongDbSyncCommittedPathReceiptTests.cs) |
| 確定パスのチャンク処理・分母・保存結果の維持 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncService.cs) の `UpsertSongRows` | [`Lr2SongDbSyncCommittedPathReceiptTests`](../../../BeMusicSeeker.Tests/Lr2SongDbSyncCommittedPathReceiptTests.cs) の `ReceiptEligibleSongRows_CompleteAcrossChunkAndOrderingWindowBoundaries`: 全件が証票対象の0・1・1,000・1,001・10,001件で、読取りなしの完了、確定処理位置、保存行と利用者列の保持を確認する。混在入力は既存の `ReceiptEligibleSongRowsSkipReaderAndCurrentnessRead`。 |
| 採番と順序枠の所有権 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncService.cs) の `UpsertSongRows` | 採番より前の枠取得、全退出経路の返却条件、番号順回収時の返却を静的に確認する。境界件数のテストでは本番のCPU別並列度を使い、特定のスレッド切替順を強制しない。 |
| 局所BMS範囲・祖先・完全一致キー | [`Lr2NormalFolderSyncScopeBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2NormalFolderSyncScopeBuilder.cs)、[`Lr2NormalFolderDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2NormalFolderDbSyncService.cs) | [`Lr2NormalFolderSyncScopeBuilderTests`](../../../BeMusicSeeker.Tests/Lr2NormalFolderSyncScopeBuilderTests.cs)、[`Lr2NormalFolderDbSyncServiceTests`](../../../BeMusicSeeker.Tests/Lr2NormalFolderDbSyncServiceTests.cs)、[`BmsLibraryFolderRenameRefreshTests`](../../../BeMusicSeeker.Tests/BmsLibraryFolderRenameRefreshTests.cs) |

## 関連資料

[一括再構成の設計判断](../../decisions/lr2-song-db-one-shot-reconciliation.md)、[索引](../core/data-and-indexes.md)、[プレイリスト出力](../playlist/storage-and-export.md)を参照します。
