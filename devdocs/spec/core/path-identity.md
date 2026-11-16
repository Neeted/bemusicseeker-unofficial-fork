# パスによる識別

## 目的と適用範囲

DB行のパスの識別、走査結果への収束、移転に伴う保存値の引継ぎを定めます。Windowsで同じファイルに解決される文字列でも、DB上の別のキーを暗黙に同一視しません。実ファイルの長いパスやI/Oは[長いパスとファイル入出力](path-length-and-io.md)を参照します。

## 用語

[共通用語集](../glossary.md)の完全一致、正規化、識別条件、入力の完全性を使います。

| 用語 | 意味 |
| --- | --- |
| 完全一致キー | 行自身が保持するパスの文字列を、大文字小文字を区別して照合するキー。正規化前の旧いDBの文字列も、そのまま含む。 |
| 収束 | 信頼できる現在の入力と、対象範囲の保存行の集合を一致させること。 |
| 保存値の引継ぎ | 異なるパスの間で、利用者が管理する列を移す処理。コードでは `relink` と呼ぶ場合がある。 |

## 仕様

### 行の識別と物理パス

DB行の識別に使うパス（`song.path`、`folder.path`、`maintenance.path`、`bmson_song.path`、`install.path` など）は、大文字小文字を区別する文字列の完全一致で扱います。LR2の `path TEXT primary key` は `COLLATE NOCASE` ではないため、同じ物理ファイルへ解決される複数の表記が、別の行として存在し得ます。

