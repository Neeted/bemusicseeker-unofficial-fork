# Path Length and Chart File I/O Policy

本資料は、譜面ファイルと譜面由来リソースを読み取るときの長パス対応、内部ファイル操作、読めないファイルの扱いを定義する。`path-identity.md` は DB 上の path 同一性を扱い、本資料は同じ path 文字列を実ファイル I/O へ渡す境界を扱う。

## 基本方針

- DB や runtime model に保存する path は、通常の absolute path 文字列に正規化する。drive path や UNC path の `\\?\` prefix は保存しない。`\\?\Volume{...}` や `\\?\GLOBALROOT` のように通常の保存 path へ安全に戻せない形式は、通常 root として扱わず、無理に prefix を剥がして別 path に変換しない。
- 実ファイル I/O では、必要に応じて `LongPathFileSystem` が Win32 extended-length path へ変換してから操作する。
- 譜面ファイルとして読める長パスは通常の譜面として扱い、`song` / `bmson_song` / `chart_digest_map` / `maintenance` / `chart_info` への登録対象にする。
- `song.db` はユーザーが直接編集して守るべき正本ではなく、root scan の列挙結果 cache として扱う。ただし cache 更新は authoritative complete scan が得られた場合だけ行う。
- root scan が不完全、失敗、上限超過、root 不在、子ディレクトリ列挙失敗、metadata 取得不能になった場合、その scan から add / update / delete のいずれも行わない。起動時スキャンを行わなかった状態に寄せ、既存 DB の読み込み、スコア読み込み、保留パッケージ復元などは続行する。
- LR2 互換性は BeMusicSeeker の読み取り可否とは分けて評価する。LR2 の legacy path 長を超える可能性がある BMS は、登録したうえで LR2 warning を付与する。OpenLR2 は改善可能性があるため、warning 名や文言には含めない。
- standalone mode でも、LR2 legacy path として明らかに成立しない root は登録前に拒否する。root 判定は CP932 で `root + separator + "a.bms"` が `Lr2CompatibilityEvaluator.MaxLegacyPathBytes` 以下、かつ CP932 encode 可能であることを条件にする。
- BeMusicSeeker 内部で完結するファイル操作は、標準 `File` / `Directory` / VisualBasic `FileSystem` の存在確認や変更 API に直接依存せず、`LongPathFileSystem` または `IFileMutationService` を通す。
- 導入先推定で directory package root 配下を探索する処理は、長パス対応だけでなく探索範囲の上限も明示する。single-file package / loose chart の親 directory は package boundary として再帰列挙しない。通常 directory package の範囲を超える巨大 directory では、全件 materialize せず打ち切り warning にする。
- complete scan 後に個別ファイルを読む段階で、長パスで読めない、権限が無い、列挙時点から削除されたなどの recoverable なファイル単位エラーは、初期化全体の失敗ダイアログにしない。対象ファイルを `SongTableFileCheckResult.FileScanFailures` と性能ログへ集約し、他の譜面の登録を継続する。
- 失敗を成功扱いにはしない。読めなかった譜面は DB に登録せず、失敗ファイルとして残す。
- 外部アプリへ長パスを渡した後の互換性は外部アプリ側の制約に従う。BeMusicSeeker 側では、外部アプリが受け付けないことを内部ファイルの不在や成功扱いに変換しない。

## 完全 scan と DB 更新

root scan 用の列挙結果は、`Success == true` かつ `IsComplete == true` かつ `ScanLimitExceeded == false` のときだけ authoritative complete とみなす。

Everything bridge が正常成功した scan は authoritative complete とみなす。Everything bridge が失敗して managed fallback を使った場合でも、managed fallback が同じ条件を満たす場合だけ authoritative complete とみなす。bridge contract mismatch は配置やバージョン不整合なので、通常の incomplete filesystem scan warning には丸めず hard failure として扱う。

incomplete scan の場合、`LibraryFileScanPipelineOwner.ApplyFileScanDiff()` は警告を queue し、file diff / LR2 folder sync / storage mutation / DB commit / resource index 差し替えに進まない。`BmsLibraryInitializationService.ApplyFileScanDiff()` 側も二重防御として、非 authoritative scan から diff を作らず空 result を返す。

scan が incomplete でも、既存 `song.db` の読み込み結果はそのまま使用する。これは「古い DB の内容を保護する」ためではなく、異常なファイルシステム状態から得られた部分列挙で cache を中途半端に更新しないためである。

## 正規 I/O 境界

譜面 bytes や更新日時を読む経路は、原則として次の entry point を使う。

- `ChartFileContentReader.ReadBuffer(path)`
- `ChartFileContentReader.ReadSnapshot(path)`
- `ChartFileContentReader.CreateSnapshot(buffer)`
- `LongPathFileSystem.ReadAllBytes(path)`
- `LongPathFileSystem.OpenRead(path)`
- `LongPathFileSystem.GetLastWriteTimeUtc(path)`

互換 API の `BMSFile.CreateBMSFileFromFile(...)`、`BmsonSongParser.Parse(path)`、`ChartInfoParser.Parse(path)` も、内部では `LongPathFileSystem` を使う。

内部ファイル操作の存在確認、列挙、属性変更、コピー、移動、削除、タイムスタンプ設定は、原則として次の entry point を使う。

- `LongPathFileSystem.FileExists(path)` / `DirectoryExists(path)` / `EntryExists(path)`
- `LongPathFileSystem.EnumerateFiles(...)` / `EnumerateDirectories(...)` / `EnumerateFileSystemEntries(...)`
- `LongPathFileSystem.CopyFile(...)` / `CopyDirectory(...)`
- `IFileMutationService.EnsureDirectory(...)`
- `IFileMutationService.MoveFile(...)` / `MoveDirectory(...)`
- `IFileMutationService.DeleteFileDirect(...)` / `DeleteDirectoryDirect(...)`
- `IFileMutationService.SetTimestamps(...)`

`IFileMutationService` は ReadOnly 補正、短時間リトライ、診断ログ、`FileMutationException` への例外集約を提供する。導入、マージ、移動、削除、拡張子変更、スマート上書きなど、ユーザー操作に紐づく変更系ファイル操作はこの service を経由する。

`MoveDirectory(..., overwrite: true)` は宛先ディレクトリを削除して置き換えるのではなく、既存宛先を保持したままマージ移動する。衝突ファイルは移動元で上書きし、宛先にしかないファイルは残す。source と destination の volume root が異なる場合、`MoveFile(...)` / `MoveDirectory(...)` は long-path 対応の copy + delete で move 相当を実現する。copy 成功後の source delete 失敗は成功扱いにせず、source と destination が両方残り得る失敗として呼び出し側へ返す。

`DeleteFileShell(...)` / `DeleteDirectoryShell(...)` でごみ箱送りを指定した場合は、シェル API の制約を隠して永久削除へフォールバックしない。永久削除かつ UI 表示がエラーダイアログのみの経路では、長パス対応の直接削除を使う。

## 適用範囲

長パス対応は初期化時の譜面読み取りだけに限定しない。ただし、すべての app-local I/O を同時に置き換えるのではなく、DB の意味やユーザー操作の成否に直結する箇所から優先する。

P0 は今回の正本範囲である。BeMusicSeeker 内でファイルシステムへ直接触れる次の処理は、標準 `File` / `Directory` / VisualBasic `FileSystem` API へ直接依存せず、上記の正規 I/O 境界を経由する。

- root scan と、その Everything fallback の列挙 / metadata 取得。
- Ribbit 経由の BMS 読み取り、bmson 読み取り、内蔵プレーヤーの譜面 / 音声リソース読み取り、音声ファイル変換時の譜面読み取り。
- 譜面 text surface、`folderinfo.txt`、`.lr2folder` の発見と読み取り。
- 導入、移動、削除、拡張子変更、スマート上書き判定、マージ移動などのユーザー操作に紐づく変更系処理。
- beatoraja `.bmt` の gzip 書き込み、manifest 読み書き、managed `.bmt` cleanup。
- LR2 カスタムフォルダ `.lr2folder` の生成、stale file cleanup、空ディレクトリ削除。
- LR2 バックアップの対象存在確認、世代削除、ファイル / ディレクトリコピー。

譜面 / パッケージの変更系処理は、I/O 境界に加えて [library-mutation-boundary.md](library-mutation-boundary.md) の共通 mutation 境界を通す。長パス対応はファイル操作の正しさを担保し、mutation 境界は二重実行防止、UI 操作ブロック、dialog 表示タイミング、model lock 契約を担保する。

P2 は、譜面管理機能の本流ではないが app 内の一貫性として順次寄せる範囲である。IR cache、設定ファイル、export/import 補助、外部ツール連携の一部存在確認などが該当する。P3 はアプリ同梱 metadata bundle、test fixture、ログ、release script など、ユーザーの譜面 path とは独立した app-local I/O である。P2/P3 の direct `System.IO` は見つかっただけで即 P0 欠陥とは扱わず、変更する場合はそれぞれの機能単位で方針を決める。

導入先推定の source surface scan は directory package の package root だけに `BoundedSourceSurfaceEnumerator` を使い、1 root ごとに既定 50,000 filesystem entry で打ち切る。この上限は投入バッチ全体の合算ではない。打ち切った場合、部分的な resource surface を推定入力へ渡さず、`SourceSurfaceScanLimitExceeded` warning として扱う。Everything bridge や通常 root scan fallback のような全件 materialize 型の列挙は、この用途では使わない。single-file BMS / bmson package と loose BMS / bmson chart は親 directory を列挙せず、source identity として親 `SourceDirectory` だけを保持する。

外部アプリ起動、関連付け起動、Explorer 起動は、BeMusicSeeker 側で対象 path の存在確認と例外処理を行う。起動先アプリが extended-length path を解釈できない場合でも、BeMusicSeeker はその失敗をファイル未存在や内部成功へ変換しない。

Explorer 起動は `ExplorerOpenService` に集約する。ファイルを開く場合は `explorer.exe /select` の文字列引数を主経路にせず、Shell API で対象ファイルの選択を試みる。Shell API / Explorer 拡張は失敗コードを返しても Explorer window や tab を開く副作用を起こす場合があるため、1 つのユーザー操作で副作用のある open 呼び出しを複数回連続実行しない。通常 path / extended-length path の候補選定は `SHParseDisplayName` などの事前解決で行い、実際に開く API 呼び出しは選択した 1 候補だけに限定する。ファイル path を事前解決できない場合は、問題箇所を確認できるように親ディレクトリを同じ方針で開く。フォルダを開く入口も同じ service を通し、深い親ディレクトリを直接開けることを優先する。ファイル選択は best effort とし、選択できないことを親フォルダを開けないことと同一視しない。

## File Diff での失敗集約

`ApplyFileScanDiff()` の reader / parser stage では、ファイル単位の recoverable I/O 例外を次のように扱う。

- `read`: bytes または file metadata の読み取り前後で失敗した。
- `parse`: snapshot 作成後の軽量 parse など、譜面ファイル単体の処理で recoverable I/O / path 例外が発生した。

これらは `ChartFileScanFailure` として `Path`, `ChartKind`, `Stage`, `ExceptionType`, `Message` を保持する。呼び出し側は個別ダイアログを出さず、性能ログへ summary と各ファイルを記録する。

## .NET 10 / manifest

現行appは.NET 10を使い、.NET Framework向け`AppContextSwitchOverrides`には依存しない。app manifestで`longPathAware=true`を宣言する。

譜面読み取りとroot scanの正本はmanifestだけに依存せず、`LongPathFileSystem`へ集約する。外部tool、native library、shell、player等には独自のpath制約があり得るため、UIでは選択可否、実I/O可否、外部component互換性を別に判定する。

## 関連仕様

- [path-identity.md](path-identity.md): DB 上の path 同一性、case-sensitive exact match。
- [chart-file-read-pipeline.md](chart-file-read-pipeline.md): 譜面 bytes / snapshot / inline parse pipeline。
- [library-mutation-boundary.md](library-mutation-boundary.md): 譜面 / パッケージ操作の共通 mutation 境界。
- [warning-model.md](warning-model.md): `Lr2PathTooLong` warning の表示仕様。
