# パスによる識別

## 目的と適用範囲

DB行の識別、走査結果への収束、移転に伴う保存値の引継ぎを定めます。Windowsで同じファイルに解決される文字列でも、DB上の別のキーを暗黙に同一視しません。実ファイルの長いパスやI/Oは[長いパスとファイル入出力](path-length-and-io.md)を参照します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 完全一致キー | 正規化前の旧DB文字列も含め、行自身が保持するキーを大文字小文字を区別して照合するもの。 |
| 収束 | 信頼できる現在入力と対象範囲の保存行集合を一致させること。 |
| 保存値の引継ぎ | 異なるパス間で利用者管理列を移す処理。コードでは `relink` と呼ぶ場合があります。 |

## 仕様

### 行の識別と物理パス

`song.path`、`folder.path`、`maintenance.path`、`bmson_song.path`、`install.path` など、DB行の識別に使うパスは**大文字小文字を区別する文字列の完全一致**で扱います。LR2の `path TEXT primary key` は `COLLATE NOCASE` ではなく、同じ物理ファイルへ解決される複数表記が別行として存在し得ます。

旧DBキーを照合する前に、小文字化、`Trim`、`GetFullPath` などで別の文字列へ変換しません。入力時に保存用パスを生成・正規化する処理と、既存行のキーを識別する処理は別です。削除は旧行自身のキー、追加・更新は現在の走査または確定した操作結果のキーを使います。

ディレクトリ探索、拡張子、表示順などには大文字小文字を区別しない比較を使えます。ただし、その探索範囲をDB更新の認可集合へそのまま流用しません。`File.Exists` が同じ実体を示すことだけで、旧行を現在の行と判断したり削除対象を広げたりしません。

### 走査結果への収束

信頼できる完全な入力があり、読取り・登録・反映に成功した対象範囲について、既存集合を `D`、現在の走査集合を `S` とすると、完全一致で `D ∩ S` を維持・更新し、`D − S` を削除、`S − D` を追加します。大文字小文字だけの違いも同じ規則です。

| 既存DB | 現在の走査 | 正常反映後 |
| --- | --- | --- |
| `{A, B}` | `{B}` | `{B}`。Bの保存値をAから上書きしません。 |
| `{A}` | `{B}` | `{B}`。保存値の引継ぎは別途判定します。 |
| `{A, B}` | `{A, B}` | `{A, B}`。現在の別キーとして両方を保持します。 |
| `{B}` | `{B}` | `{B}`。再適用で不要な追加・削除を繰り返しません。 |

不完全走査、利用できないルート、空走査の保護、個別ファイルの読取り・解析失敗、DB失敗を無視して全DBを置き換える規則ではありません。対象範囲と失敗時の保証は[ファイル読取り](../library/chart-file-reading.md)と[ファイル・DB整合](../library/file-db-consistency.md)に従います。

`ReloadFileDiff` は現在のメモリ上の集合と走査結果を比較し、外部編集されたDBを読み直しません。外部編集も取り込む場合は `FullReinitialize` を使います。任意の再読込みが常に全DBを収束させる保証はありません。

### 完全一致を維持する経路

`ChartScanResult.ChartFilePaths`、`ChartFileEntriesByPath`、ルート列挙結果、管理側走査の譜面集約、所持集合・参照索引は、同じ完全一致のパスを維持します。MD5の一致はパスの識別の代わりにはなりません。

局所の削除・移転・導入・統合では、上流で確認したすべての旧キーをDB、保存行、所持集合、パッケージ参照へ渡します。途中で大文字小文字を畳んで行を落としたり、未確認の別キーを増やしたりしません。同じ完全一致パスへの追加・更新だけが既存行を置き換え、別の大文字小文字・`.` を含む表記の行は残します。

