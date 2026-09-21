# 譜面ファイルの読取り

## 目的と適用範囲

ファイル差分、導入、再走査、LR2同期、補完解析で、譜面のバイト列を重複して読まないための処理分担と寿命を定めます。解析値の互換性、譜面情報の最新性、パス収束の定義は各専門仕様に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

本資料の「後処理」は軽量解析から保存可能な保守・詳細情報を作る段階です。「補完解析」は既所持データの不足を埋める処理であり、新規ファイルの登録後に必ず行う別処理ではありません。

## 仕様

### 読取りと解析の入口

バイト列とハッシュを必要とする処理は `ChartFileSnapshot` を使います。これは `Path`、`Bytes`、`Length`、`LastWriteTimeUtc`、`Md5`、`Sha256` を持つ一回の読取り結果であり、表示・操作用の `ChartFile` やDBの保存主体とは異なります。

| 段階 | 入口 | 責務 |
| --- | --- | --- |
| 読取り | `ChartFileContentReader.ReadBuffer(path)` | バイト列とファイル情報だけを読む |
| ハッシュ生成 | `ChartFileContentReader.CreateSnapshot(buffer)` | 同じバイト列からMD5・SHA-256を計算する |
| 小規模な互換入口 | `ReadSnapshot(path)` | 上記の二段階を続けて行う |
| BMSの軽量解析 | `BMSFile.CreateBMSFileFromSnapshot` | 一覧用の基本情報とリソース参照を生成する |
| BMSONの軽量解析 | `BmsonSongParser.ParseSnapshot` | 保存行の基本情報とリソース参照を生成する |
| 詳細解析 | `ChartInfoParser.ParseBytesDetailed` | 譜面情報、診断、時間超過、解析失敗を生成する |
| 文字コードによる再読解 | `ReloadBMSMetadataWithEncodingDetection` | 同じバイト列から表示用の基本情報を読み直す |

パスだけを持つ既存処理と保留の生成では、パスを受ける互換APIを使えます。新しい大量処理で同じ譜面をハッシュ・軽量解析・詳細解析のために別々に読みません。長パスI/Oは専用の境界を通し、保存するパスに拡張長プレフィックスを含めません。

バイト列は処理中だけ保持し、DBや長寿命のモデルに保存しません。寿命内での再利用は積極的に行いますが、軽量解析と詳細解析の責務を一つに混ぜません。

### ファイル差分の処理順序

初期化、全体の再初期化、軽量差分更新で追加・更新したファイルは、読取り、ハッシュ生成、軽量解析、保守情報・譜面情報の準備、DB確定、メモリ公開の順に扱います。

読取りは `ChartFileReadPipelinePolicy` に従い、対象が複数でCPUが十分なら最大2並列とします。件数上限のある待ち行列で入力を制御し、ハッシュ計算は解析側で行います。個別の回復可能なI/O失敗は `FileScanFailures` と診断へ集約し、譜面ごとに初期化ダイアログを出しません。詳細解析の失敗だけでは、軽量解析で得た保存行の登録を止めません。

軽量解析と後処理は別の並列段階です。後処理は一譜面分の変更不能な結果と保存用データを返し、共有の結果、現在のモデル、確定用状態を直接変更しません。一つの集約処理が入力順に件数、移動ハッシュ、モデル更新、譜面情報の公開、DBへの投入をまとめます。

既定の解析並列数はCPU数の半分程度、後処理はその約1.5倍かつCPU数以下を目安にします。明示した上書き値は尊重します。`inline_maintenance_degree` は一譜面内の並列数であり、後処理の並列数とは区別します。

`InlineChartInfoBatchSize` の既定2048件は情報検索・生成の内部単位であり、後処理の待合せやDB確定の単位ではありません。DBの既定 `DbCommitChunkSize` は10000件です。二件以上の差分では、開始時に現在版の情報を読み取り専用で捕捉し、同じ差分処理の先行確定を偶然観測しないようにします。スキーマが現在でない場合は空の捕捉結果を渡し、並列処理側で代わりのDB検索を始めません。

### DBへの集約と進捗

後処理、保存単位の集約、単一のDB書込み処理を、それぞれ上限付きの待ち行列で接続します。DBが遅い場合は入力を抑制しますが、一回の確定中も集約処理は別の入力を扱えます。DB接続は確定単位ごとに開いて閉じます。

