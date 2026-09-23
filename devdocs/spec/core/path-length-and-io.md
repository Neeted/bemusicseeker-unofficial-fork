# 長いパスとファイル入出力

## 目的と適用範囲

譜面とリソースの読取り、内部のファイル変更、長いパスと外部アプリの互換性を定めます。DB行の文字列としての同一性は[パスによる識別](path-identity.md)とは別の責務です。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 拡張長形式 | Win32の長いパス用の表記。保存用の通常パスとは分け、実I/O直前に変換します。 |
| 完全な走査 | 対象範囲を成功かつ打切りなしで列挙できた結果。個別譜面の詳細解析成功とは別です。 |

## 仕様

### 保存形式と実ファイルの操作

新しく生成する保存用パスは通常の絶対パスとし、ドライブ・UNCの `\\?\` 接頭辞はDBやモデルへ保存しません。通常のパスに安全に戻せない `\\?\Volume{...}` や `\\?\GLOBALROOT` を、単に接頭辞を外して別の場所へ変換しません。既存DBキーの照合は元の完全一致文字列を維持します。

実ファイルのI/Oは `LongPathFileSystem` が必要に応じてWin32の拡張長形式へ変換します。読める長いパスの譜面は通常どおり登録し、内部で読めることとLR2等の外部アプリで使えることを別に判定します。

LR2の従来のパス長を超えるBMSは、登録したうえでLR2互換警告を付けます。OpenLR2の挙動まで同じ警告で断定しません。ルート登録は単体モードでも、CP932で `root + separator + "a.bms"` が符号化可能で、`Lr2CompatibilityEvaluator.MaxLegacyPathBytes` 以下であることを確認します。

### 正規のI/O窓口

| 用途 | 入口 |
| --- | --- |
| 譜面内容・ハッシュ・ファイル時刻 | `ChartFileContentReader.ReadBuffer` / `ReadSnapshot` / `CreateSnapshot`、`LongPathFileSystem.ReadAllBytes` / `OpenRead` / `GetLastWriteTimeUtc`。 |
| 存在確認・列挙 | `LongPathFileSystem.FileExists` / `DirectoryExists` / `EntryExists`、各 `Enumerate*`。 |
| 内部の直接コピー・公開 | `LongPathFileSystem.CopyFile` / `CopyDirectory` / `PublishFile`、`AtomicFileWriter.Write`。 |
| 利用者操作に伴う変更 | `IFileMutationService` のディレクトリ作成、ファイル・ディレクトリ移動、直接削除、時刻設定。 |

BMS・BMSON・譜面情報のパス指定解析APIも、この窓口で読みます。`IFileMutationService` は読取り専用属性の補正、既存の短時間再試行、診断、`FileMutationException` への集約を担当します。各利用側に独自の再試行を重ねません。

`AtomicFileWriter` は同じディレクトリの一意な一時ファイルへ保存し、書込み側が閉じてから公開します。LR2の `config.xml` とプレイリストSQLバックアップもこの境界を使います。公開前に保存先を削除せず、自分が所有しない一時ファイルをまとめて消さず、失敗を成功へ変えません。

### 完全な走査だけを反映する

走査は `Success == true`、`IsComplete == true`、`ScanLimitExceeded == false` のすべてを満たす場合だけ、反映の根拠に使えます。Everythingが利用できず管理側で走査する場合も条件は同じです。ネイティブ連携の契約・版不一致は配置異常として失敗させ、単なる不完全走査の警告にしません。

ルート不在、子ディレクトリ列挙失敗、メタデータ取得不能、上限超過などで走査範囲が不完全なら、その走査から追加・更新・削除・LR2同期・リソース索引置換をしません。既存DB、スコア、保留パッケージの読込みは継続できます。部分列挙でカタログを中途半端に更新しないための保護です。

`LibraryFileScanPipelineOwner.ApplyFileScanDiff()` と `BmsLibraryInitializationService.ApplyFileScanDiff()` の両方が非完全入力を拒否します。単に空の正常走査へ読み替えて削除へ進みません。

完全列挙の後に個別ファイルで権限不足・消失・読取り不能などが生じた場合は、対象の失敗を記録し、他の譜面の処理を続けます。読めない譜面を登録・成功扱いにはしません。`ChartFileScanFailure` に `Path`、`ChartKind`、`Stage`、`ExceptionType`、`Message` を保持し、`read` と `parse` を区別してまとめて記録します。個別ダイアログを大量に表示しません。

### 内部操作への適用範囲

長いパスへの対応を、初期化の読取りだけに限定しません。ルート走査、譜面・音声リソースの読取り、譜面テキスト・`folderinfo.txt`・`.lr2folder`、導入・移動・削除・拡張子変更・統合・上書き、beatoraja出力・管理ファイルの整理、LR2カスタムフォルダ・バックアップは正規のI/O窓口を使います。変更操作はさらに[変更セッション](../library/mutations.md)の受付・通知・ロック境界に従います。

IRキャッシュ、設定、取込み・出力補助、外部連携の一部、同梱メタデータ、テストデータ、ログ、配布スクリプトなどは個別機能のI/O契約を持ちます。直接の `System.IO` があることだけで譜面管理の欠陥と断定せず、その対象パスと変更範囲で評価します。

### 推定用探索の上限

導入先推定のディレクトリ型パッケージは `BoundedSourceSurfaceEnumerator` を使い、**一ルートにつき既定50,000項目**で打ち切ります。投入全体の合算上限ではありません。上限超過時は部分的な候補を渡さず `SourceSurfaceScanLimitExceeded` として警告します。この用途に全件具体化するEverything走査を流用しません。

単一ファイル型のBMS・BMSONパッケージと所属のない譜面は、親ディレクトリを再帰列挙しません。親の `SourceDirectory` は識別情報として保持します。

### 移動・削除の失敗

`MoveDirectory(..., overwrite: true)` は宛先を消して置き換えるのではなく、宛先にしかないファイルを残す統合移動です。衝突ファイルは元で上書きします。異なるボリュームへの移動は長いパスに対応したコピーと削除で実現します。コピー後の元削除失敗は、両方が残り得る失敗として返します。

ごみ箱送りでシェルAPIが失敗しても、永久削除へ切り替えません。永久削除かつエラーだけを表示する経路は、長いパスに対応した直接削除を使います。

### 外部起動とExplorer

外部アプリへ渡す前に存在確認と例外処理を行いますが、外部アプリが長いパスを受け取れないことを、内部のファイル不在や成功に置き換えません。

Explorerは `ExplorerOpenService` に集約します。通常パスと拡張長形式の候補を `SHParseDisplayName` 等で事前解決し、実際に開く副作用のあるAPIは一操作で一候補だけ呼びます。失敗コードでもウィンドウが開く場合があるため、複数候補を続けて開きません。

ファイルはシェルAPIによる選択を試み、事前解決できなければ親フォルダを同じ方針で開きます。ファイルの選択ができないことと、親フォルダも開けないことは区別します。`explorer.exe /select` の文字列引数を主経路にはしません。

### 実行環境の宣言

アプリのマニフェストは `longPathAware=true` を宣言します。.NET 10では.NET Framework向けの `AppContextSwitchOverrides` に依存しません。ただし宣言だけをI/Oの根拠にせず、ネイティブライブラリ、シェル、プレイヤー、ダイアログが持つ個別制約を確認します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 完全走査と不完全入力の保護、個別失敗の継続 | [`LibraryFileScanPipelineOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/LibraryFileScanPipelineOwner.cs)、[`BmsLibraryInitializationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs) | [`RootFileEnumerationTests`](../../../BeMusicSeeker.Tests/Scanning/RootFileEnumerationTests.cs)、[`LibraryFileScanPipelineOwnerTests`](../../../BeMusicSeeker.Tests/Scanning/LibraryFileScanPipelineOwnerTests.cs)、[`BmsLibraryInitializationFileScanTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) |
| 安全な単一ファイル保存、書込み終了後の公開、一次失敗の保持 | [`AtomicFileWriter`](../../../BeMusicSeeker/Models/Utils/FileOperations/AtomicFileWriter.cs) | [`AtomicFileWriterTests`](../../../BeMusicSeeker.Tests/FileOperations/AtomicFileWriterTests.cs)、[`LR2ConfigTests`](../../../BeMusicSeeker.Tests/Lr2/LR2ConfigTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| Explorerの事前解決と一回だけの副作用 | [`ExplorerOpenService`](../../../BeMusicSeeker/Models/Utils/Processes/ExplorerOpenService.cs) | [`ExplorerOpenServiceTests`](../../../BeMusicSeeker.Tests/Processes/ExplorerOpenServiceTests.cs) |
| 実際の譜面移転とファイル操作境界 | [`LibraryMutationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.cs) | [`BmsLibraryCatalogRelocationTests`](../../../BeMusicSeeker.Tests/Catalog/BmsLibraryCatalogRelocationTests.cs)、[`BmsLibraryLibraryFileOperationsServiceTests`](../../../BeMusicSeeker.Tests/FileOperations/BmsLibraryLibraryFileOperationsServiceTests.cs) |

## 関連資料

[パスによる識別](path-identity.md)、[ファイル読取り](../library/chart-file-reading.md)、[警告](../library/warnings.md)。
