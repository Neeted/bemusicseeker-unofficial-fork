# ログ出力

## 目的と適用範囲

利用者の報告、起動・導入などの性能、外部連携の失敗を調べるためのログの仕様です。ログの出力先・レベル・ローテーション、Taskの診断、記録する事実の粒度を定めます。

診断の失敗で、設定保存・DB更新・ファイル操作の成功、元の例外、取消の意味を変えません。続行の条件と終了の判断は[例外・操作結果・診断](exception-handling.md)を正本とします。ログを記録したことや通知処理が終わったことは、アプリを続行する根拠になりません。

## 用語

[共通用語集](../glossary.md)の診断、計測点、終端を使います。

| 用語 | 意味 |
| --- | --- |
| 通常診断 | 警告・エラーと、調査に有用な情報。`log/application.log` へ出力する。 |
| 性能ログ | 起動、DB、走査、推定、同期などの性能・段階の診断。`log/install-performance.log` へ出力する。 |
| `LoggingAndPropagate` | 結果を返すTaskに使う診断の拡張。元の結果をそのまま呼出元へ伝える。 |
| `ObserveFault` | 結果を利用しないTaskの例外を観測する拡張。 |

## 仕様

### 出力の管理主体

`Ribbit.Logging.NLogWrapper` が、NLogの出力先、規則、ローテーションを管理します。機能側は `FileLogger` または `GetLogger(name)` を使い、ログファイルへ直接追記したり、NLogの構成を変更したりしません。

### 診断の保証範囲

通常のログ環境で、主原因と、必要な操作・対象の情報を調査できることを最低限とします。代表となる元の例外を既存の出力先へ渡し、すでに保持している原因を安価に記録できる場合は利用します。次は保証しません。

- 全例外・全スタック・全段階の完全な保存。
- 重複の完全な排除。
- ログの書込みの成功。

診断専用の履歴・運搬構造・代替の保存先は追加しません。

業務状態を変更しない純粋な診断出力の失敗は、その狭い境界で局所化します。欠落しても安全性が変わらないことが、局所化の根拠です。

- 局所化してよいのは、ログの取得・整形・出力の範囲だけです。任意のコールバック、DB更新、必要な参照の反映を、同じ捕捉に含めません。
- ログの失敗を元の操作結果へ合成せず、別の保存先・再通知・再試行で救済し続けません。
- 記録できなかったことを、記録の成功として扱いません。
- 元の業務失敗に必要な終了の判断を取り消しません。

Taskの診断拡張は、回復・終了の判断を代行しません。実処理を所有する入口で続行の条件を確認し、条件外の失敗を終了の判断へ接続します。元のTaskが保持する失敗の集合をそのまま渡す契約は、各層で全原因を収集・保存する義務を意味しません。

### Taskの結果

結果を返す処理は `LoggingAndPropagate` を使います。元のTaskの完了後に診断し、成功値・元の例外・取消をそのまま呼出元へ伝えます。診断の失敗で結果を置き換えません。結果を利用しない処理の `ObserveFault` は、待機できる結果を返さず、失敗したときだけ例外を観測します。

- 元のTaskが `Canceled` の場合は、エラーとして記録しません。
- `Faulted` の `OperationCanceledException` や `TaskCanceledException` を、型だけで取消へ読み替えません。
- 元のTaskの複数の失敗を、最初の一件へ切り詰めません。
- 診断のコールバックの失敗を、未観測の例外にしません。
- ログの継続処理が完了しただけで、本体が成功したと見なす経路は設けません。

### 画面操作の失敗

画面のイベントは元のTaskを待ち、成功後の表示・選択の復元は成功したときだけ行います。

共通の失敗通知を使うイベントでは、未加工のTaskを待ち、元の例外を `NotifyMainWindowOperationFailureAsync` へ渡します。この処理が元の例外と操作名を局所的に診断するため、同じ例外へ `LoggingAndPropagate` を重ねません。すでに同じ拡張を使っている処理と、合成したTask固有の診断は、その責務を維持します。

取消と失敗は次のように扱います。