移転先として存続する保存主体の新キーは、そのキーへの整理から保護します。別キーまで保護せず、削除する保存主体の移動先は保護集合に含めません。明示された保守行だけのパスも `song` の有無にかかわらず整理します。集合SQL・ハッシュの最後の所有者の扱いは[データと索引](data-and-indexes.md#パス整理とハッシュの所有関係)を参照します。

統合の物理処理では、確認済みの複数DBキーが同じ物理ファイルを指す場合に処理結果を共有できます。ただしDBキーはすべて保持し、代表の移転と残る旧キーの整理を同じカタログ要求へ渡します。物理処理失敗を別名パス経由で再試行しません。これは同一MD5からの保存値引継ぎや、新たな大文字小文字だけの物理名前変更機能ではありません。

フォルダの包含関係・物理削除はファイルシステムの比較規則、導入先表示を解除する行はDBの完全一致キーを使います。この二つを一つの比較器へ統合しません。

### `COLLATE NOCASE` の制限

拡張子、表示順、MD5・SHA-256などパス以外の識別子、Windowsでの探索範囲の最適化には使えます。一方、次の用途には使いません。

- パスを主キーにする一時表や、既存行と現在行の照合。
- 更新・削除・存在しない旧行の整理を決める `WHERE` / `NOT EXISTS`。
- 別のキーを区別せず、現在の表記へ単純な `UPDATE` でまとめる処理。

明示的な移転の旧新事実からキーを変更する場合は、上記の単純な同一視とは区別します。

### カタログ依存のファイル操作の受付

現在のカタログのパスを根拠にライブラリの実ファイルや所属を変更する操作は、現在のカタログ世代について、完全な走査からパス差分・保存行の反映・必須公開の引渡しまで成功した後だけ受理します。

起動、`FullReinitialize`、`ReloadFileDiff` がカタログまたは差分処理を始めると準備完了を未確認へ戻します。正常に収束した処理だけが再び受付を開きます。スコアだけの初期化はこの状態を変えません。走査省略、不完全入力、空走査による保護、反映失敗では開きません。

受付は既存の排他的な変更権を取得した後、同じ権利を保持してO(1)で再確認します。未確認なら直ちに解放し、ファイル・DB・所持集合を変更せず拒否します。操作ごとの全カタログ走査は追加しません。

収束を作る走査自身、プレイリストの読込み・DB編集・LR2カスタムフォルダ、保留パッケージだけの追加・削除・導入先解除・元の整理は、この追加条件の対象外です。既存の共通排他は維持します。自動導入では探索と保留への登録を許可し、実ライブラリへの導入だけを抑えて候補を保留に残します。

個別譜面の継続可能な読取り・軽量解析失敗は、ディレクトリ列挙全体の不完全とは区別します。列挙と反映が完全に終われば、読めない譜面があっても受付を開けます。詳細な譜面情報の解析失敗も、基本カタログ行の登録を止める条件にはしません。

### LR2同期との分担

`song` の追加・削除はファイル差分または対象範囲の差分処理が担当します。LR2の全体再整合は既存 `song` の生成列を更新しますが、所属を変更しません。`folder` の全体再整合は別の完全な入力から期待集合を作ります。「LR2同期」という総称だけで全行の収束を保証しません。詳細は[LR2楽曲DB](../integration/lr2-song-db.md)を参照します。

### 同一内容の移転に伴う保存値の引継ぎ

通常のBMSファイル差分では、**同一MD5の削除候補が一件、新規追加候補が一件の場合だけ**、旧行の `favorite`、`tag`、`adddate` を新行へ引き継ぎます。大文字小文字だけの差分も除外しません。これは内容ハッシュに基づく引継ぎであって、同じ物理ファイルや明示的な移動の証明ではありません。

削除候補はその差分で古いと確定した旧行、新規候補は完全一致の既存行がなく、読取り・基本解析に成功したBMSです。候補数は差分全体で数えます。大文字小文字を畳む、列挙順で代表を選ぶ、旧保存値を読めなかった行を候補数から落とす、といった方法で一対一に見せません。

旧・新のどちらかが複数なら引き継ぎません。既存の移転先Bがある `{A, B} → {B}` では、同MD5でもBの保存値をAで上書きしません。旧キーの保存値を捕捉できた一対一の組だけを、DB確定後に新しい完全一致キーへ復元します。

引継ぎは `Lr2SongUserColumns` の三列に限定します。新しいパス・時刻・フォルダCRC・生成メタデータ・保守情報は現在の入力から作り、旧行全体を移植しません。BMSONへこのBMS専用の引継ぎを拡張せず、別パスの保守情報も自動移植しません。復元失敗は伝播し、複数分割をまたぐ原子性、再試行、再起動後の保存値回復を追加保証しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 完全一致の追加・削除・移転先保護 | [`BmsLibraryDbGateway`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs)、[`CatalogMutationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMutationOwner.cs)、[`BmsLibraryStateApplier`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryStateApplier.cs) | [`CatalogMutationOwnerTests`](../../../BeMusicSeeker.Tests/Catalog/CatalogMutationOwnerTests.cs)、[`BmsLibraryStateApplierTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryStateApplierTests.cs) |
| 所持集合・参照解決・同一物理ファイルの結果共有 | [`OwnedChartCollectionState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/OwnedChartCollectionState.cs)、[`LibraryMutationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.cs) | [`OwnedChartCollectionProjectionTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionProjectionTests.cs)、[`OwnedChartCollectionReferenceIndexTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionReferenceIndexTests.cs)、[`OwnedChartCollectionLibraryMutationTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionLibraryMutationTests.cs)、[`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) |
| 実導入の確定パスと未収束時の保留 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) |
| 走査の収束・再適用・個別解析失敗 | [`LibraryFileScanPipelineOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/LibraryFileScanPipelineOwner.cs)、[`FileScanParseCommitOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/FileScanParseCommitOwner.cs) | [`BmsLibraryInitializationFileScanTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationFileScanTests.cs)、[`BmsLibraryInitializationInlineChartInfoTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationInlineChartInfoTests.cs)、[`Lr2SongDbWriterTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbWriterTests.cs) |
| 一対一の保存値引継ぎ・既存移転先・曖昧な候補 | [`FileScanParseCommitOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/FileScanParseCommitOwner.cs) | [`BmsLibraryInitializationFileScanTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) の `ApplyFileScanDiff_MovedBmsWithSameMd5PreservesUserSongColumns`、`ApplyFileScanDiff_MovedBmsWithExistingDestinationPreservesDestinationUserColumns`、旧・新候補が複数の各テスト。通常表記と大文字小文字だけの差分、DB・メモリ・保守情報、二回目の走査を確認します。 |
| 導入先の一時状態と表示行の完全一致 | [`InstallDestinationStateOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/InstallDestinationStateOwner.cs) | [`InstallDestinationStateOwnerTests`](../../../BeMusicSeeker.Tests/Install/InstallDestinationStateOwnerTests.cs)、[`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs) |

## 関連資料

[データと索引](data-and-indexes.md)、[長いパス](path-length-and-io.md)、[変更セッション](../library/mutations.md)。
