# Path Length and Chart File I/O Policy

本資料は、譜面ファイルと譜面由来リソースを読み取るときの長パス対応と、読めないファイルの扱いを定義する。`path-identity.md` は DB 上の path 同一性を扱い、本資料は同じ path 文字列を実ファイル I/O へ渡す境界を扱う。

## 基本方針

- DB や runtime model に保存する path は、通常の absolute path 文字列に正規化する。`\\?\` prefix は保存しない。
- 実ファイル I/O では、必要に応じて `LongPathFileSystem` が Win32 extended-length path へ変換してから読み取る。
- 譜面ファイルとして読める長パスは通常の譜面として扱い、`song` / `bmson_song` / `chart_digest_map` / `maintenance` / `chart_info` への登録対象にする。
- LR2 互換性は読み取り可否とは分けて評価する。LR2 の legacy path 長を超える可能性がある BMS は、登録したうえで `Lr2PathTooLong` warning を付与する。
- 長パスで読めない、権限が無い、列挙時点から削除されたなどの recoverable なファイル単位エラーは、初期化全体の失敗ダイアログにしない。対象ファイルを `SongTableFileCheckResult.FileScanFailures` と性能ログへ集約し、他の譜面の登録を継続する。
- 失敗を成功扱いにはしない。読めなかった譜面は DB に登録せず、失敗ファイルとして残す。

## 正規 I/O 境界

譜面 bytes や更新日時を読む経路は、原則として次の entry point を使う。

- `ChartFileContentReader.ReadBuffer(path)`
- `ChartFileContentReader.ReadSnapshot(path)`
- `ChartFileContentReader.CreateSnapshot(buffer)`
- `LongPathFileSystem.ReadAllBytes(path)`
- `LongPathFileSystem.OpenRead(path)`
- `LongPathFileSystem.GetLastWriteTimeUtc(path)`

互換 API の `BMSFile.CreateBMSFileFromFile(...)`、`BmsonSongParser.Parse(path)`、`ChartInfoParser.Parse(path)` も、内部では `LongPathFileSystem` を使う。

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