| 進捗 | 数えているもの |
| --- | --- |
| 読取り | バイト列とファイル情報を読んだ件数 |
| 解析 | ハッシュと解析・評価を終えた件数 |
| 画面のファイル差分進捗 | 後処理がDBへ渡せる一譜面分のデータを作った件数。DB確定済み件数ではない |
| DB書込み | 保存先への確定を終えた件数 |

BMS追加とBMSON追加・更新を同じ進捗の対象に含めます。`song`、`bmson_song`、ハッシュ対応、情報・失敗、作成できた保守行を同じ確定単位へ渡し、成功後だけ保存主体、索引、警告を公開します。

完全に現在の既存情報を、ファイル差分の成果物へ全件保持しません。`file_diff_inline` の索引差分は新規・更新した情報だけであり、全体の情報索引は `chart_info_hydration` が更新します。ハッシュ不足などの修正対象が既存情報を再利用する場合は、その対象に必要な保存用適用結果を残します。

### パス差分と利用者の保存値

軽量な `ReloadFileDiff` は、メモリ上のBMS・BMSONと走査結果を比較します。DBの再読込み、メタデータ取込み、全体の情報読込み・補完、遅延保守を同時に行いません。外部のDB変更や互換修復には `FullReinitialize` を使います。

旧パスの削除は完全一致の一時表を使う集合SQLで行います。BMSの旧MD5を退避し、譜面・保守行を消した後、残存主体がないハッシュ対応だけを削除します。BMSONと保守行も同じ確定単位で扱います。メモリ側の削除判定はハッシュ集合を使い、全譜面と全削除パスの総当たりに戻しません。

保守行の一時表は `path TEXT PRIMARY KEY` を使い、同じ完全一致のパスだけを最後の値へまとめます。大文字小文字だけが異なるパスは別行です。

移動に伴う利用者の列の引継ぎは、差分開始時の旧パスから捕捉し、同じMD5の旧新候補が差分全体で一対一の場合だけ行います。並列処理の完了順や保存単位で一意性を判定しません。列、既存の導入先保護、曖昧な候補の扱いは[パスの識別と収束](../core/path-identity.md)に従います。保守情報は引き継がず現在の入力から評価します。

### リソース参照と文字コード

軽量解析で得る `WAVfiles`、`BGAfiles` とBMSONのリソース参照は、同じ処理中に保守行へ畳み込みます。拡張子を除いた譜面相対のキーを音声・画像・動画別の索引へ照合し、行を作った後に参照集合と派生キャッシュを破棄します。DB由来で参照がない既存譜面だけが、後続保守でパスから読み直せます。

リソース索引にディレクトリがある場合は、その集合へ直接照合します。索引がない場合だけ共有の存在確認キャッシュを補助的に使います。同じディレクトリ・同じ要求集合で共有できるのは存在件数です。パス、ハッシュ、文字コード、定義数、LR2互換性、警告の無視状態、保守行全体を共有しません。画像でもstagefile・backbmp・bannerの役割は区別します。

BMSの基本情報とリソース参照はまずCP932系の既定で読みます。同じバイト列からASCII、Shift_JIS、KS_C_5601、UTF-8、不明を判定し、非Shift_JISが確定した場合だけ、`title`、`subtitle`、`artist`、`subartist`、`genre` を元の個別値として読み直します。不明や推定末尾の `?` では補正しません。タイトルと副題、アーティストと副アーティストを合成しません。

リソース参照と画像パスは、LR2の本文解釈に合わせ、BOM自動判定をしないCP932の値を維持します。`maintenance.encoding` は表示・保守用であり、詳細解析の文字コードを変更しません。

### 導入と手動再走査

保留中に読んだバイト列を導入まで長期保持しません。導入は実際の配置先を読み直して詳細情報を生成し、影響する譜面の保守を操作単位で行います。導入前配置に対する一時的な `ResourceHealth` 警告を解除し、導入後は通常ライブラリと同じ保守情報から表示します。保存・必須反映・公開は[変更セッション](mutations.md)に従い、パッケージごとの確定・通知に分割しません。