| 失敗 | 扱い |
| --- | --- |
| 操作が所有する終了・取消の要求による `OperationCanceledException` | 通常のエラー通知をしない。確定状態と後片付けを確認し、他の致命的な失敗を取消で隠さない。 |
| 所有されていない取消型の例外、通信の期限切れ | 型だけで正常な取消にしない。 |
| 続行の条件を満たす操作の失敗 | ログと既存の優先エラー表示へ渡して、操作を終える。 |
| 続行の条件を満たさない失敗 | 通知だけで通常の利用へ戻さず、操作の所有者から共通の終了の判断へ渡す。この接続が既存の全入口で成立している保証ではない。 |

表示は `IUiDialogService` を待ち、一般の失敗通知は `Msg_error_unexpected` と `Error` を使います。

- 例外の本文や集約例外の内訳は要求しません。確定済みの変更・未完了の処理・続行か終了かを誤認させない案内を残します。
- 元の例外の詳細はログへ渡しますが、記録の成功は保証しません。
- 表示できない場合、`UiDialogResult.Failed`、表示の例外は、局所的な診断だけにします。別の画面への切替えや再試行はしません。
- 部分成功の後も、必要な選択の整理などは `finally` で行います。診断・表示の失敗で、必要な終了を妨げません。

プレイリストの表へのドロップでは、作業領域の通知セッションが返す結果記録を、唯一の通知境界とします。既存の警告・エラーを優先し、一次失敗が未報告の場合だけ、一般のエラーを一件追加します。情報通知だけでは、失敗を報告済みとしません。`playlistTableDrop` は一次結果を保って診断し、同じ失敗を別のダイアログで重複して表示しません。通知先の失敗も診断に留めます。

### ファイルとレベル

実行ファイルと同じ場所の `log/` に、BOMなしのUTF-8で出力します。ローテーション済みのファイルも同じ文字コードです。既存のログの古い部分は再変換せず、過去の文字コードと追記部分の混在は許容します。

| ファイル | 内容 |
| --- | --- |
| `log/application.log` | 通常診断。 |
| `log/install-performance.log` | 性能ログ。 |

`InstallPerformance*` の出力は性能ログへ分け、通常診断へ重複して出力しません。

既定のレベルは `INFO` です。コマンドラインの `--log-level` は次のように解釈します。

| 指定 | 扱い |
| --- | --- |
| `Info` | 既定と同じ互換の引数。 |
| `Warn`・`Warning`・`Error` | 明示的な抑制の指定。 |
| 空の値、空白を含む不正な値 | 互換の規則により `Warn` とし、元の文字列を保持する。 |

- スイッチ名と値の大文字小文字を区別せず、最後の指定を使います。
- 前後の空白は除去しません。
- 不正な値が、空・空白以外の文字を含む場合だけ警告します。
- INFOを抑制する設定では、INFOの性能ログも出力しません。

