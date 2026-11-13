# 長いパスとファイル入出力

## 目的と適用範囲

譜面とリソースの読取り、内部のファイル変更、走査の完全性、長いパスと外部アプリとの互換性を定めます。DB行の文字列としての同一性は別の責務であり、[パスによる識別](path-identity.md)を参照します。

## 用語

[共通用語集](../glossary.md)の入力の完全性を使います。

| 用語 | 意味 |
| --- | --- |
| 拡張長形式 | Win32の長いパス用の `\\?\` 付きの表記。保存用の通常のパスとは分け、実際のI/Oの直前に変換する。 |
| 完全な走査 | 対象範囲を、成功かつ打切りなしで列挙できた結果。個別の譜面の詳細解析の成功とは別。 |
| 正規のI/O窓口 | 長いパスへの変換、再試行、診断を一か所で担う、下表の入口。 |

## 仕様

### 保存形式と実ファイルの操作

新しく生成する保存用のパスは通常の絶対パスとし、ドライブ・UNCの `\\?\` 接頭辞をDBやモデルへ保存しません。通常のパスに安全に戻せない `\\?\Volume{...}` や `\\?\GLOBALROOT` は、接頭辞を外しただけの別の場所へ変換しません。既存のDBキーの照合は、元の完全一致の文字列を維持します。

実ファイルのI/Oでは、正規のI/O窓口が必要に応じて拡張長形式へ変換します。内部で読める長いパスの譜面は通常どおり登録します。内部で読めることと、LR2などの外部アプリで使えることは、別に判定します。

LR2の従来のパス長を超えるBMSは、登録したうえで、LR2互換の警告を付けます。OpenLR2の挙動までは、同じ警告で断定しません。ルートの登録では、単体モードでも、CP932で `root + separator + "a.bms"` を符号化でき、その長さが `Lr2CompatibilityEvaluator.MaxLegacyPathBytes` 以下であることを確認します。

### 正規のI/O窓口

| 用途 | 入口 |
| --- | --- |
| 譜面の内容・ハッシュ・ファイル時刻 | `ChartFileContentReader.ReadBuffer` / `ReadSnapshot` / `CreateSnapshot`、`LongPathFileSystem.ReadAllBytes` / `OpenRead` / `GetLastWriteTimeUtc` |
| 存在確認・列挙 | `LongPathFileSystem.FileExists` / `DirectoryExists` / `EntryExists`、各 `Enumerate*` |
| 内部の直接のコピー・公開 | `LongPathFileSystem.CopyFile` / `CopyDirectory` / `PublishFile`、`AtomicFileWriter.Write` |
| 利用者の操作に伴う変更 | `IFileMutationService` のディレクトリ作成、ファイル・ディレクトリの移動、直接削除、時刻設定 |

パスを指定するBMS・BMSON・譜面情報の解析APIも、この窓口で読みます。

`IFileMutationService` は、読取り専用属性の補正、既存の短時間の再試行、診断、`FileMutationException` への集約を担当します。各利用側に、独自の再試行を重ねません。

`AtomicFileWriter` は、書込みを完了した内容だけを保存先へ公開します。LR2の `config.xml` とプレイリストのSQLバックアップも、この境界を使います。

- 公開前に保存先を削除しません。
- 自分が所有しない一時ファイルを、まとめて消しません。
- 失敗を成功へ変えません。

### 長いパスへの対応範囲

長いパスへの対応は、初期化の読取りだけに限定しません。次の処理は正規のI/O窓口を使います。変更操作は、さらに[ライブラリ変更](../library/mutations.md)の受付・通知・ロックの境界に従います。

- ルートの走査、譜面・音声リソースの読取り、譜面のテキスト、`folderinfo.txt`、`.lr2folder`。
- 導入・移動・削除・拡張子の変更・統合・上書き。
- beatoraja出力と管理ファイルの整理、LR2カスタムフォルダとバックアップ。

IRキャッシュ、設定、取込み・出力の補助、外部連携の一部、同梱メタデータ、テストデータ、ログ、配布スクリプトなどは、各機能が個別のI/O契約を持ちます。`System.IO` を直接使っていることだけで譜面管理の欠陥と断定せず、その対象パスと変更範囲で評価します。

### 完全な走査だけを反映する

ライブラリへ反映する走査は、`Success == true`、`IsComplete == true`、`ScanLimitExceeded == false` のすべてを満たす場合だけ、反映の根拠に使えます。Everythingを利用できず管理側で走査する場合も、条件は同じです。

次のような場合は走査範囲が不完全です。その走査から、追加・更新・削除・LR2同期・リソース索引の置換をしません。既存のDB、スコア、保留パッケージの読込みは継続できます。部分的な列挙でカタログを中途半端に更新しないための保護です。

- ルートの不在。
- 子ディレクトリの列挙の失敗、メタデータの取得不能。
- 上限の超過。
- その他、対象を完全に列挙できなかった場合。

走査の差分を反映する入口（`LibraryFileScanPipelineOwner.ApplyFileScanDiff()` と `BmsLibraryInitializationService.ApplyFileScanDiff()`）は、どちらも不完全な入力を拒否します。空の正常な走査へ読み替えて、削除へ進みません。

ネイティブ連携の契約や版の不一致は、配置の異常として失敗させます。単なる不完全な走査の警告にはしません。

#### Everythingの結果の完全な取得

Everything SDKから取得するパス・ディレクトリ・名前は、完全に取得できたことを確認してから分割・集約します。サロゲートペアの直前での切詰めも区別し、取得し直しても必要な容量と一致しなければ、その問い合わせを失敗にします。失敗した項目を飛ばして部分集合を成功にせず、既存の管理側の代替走査と完全性の判定へ伝えます。

性能のため、通常の短いパスは各項目を一回の取得で済ませ、拡張したバッファーは問い合わせの中で再利用します。新しいパス長の上限や保存キーは設けません。

#### Everythingの索引の範囲

Everythingの問い合わせが成功した結果は、登録ルートの完全な走査として採用します。Everythingの索引に含まれない範囲を検出する確認は行わず、その範囲の譜面は保証の対象外とします。採用理由と採用しない代替案は[Everythingの問い合わせと索引の範囲](../../decisions/everything-query-boundary.md#索引の範囲を完全な走査とみなす)に置きます。

- 対象外になる範囲の例は、Everythingの設定で索引していないドライブ・フォルダ、ネットワークドライブ、Everythingが辿らないジャンクションなどのリンク先です。どの範囲が索引されるかは、Everythingの設定と製品の挙動で決まります。
- Everythingによる走査の事前確認は、登録ルートの存在確認だけです。索引されない範囲の譜面は走査結果に含まれず、既存の登録は通常の削除の差分として反映されます。譜面行に保存していた値と、LR2の `song.db` の行も削除され、戻しません。
- 走査全体が0件の場合の保護（[差分0件と空の走査](../runtime/startup.md#差分0件と空の走査)）は維持しますが、一部のルートや範囲だけが欠けた結果は区別しません。
- ライブラリの走査が管理側の走査へ切り替わるのは、Everythingを利用できない場合と問い合わせが失敗した場合だけです。ネイティブ連携の契約や版の不一致は、上記のとおり切り替えずに失敗させます。管理側の走査はリンク先も辿るため、取得元が切り替わると列挙される集合が変わる場合があります。
- LR2の `folder` 行のためのディレクトリとファイルの探索（`Lr2FolderDirectoryEnumerationService`、`Lr2FolderFileDiscoveryService`）は、Everythingの結果が全体で0件のときだけ管理側の列挙で確かめ直します。これらを含め、Everythingを使う他の列挙も、一部の範囲だけが欠けた結果は検出しません。

利用者には、索引に含まれない場所をBMSディレクトリへ登録する場合に、その場所を索引へ加えるか、Everythingを終了して管理側の走査を使うよう[マニュアル](../../../docs/manual.ja.md#everything-15-x64-の準備)で案内します。

#### 個別のファイルの失敗

完全な列挙の後に、個別のファイルで権限不足・消失・読取り不能などが起きた場合は、その対象の失敗を記録し、他の譜面の処理を続けます。

- 読めない譜面を、登録済み・成功として扱いません。
- 失敗は `ChartFileScanFailure` に、読取り（`read`）と解析（`parse`）の段階を区別してまとめ、一括して記録します。
- 個別のダイアログを大量に表示しません。

### 推定用探索の上限

導入先推定でディレクトリ型パッケージの入力元を探索する場合は、`BoundedSourceSurfaceEnumerator` を使い、一ルートにつき既定で50,000項目で打ち切ります。投入全体の合算の上限ではありません。上限を超えた場合は、部分的な候補を渡さず、`SourceSurfaceScanLimitExceeded` として警告します。この用途に、全件を具体化するEverythingの走査を流用しません。

上限付きの列挙の結果は次のとおりです。

| 状況 | 結果 |
| --- | --- |
| ルートの不在、通常の外部要因による失敗 | `Success=false`、`IsComplete=false`。失敗したパスと段階を診断する。 |
| 正規化できない項目、メタデータを取得できない項目 | その項目を飛ばす。 |
| 属性を取得できないディレクトリ | その下へは降下しない。 |
| 正常な再解析ポイントの除外 | 失敗にしない。 |
| 内部の不具合、資源不足 | 呼出元へ伝播する。 |

存在確認が `false` というだけでは、消失とアクセス不能を区別できないものとして扱います。

通常の不完全な結果を推定の入力として受け入れる方針と、消費側の診断は、[入力元に同梱されたリソース](../library/install-estimation.md#入力元に同梱されたリソース)に従います。これはライブラリへの反映の完全性の条件を緩めるものではありません。

単一ファイル型のBMS・BMSONパッケージと、所属のない譜面は、親ディレクトリを再帰的に列挙しません。親の `SourceDirectory` は識別情報として保持します。

### 移動・削除の失敗

`MoveDirectory(..., overwrite: true)` は、宛先を消して置き換えるのではなく、宛先にしかないファイルを残す統合移動です。衝突したファイルは移動元で上書きします。異なるボリュームへの移動は、長いパスに対応したコピーと削除で行います。コピー後に移動元の削除が失敗した場合は、両方が残り得る失敗として返します。

ごみ箱への送付でシェルAPIが失敗しても、永久削除へ切り替えません。永久削除でエラーだけを表示する経路は、長いパスに対応した直接削除を使います。

### 外部起動とExplorer

外部アプリへ渡す前に、存在確認と例外処理を行います。外部アプリが長いパスを受け取れないことを、内部のファイルの不在や成功に置き換えません。

Explorerを開く処理は `ExplorerOpenService` に集約します。

- 通常のパスと拡張長形式の候補を、副作用のないシェルAPIで事前に解決します。実際に開く副作用のあるAPIは、一操作につき一候補だけ呼びます。失敗コードが返ってもウィンドウが開く場合があるため、複数の候補を続けて開きません。
- ファイルはシェルAPIによる選択を試み、事前に解決できなければ、親フォルダを同じ方針で開きます。ファイルを選択できないことと、親フォルダも開けないことは区別します。
- `explorer.exe /select` の文字列引数を主経路にはしません。

### 実行環境の宣言

アプリのマニフェストは `longPathAware=true` を宣言します。.NET 10では、.NET Framework向けの `AppContextSwitchOverrides` に依存しません。宣言だけをI/Oの根拠にせず、ネイティブライブラリ、シェル、プレイヤー、ダイアログが持つ個別の制約を確認します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| Everythingの結果の完全な取得、短いパスの一回の取得、問い合わせ内での再利用と失敗の伝播 | [`EverythingBridge.cpp`](../../../native/EverythingBridge/EverythingBridge.cpp) の `GetCompleteResultString`、`ExecuteQuery` | [`EverythingBridgeQueryTests.cpp`](../../../native/EverythingBridge/tests/EverythingBridgeQueryTests.cpp) が、固定したSDKの実際の取得関数と製品の処理で、境界・サロゲート・再利用・失敗を確認し、公開APIの結果までパス・mtime・資源の所有を確認する。[ネイティブ専用の入口](../../../native/EverythingBridge/test-x64.ps1)で実行し、実際のEverythingとのプロセス間通信や製品の完了時間の測定とは区別する。 |
| Everythingの索引の範囲を完全とみなし、利用不能・失敗の場合だけ管理側の走査へ切り替えること | [`EverythingFileScanner`](../../../BeMusicSeeker/Models/Utils/Scanning/EverythingFileScanner.cs) のルートの事前確認、[`LibraryFileScanPipelineOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/LibraryFileScanPipelineOwner.cs) の `ExecuteChartScanWithManagedFallback` | 切り替えの条件は[`LibraryFileScanPipelineOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Scanning/LibraryFileScanPipelineOwnerTests.cs) の `ApplyActiveFileScan_EverythingUnavailableAppliesCompleteManagedFallback` が確認する。索引の範囲は外部製品の設定に依存し、保証しない範囲のため自動テストで固定しない。 |
| 1024文字を超えるBMS・BMSONの登録と、同じ完全なパスの再走査での保持 | [`BmsLibraryInitializationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs) | [`BmsLibraryInitializationFileScanTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) / [`BmsLibraryInitializationFileScanTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) の長いパスの2系列が、実際の解析・保存・再適用の後の保存行と保守行を確認する。BMSの利用者列、LR2の警告、文字コードの再読込みも確認する。不完全な走査の系列は、既存の長いパスが欠けた空でない部分結果から、追加・削除しないことを確認する。 |
| 完全な走査と不完全な入力の保護、個別の失敗での継続 | [`LibraryFileScanPipelineOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/LibraryFileScanPipelineOwner.cs)、[`BmsLibraryInitializationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs) | [`RootFileEnumerationTests`](../../../tests/BeMusicSeeker.Core.Tests/Scanning/RootFileEnumerationTests.cs)、[`LibraryFileScanPipelineOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Scanning/LibraryFileScanPipelineOwnerTests.cs) / [`LibraryFileScanPipelineOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Scanning/LibraryFileScanPipelineOwnerTests.cs)、[`BmsLibraryInitializationFileScanTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) / [`BmsLibraryInitializationFileScanTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) |
| 上限付きの列挙の通常の失敗、独立したルートでの継続、狭い捕捉 | [`BoundedSourceSurfaceEnumerator`](../../../BeMusicSeeker/Models/Utils/Scanning/BoundedSourceSurfaceEnumerator.cs) | [`RootFileEnumerationTests`](../../../tests/BeMusicSeeker.Core.Tests/Scanning/RootFileEnumerationTests.cs) の `BoundedEnumerator_DisappearingRootPreservesIndependentPathsAndReportsIncomplete` が実ファイルの列挙と不完全な理由を、`OsEnumerator_MissingRootIsIncompleteAndNotSuccessful` が高速・上限付きの列挙での初期の欠落を確認する。属性・メタデータ・除外判定の捕捉と、内部例外の伝播は静的に確認する。推定用の受入と診断は[導入先推定](../library/install-estimation.md#実装とテストの対応)が分担する。 |
| 安全な単一ファイルの保存、書込み終了後の公開、一次失敗の保持 | [`AtomicFileWriter`](../../../BeMusicSeeker/Models/Utils/FileOperations/AtomicFileWriter.cs) | [`AtomicFileWriterTests`](../../../tests/BeMusicSeeker.Core.Tests/FileOperations/AtomicFileWriterTests.cs)、[`LR2ConfigTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/LR2ConfigTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) / [`PlaylistWorkspacePersistenceCommandTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| Explorerの事前解決と、一回だけの副作用 | [`ExplorerOpenService`](../../../BeMusicSeeker/Models/Utils/Processes/ExplorerOpenService.cs) | [`ExplorerOpenServiceTests`](../../../tests/BeMusicSeeker.Core.Tests/Processes/ExplorerOpenServiceTests.cs) |
| 実際の譜面の移転とファイル操作の境界 | [`LibraryMutationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.cs) | [`BmsLibraryCatalogRelocationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Catalog/BmsLibraryCatalogRelocationTests.cs) / [`BmsLibraryCatalogRelocationTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Catalog/BmsLibraryCatalogRelocationTests.cs)、[`BmsLibraryLibraryFileOperationsServiceTests`](../../../tests/BeMusicSeeker.Core.Tests/FileOperations/BmsLibraryLibraryFileOperationsServiceTests.cs) |
| 別ボリュームへの物理移動 | `IFileMutationService` の実装 | `ResilientFileMutationServiceTests` のCrossVolumeの系列を `EnvironmentIntegration` で実行し、長い宛先、上書きを伴う統合、上書き禁止時の両側の保持を確認する。検証データの基点と実際のボリュームの確認は[専用環境の準備](../development/testing.md#資源の分離と共有)に従う。索引の親子、旧スナップショット、参照、件数は、`BmsLibraryLibraryFileOperationsServiceTests.MoveFolderAndUpdateReferences_RewritesDirectoryIndexAndBuildFolderMoveFactsTracksReferences` と `UpdateMovedFolderReferences_RewritesMultipleMovedFoldersInOnePass` が通常の検証で確認する。 |
| LR2の従来の長さを超える改名と、警告の境界 | `LibraryMutationOwner`、`Lr2CompatibilityEvaluator` | `BmsLibraryFolderRenameRefreshTests.AutoRenameChartFolders_DoesNotFailWhenLongChartFileNameExceedsLr2LegacyPathLimit` が、所有する一時領域の中で、改名後のCP932で259バイトを超えるパスを構成し、実際の改名、元の消失、先の成立、同じ識別の現在のパスを確認する。改名前のパスが旧い制限内であることは前提にしない。259/260バイトの境界の警告判定は `Lr2CompatibilityGoldenTests.EvaluatorFlagsChartPathByteBoundary` が分担する。 |

## 関連資料

[パスによる識別](path-identity.md)、[譜面ファイルの読取り](../library/chart-file-reading.md)、[警告](../library/warnings.md)。
