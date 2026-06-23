# Path Length and Chart File I/O Policy

本資料は、譜面ファイルと譜面由来リソースを読み取るときの長パス対応、内部ファイル操作、読めないファイルの扱いを定義する。`path-identity.md` は DB 上の path 同一性を扱い、本資料は同じ path 文字列を実ファイル I/O へ渡す境界を扱う。

## 基本方針

- DB や runtime model に保存する path は、通常の absolute path 文字列に正規化する。`\\?\` prefix は保存しない。
- 実ファイル I/O では、必要に応じて `LongPathFileSystem` が Win32 extended-length path へ変換してから操作する。
- 譜面ファイルとして読める長パスは通常の譜面として扱い、`song` / `bmson_song` / `chart_digest_map` / `maintenance` / `chart_info` への登録対象にする。
- LR2 互換性は読み取り可否とは分けて評価する。LR2 の legacy path 長を超える可能性がある BMS は、登録したうえで `Lr2PathTooLong` warning を付与する。
- BeMusicSeeker 内部で完結するファイル操作は、標準 `File` / `Directory` / VisualBasic `FileSystem` の存在確認や変更 API に直接依存せず、`LongPathFileSystem` または `IFileMutationService` を通す。
- 導入先推定のように、ユーザーが指定した path の親 directory や配下を探索する処理は、長パス対応だけでなく探索範囲の上限も明示する。通常 package の範囲を超える巨大 directory では、全件 materialize せず打ち切り warning にする。
- 長パスで読めない、権限が無い、列挙時点から削除されたなどの recoverable なファイル単位エラーは、初期化全体の失敗ダイアログにしない。対象ファイルを `SongTableFileCheckResult.FileScanFailures` と性能ログへ集約し、他の譜面の登録を継続する。
- 失敗を成功扱いにはしない。読めなかった譜面は DB に登録せず、失敗ファイルとして残す。
- 外部アプリへ長パスを渡した後の互換性は外部アプリ側の制約に従う。BeMusicSeeker 側では、外部アプリが受け付けないことを内部ファイルの不在や成功扱いに変換しない。

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

`MoveDirectory(..., overwrite: true)` は宛先ディレクトリを削除して置き換えるのではなく、既存宛先を保持したままマージ移動する。衝突ファイルは移動元で上書きし、宛先にしかないファイルは残す。

`DeleteFileShell(...)` / `DeleteDirectoryShell(...)` でごみ箱送りを指定した場合は、シェル API の制約を隠して永久削除へフォールバックしない。永久削除かつ UI 表示がエラーダイアログのみの経路では、長パス対応の直接削除を使う。

## 適用範囲

長パス対応は初期化時の譜面読み取りだけに限定しない。BeMusicSeeker 内でファイルシステムへ直接触れる次の処理は、標準 `File` / `Directory` / VisualBasic `FileSystem` API へ直接依存せず、上記の正規 I/O 境界を経由する。

- root scan と、その Everything fallback の列挙 / metadata 取得。
- Ribbit 経由の BMS 読み取り、内蔵プレーヤーの譜面 / 音声リソース読み取り、音声ファイル変換時の譜面読み取り。
- 導入、移動、削除、拡張子変更、スマート上書き判定、マージ移動などのユーザー操作に紐づく変更系処理。
- beatoraja `.bmt` の gzip 書き込み、manifest 読み書き、managed `.bmt` cleanup。
- LR2 カスタムフォルダ `.lr2folder` の生成、stale file cleanup、空ディレクトリ削除。
- LR2 バックアップの対象存在確認、世代削除、ファイル / ディレクトリコピー。

導入先推定の source surface scan は `BoundedSourceSurfaceEnumerator` を使い、1 つの source surface ごとに既定 50,000 filesystem entry で打ち切る。この上限は投入バッチ全体の合算ではなく、1 つの導入先推定対象 package / source directory を BMS package 境界として扱えるかの上限である。打ち切った場合、部分的な resource surface を推定入力へ渡さず、`SourceSurfaceScanLimitExceeded` warning として扱う。Everything bridge や通常 root scan fallback のような全件 materialize 型の列挙は、この用途では使わない。

外部アプリ起動、関連付け起動、Explorer 起動は、BeMusicSeeker 側で対象 path の存在確認と例外処理を行う。起動先アプリが extended-length path を解釈できない場合でも、BeMusicSeeker はその失敗をファイル未存在や内部成功へ変換しない。

Explorer 起動は `ExplorerOpenService` に集約する。ファイルを開く場合は `explorer.exe /select` の文字列引数を主経路にせず、Shell API で対象ファイルの選択を試みる。Shell API / Explorer 拡張は失敗コードを返しても Explorer window や tab を開く副作用を起こす場合があるため、1 つのユーザー操作で副作用のある open 呼び出しを複数回連続実行しない。通常 path / extended-length path の候補選定は `SHParseDisplayName` などの事前解決で行い、実際に開く API 呼び出しは選択した 1 候補だけに限定する。ファイル path を事前解決できない場合は、問題箇所を確認できるように親ディレクトリを同じ方針で開く。フォルダを開く入口も同じ service を通し、深い親ディレクトリを直接開けることを優先する。ファイル選択は best effort とし、選択できないことを親フォルダを開けないことと同一視しない。

## File Diff での失敗集約

`ApplyFileScanDiff()` の reader / parser stage では、ファイル単位の recoverable I/O 例外を次のように扱う。

- `read`: bytes または file metadata の読み取り前後で失敗した。
- `parse`: snapshot 作成後の軽量 parse など、譜面ファイル単体の処理で recoverable I/O / path 例外が発生した。

これらは `ChartFileScanFailure` として `Path`, `ChartKind`, `Stage`, `ExceptionType`, `Message` を保持する。呼び出し側は個別ダイアログを出さず、性能ログへ summary と各ファイルを記録する。

## AppContext

.NET Framework で標準 API が長パスを扱いやすいよう、アプリ設定では次を明示する。

```xml
<AppContextSwitchOverrides value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false" />
```

ただし、譜面読み取りの正本はこの設定だけに依存せず、`LongPathFileSystem` に集約する。OS やランタイム設定差で標準 API の挙動が変わっても、譜面読み取り経路の意味を変えないためである。

## 関連仕様

- [path-identity.md](path-identity.md): DB 上の path 同一性、case-sensitive exact match。
- [chart-file-read-pipeline.md](chart-file-read-pipeline.md): 譜面 bytes / snapshot / inline parse pipeline。
- [warning-model.md](warning-model.md): `Lr2PathTooLong` warning の表示仕様。