コマンドライン引数の捕捉と、共有する窓口への適用は[起動](../runtime/startup.md#起動時の配置と実行条件)に従います。

通常の起動でINFOが出力されるため、配布物に `LaunchWithInfoLog.bat` は含めません。すでに配布済みの同ファイルが渡す `--log-level=Info` は、引き続き受け付けます。

### ローテーションと書込みの性能

| 項目 | 規定 |
| --- | --- |
| ローテーション | 一つのファイルが20 MiBを超えたときに行い、種類ごとに最大5世代を `log/archive/` に保持する。NLogの `FileTarget` の機能を使い、独自の移動・削除処理は持たない。 |
| 通常診断の配送 | 通常診断の出力先の実体は一つとし、NLog標準の非同期の配送で包む。`FileLogger` と名前付きの通常診断は同じ非同期の経路を使う。性能ログの分離・書式・レベル設定は維持する。 |
| 配送の上限 | 待ち行列の上限は10,000件。満杯の場合は標準の破棄により捨て、呼出元をファイル保存の待機で止めない。独自の重要度別の配送・再試行・欠落の監視はしない。 |
| フラッシュ | 通常の起動中は待たない。正常終了、起動失敗、未処理例外による最終終了では、NLog標準のフラッシュを最大2秒だけ試す。起動失敗も `Shutdown` による通常のExitイベントへ接続し、そこで一回だけフラッシュする。 |

保存の失敗は、必要な終了やミューテックスの解放を妨げません。出力先の障害、待ち行列の満杯、強制終了での完全な保存は保証しません。

計測の範囲、対象件数、欠測は[データ規模と性能](performance-and-scale.md#計測する完了範囲)に従います。集約した件数と時間を使い、リソースごとの同期ログや、巨大な集合の文字列化は追加しません。推奨する診断のすべてが、既存の経路に実装済みという意味ではありません。

### 本体表示後の基本環境

起動ごとに一回、本体の `Show` の後に、基本環境をINFOで `application.log` へ記録します。INFOを抑制している場合は、通常のレベル設定に従います。

本体のウィンドウの実際のDPI認識モードとDPIだけをUIスレッドで捕捉し、次を背景で取得します。

- 実行中のアプリのバージョンと、取得できるビルド情報。
- Windowsのバージョンとビルド、.NETとロード済みのWPFのバージョン、プロセスのアーキテクチャ。
- 有効なモニターの一覧。台数、主画面、物理ピクセル単位の矩形・解像度・作業領域、取得できた倍率を含む。

各取得の失敗は `unavailable` として明示し、DPIを96、倍率を100%と補いません。倍率のAPIが失敗時に返す代替の値も利用しません。背景でWPFのオブジェクトを操作せず、診断のTaskの失敗を観測します。

取得・保存は起動完了の条件にせず、終了時に収集の完了を待ちません。定期的な再取得、操作履歴、機器の識別子、端末名・利用者名、Git・ネットワークへの照会は追加しません。

未処理の例外は、元の `Exception` をローカルのロガーへ渡し、内部例外とスタックを既存の例外の書式で記録します。全文をメッセージにも重複して連結せず、自動送信しません。既存の例外型による無記録の抑止は再導入しません。続行・終了の判断と、終了中の限定条件は[終了](../runtime/shutdown.md#sqliteと失敗の観測)に従います。

### 初期化・探索・同期の記録

処理ごとに、開始の条件と終端の結果を対応付けます。既存の操作トークン、世代、要求版を持つ経路ではその識別を使い、ログ専用の永続的な識別や再実行の状態を追加しません。画面の表示行と処理の対象を対応させますが、行の描画や通知の排出を、処理の終端の条件にしません。

| 記録する事実 | 意味 |
| --- | --- |
| 処理と対象 | DB読込み、探索、差分判定、解析、反映、補完など、何を扱う処理か。 |
| 入力条件 | 検索ルート、拡張子、除外の種類と件数、走査方式。省略や代替の走査の理由も区別する。 |
| 処理量 | 探索のヒット数、除外後の確認対象数、確認済み数、解析対象数、読込み行数。各対象と単位を区別する。 |
| 実際の結果 | 変更不要、生成、更新、削除、失敗、互換性による省略など。候補数や生成計画の数を、実際のDBの変更数として使わない。 |
| 終端 | 成功、不要、入力不完全、失敗、取消を区別し、その理由を記録する。 |
| 時間 | 記録対象の開始から終端までの実時間と、待機と処理の内訳。未計測・適用対象外を実測の0で補わない。 |

通常のINFOの探索条件は、譜面・リソース・LR2フォルダ・補助テキストなどで、同じ粒度にします。

- 検索ルートと拡張子、除外条件の出所・種類と件数を記録します。数百のディレクトリなど巨大な除外パスの一覧と、除外を含む問い合わせの全文は省きます。
- 条件の記録は実際の要求から行います。ログのための再検索・DBの照会・別の対象集合の構築はしません。
- LR2のアプリ管理の出力ディレクトリによる探索の除外と、組込みカスタムフォルダの設定による候補の除外は、別の理由として記録します。
- 差分がなくても確認対象は存在するため、確認候補・変更不要・解析対象・実際の反映結果を分けます。親ディレクトリの生成・更新の行数とファイル数も混ぜません。

主要な件数と結果を読める要約を置き、調査に必要な、検索・読取り・解析・保存・キャッシュ・待機の詳細な内訳は、その処理へ接続します。対象ごとの高頻度の同期ログや、同じ結果を段階ごとに巨大な文字列へ複製する記録は増やしません。

起動全体では、操作可能、必須初期化の完了、後続処理の収束を、別の境界として維持します。入れ子や並行処理の累積時間を足して操作の実時間にせず、詳細な区間と全体の範囲を揃えて比較します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| ログの構成、文字コード、ローテーション、元の例外、基本環境、レベルと配送 | [`NLogWrapper`](../../../BeMusicSeeker/Ribbit/Logging/NLogWrapper.cs)、[`ApplicationEnvironmentDiagnostics`](../../../BeMusicSeeker/Diagnostics/ApplicationEnvironmentDiagnostics.cs) | [`NLogWrapperTests`](../../../tests/BeMusicSeeker.Windows.Tests/Runtime/NLogWrapperTests.cs) が、実際のログの日本語のUTF-8、形式、内部例外・スタック、基本環境、名前付きの通常診断と性能ログの分離、レベルの抑制、ローテーションを確認する。出力を止めた状態でも呼出元と上限付きのフラッシュが進み、再開後に保存できることを、実際の配送で確認する。 |
| 基本環境の背景での収集と、起動への非干渉 | [`App`](../../../BeMusicSeeker/App.cs)、[`ApplicationEnvironmentDiagnostics`](../../../BeMusicSeeker/Diagnostics/ApplicationEnvironmentDiagnostics.cs) | [`ApplicationStartupCompositionOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/ApplicationStartupCompositionOwnerTests.cs) / [`ApplicationStartupCompositionOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Startup/ApplicationStartupCompositionOwnerTests.cs) の `ApplicationEnvironmentCollectionGateDoesNotDelayStartupCompletion` が、収集を止めた状態でも起動のTaskが完了し、取得の失敗を欠測として記録することを確認する。`Show` の後に一回だけ接続すること、UIが所有する値の捕捉、終了時に収集のTaskを待たないことは、静的に確認する。 |
| 探索条件と、確認対象・解析対象・保存結果の区別 | [`RootFileEnumerationService`](../../../BeMusicSeeker/Models/Utils/Scanning/RootFileEnumeration.cs)、[`BmsLibraryInitializationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs)、[`Lr2SynchronizationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs) | [`LibraryFileScanPipelineOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Scanning/LibraryFileScanPipelineOwnerTests.cs) / [`LibraryFileScanPipelineOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Scanning/LibraryFileScanPipelineOwnerTests.cs)、[`BmsLibraryInitializationFileScanTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) / [`BmsLibraryInitializationFileScanTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Startup/BmsLibraryInitializationFileScanTests.cs)、[`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) / [`BmsLibraryLr2SongDbSyncTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) が、処理対象と実際の結果を確認する。条件のログが実際の要求から出力されることは静的に確認する。ログ全文を固定する検査は設けない。 |
| Taskの成功値、元の失敗の集合、取消の状態・トークン、診断の失敗 | [`TaskEx`](../../../BeMusicSeeker/Ribbit/Util/Extensions/TaskEx.cs)、[アプリの診断のTask拡張](../../../BeMusicSeeker/Models/Utils/TaskEx.cs) | [`TaskLoggingTests`](../../../tests/BeMusicSeeker.Core.Tests/Runtime/TaskLoggingTests.cs)、[`TaskLoggingTests`（ログ構成）](../../../tests/BeMusicSeeker.Windows.Tests/Runtime/TaskLoggingTests.cs) |
| プレイリストの通知の順序、一次失敗、通知が失敗しても再表示しないこと | [`PlaylistOperationNotificationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistOperationNotificationOwner.cs) | [`PlaylistOperationNotificationOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistOperationNotificationOwnerTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) / [`PlaylistWorkspacePersistenceCommandTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 画面のイベントからの例外の通知 | [`MainWindow.cs`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の `NotifyMainWindowOperationFailureAsync` と `playlistTableDrop` | 共通サービスへの引渡しと、既存の通知境界を実装で確認する。ダイアログの受付・失敗は[ダイアログの対応表](../ui/dialogs.md)を参照する。 |
| Windowsのログ検証の構成の分離と復元 | `ApplicationLoggingTestScope` | `ApplicationLoggingTestScopeTests` が、非並列の検証で独立した構成へ交換し、元の構成とFile・Networkの出力先・ロガーの参照を配送の終端後に復元すること、正常時と本体の例外後の、保持したロガーによる実ファイルへの追記、元の出力先の再初期化に失敗した場合の非成功を確認する。元の規則は直接変更しない。 |

## 関連資料

[例外・操作結果・診断](exception-handling.md)、[データ規模と性能](performance-and-scale.md)、[ダイアログ](../ui/dialogs.md)、[プレイリストの保存と出力](../playlist/storage-and-export.md)。