| 処理 | 使うキー |
| --- | --- |
| 旧い行の照合 | 行自身のキー。照合の前に小文字化、`Trim`、`GetFullPath` などで別の文字列へ変換しない。 |
| 削除 | 旧い行自身のキー。 |
| 追加・更新 | 現在の走査、または確定した操作結果のキー。 |
| 保存用のパスの生成 | 入力時の生成・正規化の規則（[長いパスとファイル入出力](path-length-and-io.md#保存形式と実ファイルの操作)）。既存の行の識別とは別の処理。 |

ディレクトリの探索、拡張子、表示順などには、大文字小文字を区別しない比較を使えます。ただし、その探索範囲を、DB更新を許可する集合へそのまま流用しません。`File.Exists` が同じ実体を示すことだけで、旧い行を現在の行と判断したり、削除の対象を広げたりしません。

`install` の正常な復元では、初期のDBスナップショットの全キーをそのまま保持して、別名の単位を評価します。全対象の復元が完成した後にだけ、完全一致の削除と正規形の登録を一括で確定します。不完全な復元を表示だけで採用することはしないため、後続の変更で全表の正規形の別名を再解決する補償は行いません。詳細は[起動](../runtime/startup.md#導入とプレイリストの準備完了)に従います。

### 走査結果への収束

信頼できる完全な入力があり、読取り・登録・反映に成功した対象範囲について、既存の集合を `D`、現在の走査の集合を `S` とします。完全一致で、`D ∩ S` を維持・更新し、`D − S` を削除し、`S − D` を追加します。大文字小文字だけの違いも同じ規則です。

| 既存のDB | 現在の走査 | 正常な反映の後 |
| --- | --- | --- |
| `{A, B}` | `{B}` | `{B}`。Bの保存値をAから上書きしない。 |
| `{A}` | `{B}` | `{B}`。保存値の引継ぎは[別に判定する](#同一内容の移転に伴う保存値の引継ぎ)。 |
| `{A, B}` | `{A, B}` | `{A, B}`。現在の別のキーとして両方を保持する。 |
| `{B}` | `{B}` | `{B}`。再適用で不要な追加・削除を繰り返さない。 |

次の場合は、この規則を理由に全DBを置き換えません。対象範囲と失敗時の保証は[譜面ファイルの読取り](../library/chart-file-reading.md)と[ファイルとDBの整合性](../library/file-db-consistency.md)に従います。

- 不完全な走査、利用できないルート。
- 空の走査に対する保護。
- 個別のファイルの読取り・解析の失敗。
- DBの失敗。

`ReloadFileDiff` は、現在のメモリ上の集合と走査結果を比較し、外部で編集されたDBを読み直しません。外部の編集も取り込む場合は `FullReinitialize` を使います。任意の再読込みが、常に全DBを収束させるわけではありません。

### 完全一致を維持する経路

走査結果（`ChartScanResult.ChartFilePaths`、`ChartFileEntriesByPath`）、ルートの列挙結果、管理側の走査の譜面集約、所持集合、参照索引は、同じ完全一致のパスを維持します。MD5の一致は、パスの識別の代わりになりません。

局所的な削除・移転・導入・統合では、上流で確認したすべての旧いキーを、DB、保存行、所持集合、パッケージ参照へ渡します。

- 途中で大文字小文字を畳んで行を落としたり、未確認の別のキーを増やしたりしません。
- 同じ完全一致のパスへの追加・更新だけが、既存の行を置き換えます。大文字小文字や `.` の有無が異なる表記の行は残します。
- 移転先として存続する保存主体の新しいキーは、そのキーへの整理から保護します。別のキーまでは保護せず、削除する保存主体の移動先は保護の集合に含めません。
- 保守行だけが明示されたパスも、`song` の有無にかかわらず整理します。

集合を扱うSQLと、ハッシュの最後の所有者の扱いは[データと索引](data-and-indexes.md#パス整理とハッシュの所有関係)を参照します。

統合の物理処理では、確認済みの複数のDBキーが同じ物理ファイルを指す場合に、物理処理の結果を共有できます。ただしDBキーはすべて保持し、代表の移転と、残る旧いキーの整理を、同じカタログ要求へ渡します。物理処理の失敗を、別名のパス経由で再試行しません。この共有は、同じMD5からの保存値の引継ぎや、大文字小文字だけの物理的な名前変更の機能を加えるものではありません。

フォルダの包含関係と物理削除にはファイルシステムの比較規則を、導入先の表示を解除する行にはDBの完全一致キーを使います。この二つを、一つの比較器へ統合しません。

### `COLLATE NOCASE` の制限

`COLLATE NOCASE` は、拡張子、表示順、MD5・SHA-256などパス以外の識別子、Windowsでの探索範囲の最適化には使えます。次の用途には使いません。

- パスを主キーにする一時表や、既存の行と現在の行の照合。
- 更新・削除・存在しない旧い行の整理を決める `WHERE` / `NOT EXISTS`。
- 別のキーを区別せず、現在の表記へ単純な `UPDATE` でまとめる処理。

明示的な移転の旧新の事実からキーを変更する処理は、上記の単純な同一視とは区別します。

### カタログ依存のファイル操作の受付

現在のカタログのパスを根拠に、ライブラリの実ファイルや所属を変更する操作は、カタログの準備完了を確認できた場合だけ受理します。準備完了とは、現在のカタログ世代について、完全な走査から、パスの差分、保存行の反映、必須の公開の引渡しまでが成功した状態です。

| 契機 | 準備完了の状態 |
| --- | --- |
| 起動、`FullReinitialize`、`ReloadFileDiff` がカタログまたは差分の処理を開始する | 未確認へ戻す。 |
| その処理が正常に収束する | 確認済みにする。 |
| 走査の省略、不完全な入力、空の走査に対する保護、反映の失敗 | 未確認のまま。 |
| スコアだけの初期化 | 変えない。 |

受付は、既存の排他的な変更権を取得した後、同じ権利を保持したまま、準備完了をO(1)で再確認します。未確認なら直ちに権利を解放し、ファイル・DB・所持集合を変更せずに拒否します。操作ごとの全カタログの走査は追加しません。

次はこの追加条件の対象外です。既存の共通の排他は維持します。

- 収束を作る走査自身。
- プレイリストの読込みとDB編集、LR2カスタムフォルダ。
- 保留パッケージだけの追加・削除・導入先の解除・元の整理。

自動導入は、準備完了が未確認でも探索と保留への登録を行い、実ライブラリへの導入だけを抑えて、候補を保留に残します。

個別の譜面の、継続可能な読取り・軽量解析の失敗は、ディレクトリの列挙全体の不完全さとは区別します。列挙と反映が完全に終われば、読めない譜面があっても準備完了にできます。詳細な譜面情報の解析失敗も、基本のカタログ行の登録を止める条件にはしません。

### LR2同期との分担

| 処理 | 変更する範囲 |
| --- | --- |
| ファイル差分・対象範囲の差分処理 | `song` の行を追加・削除する。 |
| LR2の全体の再整合 | 既存の `song` の生成列を更新する。所属は変更しない。 |
| `folder` の全体の再整合 | `folder` の行を、別の完全な入力から作った期待する集合へ揃える。 |

「LR2同期」という総称だけで、全行の収束を保証しません。詳細は[LR2楽曲DB](../integration/lr2-song-db.md)を参照します。

### folderの追加時刻

`folder.path` の旧い集合と必要な集合は、元の文字列の `Ordinal` の完全一致で比較します。追加時刻 `adddate` は次のように扱います。

| 場合 | `adddate` |
| --- | --- |
| 通常のフォルダ、全表の再整合、カスタムフォルダとその親の生成で、完全一致で存続する行 | NULLも含めて保持する。 |
| 走査・生成で新しいキーを作る | 通常の生成時刻 `GeneratedAtUtc` のUnix秒で初期化する。別のキーの値を検索・継承しない。 |
| 成功が明示的に確認されたフォルダと、管理外のカスタムフォルダの改名・移動 | 確認した旧→新の物理移転の事実に従って、元の値をNULLも含めて引き継ぐ。 |

改名・移動での引継ぎは、未知の別のキーを推測で結び付ける走査・生成とは区別します。完全一致する別の宛先の所有・衝突の処理と、`song` の利用者列の扱いは、各操作の契約を維持します。

次の表は、走査・生成による必要な集合の比較を表します。

| 旧いfolderの集合 | 必要な集合 | 追加時刻と退役 |
| --- | --- | --- |
| `{a}` | `{A}` | `a` を削除し、`A` は通常の新規の値。 |
| `{a, A}` | `{A}` | `a` を削除し、完全一致する `A` の値を保持。 |
| `{A}` | `{A}` | 生成列を更新し、完全一致する `A` の値を保持。 |
| `{a}` | `{B}` | `a` を削除し、`B` は通常の新規の値。 |

走査・生成では、ASCII・非ASCIIの大文字小文字の差と、通常の別のパスへの変更に、同じ規則を使います。mtimeが同値であることを、追加・削除を省く理由にはしません。必要な集合を生成した後に、同値で存続する行の保存だけを省略します。物理的なルートの包含やメタデータの位置の照合に使う `OrdinalIgnoreCase` と、保存キーの識別を混同しません。全登録の完全な走査と局所的な変更の範囲は[LR2楽曲DB](../integration/lr2-song-db.md#folderの全体生成)に従います。

管理出力の基点の表記を訂正するときは、確認した旧・新の各表ディレクトリを、対象を限定したDB読取りへ渡します。

- 外部同期は、今回の登録入力のうち、現在のルートと同じ場所を指す旧い表記を、既存の行の追加の読取りにだけ使います。
- この検索範囲の集合も `Ordinal` で保持します。SQLiteのASCIIだけを畳む前方一致検索へ渡す前に、非ASCIIの旧い表記を落としません。
- 生成・削除・管理除外の物理範囲は変えず、未知の第三の表記を探す全件取得は加えません。

### 同一内容の移転に伴う保存値の引継ぎ

通常のBMSファイル差分では、同じMD5の削除候補が一件、新規追加候補が一件の場合だけ、旧い行の `favorite`、`tag`、`adddate` を新しい行へ引き継ぎます。大文字小文字だけの差分も除外しません。これは内容のハッシュに基づく引継ぎであり、同じ物理ファイルや明示的な移動の証明ではありません。

| 候補 | 条件 |
| --- | --- |
| 削除候補 | その差分で古いと確定した旧い行。 |
| 新規候補 | 完全一致する既存の行がなく、読取りと基本解析に成功したBMS。 |

候補数は差分全体で数えます。大文字小文字を畳む、列挙順で代表を選ぶ、旧い保存値を読めなかった行を候補数から落とす、といった方法で、一対一に見せません。旧・新のどちらかが複数なら引き継ぎません。既存の移転先Bがある `{A, B} → {B}` では、同じMD5でも、Bの保存値をAで上書きしません。旧いキーの保存値を捕捉できた一対一の組だけを、DB確定後に新しい完全一致キーへ復元します。

引継ぎの範囲と保証は次のとおりです。

- 引き継ぐのは `Lr2SongUserColumns` の三列だけです。新しいパス・時刻・フォルダCRC・生成メタデータ・メンテナンス情報は現在の入力から作り、旧い行全体を移植しません。
- BMSONへは、このBMS専用の引継ぎを拡張しません。別のパスのメンテナンス情報も自動では移植しません。
- 復元の失敗は伝播します。複数の分割をまたぐ原子性、再試行、再起動後の保存値の回復は保証しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 完全一致の追加・削除と、移転先の保護 | [`BmsLibraryDbGateway`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs)、[`CatalogMutationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMutationOwner.cs)、[`PackageLifecycleOwner.PackageStateMutationApplier`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryStateApplier.cs) | [`CatalogMutationOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Catalog/CatalogMutationOwnerTests.cs)、[`BmsLibraryStateApplierTests`](../../../tests/BeMusicSeeker.Windows.Tests/Install/BmsLibraryStateApplierTests.cs) |
| 所持集合、参照解決、同じ物理ファイルの処理結果の共有 | [`OwnedChartCollectionState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/OwnedChartCollectionState.cs)、[`LibraryMutationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.cs) | [`OwnedChartCollectionProjectionTests`](../../../tests/BeMusicSeeker.Core.Tests/Catalog/OwnedChartCollectionProjectionTests.cs)、[`OwnedChartCollectionReferenceIndexTests`](../../../tests/BeMusicSeeker.Core.Tests/Catalog/OwnedChartCollectionReferenceIndexTests.cs)、[`OwnedChartCollectionLibraryMutationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Catalog/OwnedChartCollectionLibraryMutationTests.cs)、[`BmsLibraryDuplicateServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) / [`BmsLibraryDuplicateServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) |
| 実際の導入の確定パスと、準備完了が未確認の間の保留 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`BmsLibraryPackageInstallServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) / [`BmsLibraryPackageInstallServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) |
| 走査の収束、再適用、個別の解析失敗 | [`LibraryFileScanPipelineOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/LibraryFileScanPipelineOwner.cs)、[`FileScanParseCommitOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/FileScanParseCommitOwner.cs) | [`BmsLibraryInitializationFileScanTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) / [`BmsLibraryInitializationFileScanTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Startup/BmsLibraryInitializationFileScanTests.cs)、[`BmsLibraryInitializationInlineChartInfoTests`](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationInlineChartInfoTests.cs)、[`Lr2SongDbWriterTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbWriterTests.cs) |
| folderの元の文字列の完全一致と追加時刻 | [`Lr2FolderRowGenerator`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderRowGenerator.cs)、[`Lr2FolderGenerationScopePlanner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderGenerationScopePlanner.cs) | [`Lr2NormalFolderDbSyncServiceTests.Sync_UsesRawExactFolderSet`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2NormalFolderDbSyncServiceTests.cs) が、実際の限定取得からDB保存までで、ASCII・CP932のギリシャ文字・通常の移動、NULLの保持、対象外の保全、再適用を確認する。`FullReinitialize` と `ReloadFileDiff` の取得面と逆順は `BmsLibraryInitializationLr2NormalFolderTests.ApplyFileScanDiff_ReusesOperationFolderSnapshot` が分担する。 |
| 管理出力の旧・新の前方一致による、対象を限定した取得 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)、[`Lr2FolderFileDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderFileDbSyncService.cs) | `Lr2FolderFileDbSyncServiceTests.Sync_ReplacesCaseDriftedExistingPath` が、ASCII・CP932のギリシャ文字の旧い行の退役と新規の追加時刻を確認する。`SettingDialogLr2OwnerTests.NormalOutputScan_PreservesManagedHierarchyAndConvergesConfirmedPaths` が、ギリシャ文字の稼働中の訂正と、ASCIIの次回起動での初検出から、保存表記と子のCRCが同じ操作の中で収束することを確認する。 |
| 一対一の保存値の引継ぎ、既存の移転先、曖昧な候補 | [`FileScanParseCommitOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/FileScanParseCommitOwner.cs) | [`BmsLibraryInitializationFileScanTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) / [`BmsLibraryInitializationFileScanTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) の `ApplyFileScanDiff_MovedBmsWithSameMd5PreservesUserSongColumns`、`ApplyFileScanDiff_MovedBmsWithExistingDestinationPreservesDestinationUserColumns`、旧・新の候補が複数の各テストが、通常の表記と大文字小文字だけの差分で、DB・メモリ・メンテナンス情報と二回目の走査の結果を確認する。 |
| 導入先の一時状態と、表示行の完全一致 | [`InstallDestinationStateOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/InstallDestinationStateOwner.cs) | [`InstallDestinationStateOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Install/InstallDestinationStateOwnerTests.cs)、[`ChartListVirtualViewTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartList/ChartListVirtualViewTests.cs)、[`ChartListVirtualViewTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/ChartListVirtualViewTests.cs) |

## 関連資料

[データと索引](data-and-indexes.md)、[長いパスとファイル入出力](path-length-and-io.md)、[ライブラリ変更](../library/mutations.md)。