手動の再スキャンと全譜面再スキャンは、リソース、文字コード、BMSONの参照を再評価する重い操作です。上限付きの読取り、並列評価、単一DB書込みを使い、変更した保守行だけを保存します。既存行と同じ場合は書きません。この操作では `chart_info` を作らず、不足や版違いは情報の読込み・補完に任せます。

### LR2同期と全件補完

LR2の `song_rows` も同じスナップショットと `Lr2SongRowEnricher.CreateParsedSongRowFromSnapshot` を使います。開始時に情報と時間上限を考慮した失敗集合を捕捉し、追加の読取り・DB検索なしで `EvaluateSnapshot` へ渡します。回復可能な行生成失敗で既存行を保つ経路は、その失敗を保持したまま使います。LR2互換性の反映は保守行のLR2列だけを更新し、リソース・文字コードの結果を置換しません。

直前のファイル差分が、同じ入力・走査世代の全BMSパスを永続化し、保守・情報生成の対象を満たし、移動ハッシュの曖昧さがない場合は、自動同期の `song_rows` を省略できます。全列の再比較は差異の診断であり、必須の受付条件にしません。手動強制同期、署名不一致、対象不足では通常どおり読みます。省略の根拠は永続化しません。

全件補完は既存DBの不足・旧版を補う処理です。完全に現在の対象は読取り前に除き、ハッシュなどの修正が必要な対象は読取り後に既存情報を再利用できます。同じMD5は一回評価して全BMS主体へ適用し、既存行の9列だけを更新します。BMSONからLR2の行を作りません。詳細は[譜面情報の保存](chart-info.md)に従います。

### 寿命と診断

解析後のバイト列、保守行に畳み込んだリソース参照、確定後の保存用データは速やかに解放します。保存件数の上限を、バイト列の保持上限の代わりにしません。初期化で明示的なGCや大規模オブジェクト領域の圧縮を行いません。

| 診断 | 意味 |
| --- | --- |
| `song_tbl_file_check_breakdown` | 各段階の件数、時間、待ち行列の待機、情報・保守・文字コードの内訳 |
| `inline_chart_info_index_published_count` | ファイル差分から索引へ渡した新規・更新情報の行数。再利用で省略した全行数ではない |
| `inline_maintenance_*` | 保守行の生成と譜面ごとの索引・存在確認の利用数。並列の他譜面の件数を混ぜない |
| `inline_encoding_*` | 文字コード判定と、元の基本情報を読み直した件数・時間 |
| `commit_queue_wait_ms` / `commit_writer_queue_wait_ms` | 後処理から集約、集約からDB書込みへの待機を分けた値 |
| `chart_info_inline_install` | 実配置先から生成した情報の導入時集計 |
| `chart_info_backfill start/done` | 補完解析の読取り数・量・並列数・解析時間 |
| `chart_info_backfill parse_failed` | 共通解析処理の失敗。名称だけで補完経路と断定せず、周辺の集計と合わせる |

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 読取りの分担・上限・ハッシュ生成 | [`ChartFileContentReader`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartFileContentReader.cs)、[`ChartFileReadPipelinePolicy`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartFileReadPipelinePolicy.cs) | [`ChartFileReadPipelinePolicyTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartFileReadPipelinePolicyTests.cs) |
| 差分の順序、保存、進捗、個別失敗、保守と情報の同時生成 | [`BmsLibraryInitializationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs) | [`BmsLibraryInitializationFileScanTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationFileScanTests.cs)、[`BmsLibraryInitializationInlineChartInfoTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationInlineChartInfoTests.cs) |
| 詳細情報の再利用、公開順序、補完時の限定更新 | [`ChartInfoBuildService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoBuildService.cs)、[`ChartInfoInlineBuildService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoInlineBuildService.cs) | [`ChartInfoInlineHydrationTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs)、[`ChartInfoBackfillStorageTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoBackfillStorageTests.cs) |
| 導入先からの解析と失敗再利用 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`ChartInfoInstallFailureRetryTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInstallFailureRetryTests.cs)、[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) |
| LR2行の生成と差分直後の同期 | [`Lr2SongRowEnricher`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongRowEnricher.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) |

## 関連資料

[解析互換性](parser-compatibility.md)、[データと索引](../core/data-and-indexes.md)、[LR2楽曲DB](../integration/lr2-song-db.md)、[規模と性能](../core/performance-and-scale.md)を参照します。
