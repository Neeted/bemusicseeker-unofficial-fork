# ログ出力仕様

この資料は、BeMusicSeeker のアプリケーションログと性能診断ログの現行仕様をまとめる。実装履歴ではなく、現行動作と守るべき意味を正本として書く。

## 目的

ログは、ユーザー報告の解析、起動・リロード・譜面導入の性能確認、外部連携失敗の原因調査に使う。ログ出力はアプリ本来の処理結果を変えてはならない。ログ出力に失敗しても、設定保存、DB 更新、ファイル操作などの成功/失敗を別の意味へ変換しない。

## ログ基盤

通常のログ出力は `Ribbit.Logging.NLogWrapper` を経由する。アプリ内で個別に `application.log` へ直接追記する経路は持たない。

NLog が扱う file target、logging rule、ローテーション設定は `NLogWrapper` に集約する。個別機能は logger 名とログメッセージの責務だけを持ち、出力先やローテーションの判断を持たない。

## Task の完了結果と診断ログ

操作結果を呼出元へ返す処理では `LoggingAndPropagate` を使う。対象の `Task` または `Task<T>` が完了した後に診断ログを記録し、成功値、元の例外、キャンセルをそのまま呼出元へ伝播する。ログ出力の失敗は操作結果を置き換えない。

完了結果を利用しない fire-and-forget 処理では `ObserveFault` を使う。この API は待機可能な結果を返さず、元タスクが fault になった場合だけ例外を観測して診断ログを記録する。キャンセルはエラーとして記録せず、診断コールバックの失敗も未観測のタスク例外にしない。

旧 `Logging` overload は廃止し、ログ continuation の完了を操作の成功として利用する経路を持たない。

## UI 操作の失敗通知

UI event が開始した操作は、元の Task を await して完了結果を保持する。成功時だけ行う表示更新や選択復元は、操作が成功した場合に限って実行する。新たに失敗通知を持つ event は raw Task を await し、catch した元例外を共通の `NotifyMainWindowOperationFailureAsync` へ渡す。同 reporter が `ObserveFault` で元例外を診断するため、これらの event で `LoggingAndPropagate` を重ねない。従前から `LoggingAndPropagate` を使う route と、合成 Task の専用診断はその構成を維持する。`OperationCanceledException` はユーザー操作のキャンセルとして通知せず、その event を終了する。その他の例外はログを記録したうえで既存の優先エラー表示へ渡し、event の外へ漏らさずにアプリケーションを継続する。

エラー表示は既存の `IUiDialogService` を await して行い、既存翻訳の `Msg_error_unexpected` と `Error` を使う。表示結果が `UiDialogResult.Failed`、表示不可、または表示処理の例外になった場合は、その失敗を診断ログへ記録するだけで、代替 dialog や retry は行わない。表削除後の空 root 選択整理など部分成功後も必要な UI cleanup は `finally` で継続する。

プレイリスト表へのドロップでは、workspace owner の notification session が作成する receipt を唯一の失敗通知境界とする。receipt に既存の Warning または Error がある場合はそれを優先し、一次失敗が未報告の場合だけ `Msg_error_unexpected` と `Error` による generic error を一件追加する。Information だけでは一次失敗を報告済みとみなさない。`playlistTableDrop` は完了した Task の一次結果を維持したまま診断ログを観測し、同じ失敗を汎用 dialog で重ねて表示しない。通知 subscriber の失敗は診断ログだけに記録し、一次失敗を置き換えず再試行しない。

## 出力先

ログは実行ファイルと同じディレクトリにある `log/` フォルダへ出力する。

| ファイル | 用途 |
| --- | --- |
| `log/application.log` | 通常の診断ログ。WARN/ERROR だけでなく、通常運用で有用な INFO も含む。 |
| `log/install-performance.log` | 起動、DB 読み込み、ファイルスキャン、導入先推定、プレイリスト同期などの性能・段階診断ログ。 |

`InstallPerformance*` logger は `install-performance.log` に分離し、`application.log` へ重複出力しない。

ログファイルの文字コードは UTF-8 とし、BOM は付けない。対象は現在の `application.log` / `install-performance.log` と、ローテーション後の archive を含む。既存ログへ追記する場合、過去に別エンコードで書かれた部分と UTF-8 の追記部分が同一ファイル内で混在することは許容する。

## ログレベル

既定のログレベルは `INFO` とする。通常起動で `application.log` と `install-performance.log` の両方を出力する。

`--log-level=Info` は互換引数として受け付けるが、既定と同じ意味である。既存の `LaunchWithInfoLog.bat` から起動しても動作は変わらない。

`--log-level=Warn` / `--log-level=Warning` / `--log-level=Error` は、ログ量を抑える明示指定として維持する。無効な `--log-level` 値は従来互換のため `Warn` 扱いとし、警告ログを出す。

`install-performance.log` は INFO レベルの性能ログを前提にしているため、明示的に INFO 未満を抑制した場合は出力対象から外れる。

## 性能markerの計測契約

処理速度の主指標、operation完了境界、対象件数と差分、入れ子・並列timerの扱いは [performance-and-scale.md](performance-and-scale.md) を正本とする。markerの名前だけで計測範囲を推定させず、範囲変更は旧版との比較可否を明示する。未計測を0msと解釈させず、失われたmarkerは欠測として扱う。

大規模経路では必要なphase単位の集約counterを使い、全件走査・rootコピー・再構築の回数、total / affected件数、DB読込row、I/O量を時間と対応付ける。新しいcounter全てが現行ログに実装済みという意味ではない。計測のために per-resource 同期ログや巨大collectionの文字列化を追加しない。

## ローテーション

ログファイルは NLog の `FileTarget` でサイズベースのローテーションを行う。独自のファイル移動・削除処理は持たない。

現行値:

- 1 ファイルあたり 20 MiB を超えたらアーカイブする。
- 各ログ種別につき最大 5 世代を保持する。
- アーカイブは `log/archive/` に置く。

例:

```text
log/application.log
log/install-performance.log
log/archive/application.1.log
log/archive/install-performance.1.log
```

## ドキュメントと配布

`LaunchWithInfoLog.bat` は新しいリリースパッケージには含めない。通常起動で INFO ログが出るため、ユーザー向け導線は `log/` フォルダを見る案内に統一する。

既に配布済みの `LaunchWithInfoLog.bat` は `BeMusicSeeker.exe --log-level=Info` を呼ぶだけなので、互換引数として動作を維持する。
