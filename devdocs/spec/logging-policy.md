# ログ出力仕様

この資料は、BeMusicSeeker のアプリケーションログと性能診断ログの現行仕様をまとめる。実装履歴ではなく、現行動作と守るべき意味を正本として書く。

## 目的

ログは、ユーザー報告の解析、起動・リロード・譜面導入の性能確認、外部連携失敗の原因調査に使う。ログ出力はアプリ本来の処理結果を変えてはならない。ログ出力に失敗しても、設定保存、DB 更新、ファイル操作などの成功/失敗を別の意味へ変換しない。

## ログ基盤

通常のログ出力は `Ribbit.Logging.NLogWrapper` を経由する。アプリ内で個別に `application.log` へ直接追記する経路は持たない。

NLog が扱う file target、logging rule、ローテーション設定は `NLogWrapper` に集約する。個別機能は logger 名とログメッセージの責務だけを持ち、出力先やローテーションの判断を持たない。

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
