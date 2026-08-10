# Library Mutation Boundary

本資料は、譜面行、保留パッケージ、導入済みパッケージに対する破壊的操作の UI / model 境界を定義する。長パス I/O の正本は [path-length-and-io.md](path-length-and-io.md) だが、ユーザー操作としての実行順、二重実行防止、dialog 表示タイミングは本資料を正本にする。

## 基本方針

- 譜面 / パッケージの変更系操作は `MainWindowViewModel.RunChartPackageMutation(...)` を入口にする。
- 境界の内側では、`lockCopyFile`、再生停止、UI refresh suppression、操作中フラグ、operation dialog scope、操作後 refresh flush を一元管理する。
- mutation 中は譜面行 / 保留行 / package 行の context menu open と command 起動を拒否する。context menu の enable 判定で UI thread から file existence check を走らせない。
- `BMSLibrary` の writer lock 中に `Dispatcher.Invoke`、message box、UI event callback、`Task.Wait` / `.Result` のような同期待ちは行わない。
- 失敗を隠す fallback は追加しない。必要な確認が取れない場合は処理を進めず、境界違反は明示的な失敗にする。

## ViewModel 境界

`RunChartPackageMutation(...)` は、次の責務だけを持つ。

- 同時実行中かどうかを `IsChartPackageMutationInProgress` で公開する。
- 操作中は LR2 song DB 同期や譜面 / package 操作の再入をブロックできる状態にする。
- 必要なら対象譜面の再生を停止する。
- `BeginUiUpdateSuppression(...)` / `EndUiUpdateSuppression(...)` で refresh をまとめ、操作後に pending install tree、library main view、folder tree、duplicate tree などの必要 channel を flush する。
- `BMSLibrary.BeginOperationDialogScope()` を開始し、model lock を抜けた後で蓄積された warning / error dialog を一度だけ表示する。

境界は、確認 dialog の意味を決めない。ユーザー確認が必要な操作は、mutation 実行前の preflight で ViewModel が dialog を表示し、結果を explicit decision として `BMSLibrary` へ渡す。

## Model 契約

`BMSLibrary` は、破壊的操作の結果として表示すべき OK dialog を operation report として蓄積できる。operation dialog scope が有効な場合、OK dialog は即時表示せず、scope の flush で表示する。

`YesNo` / `OKCancel` などの interactive prompt は、operation dialog scope 内では表示しない。必要な decision が渡されていない場合、`BMSLibrary` は暗黙の yes / no に倒さず、境界違反として明示失敗にする。これにより、model writer lock 中に UI thread を待つ経路を残さない。

scope 外から `BMSLibrary` を直接呼ぶ既存テストや内部ユーティリティでは、従来どおり dialog service を使う。ただし、アプリ本体の譜面 / パッケージ操作入口は ViewModel 境界を通す。

## Chart-info Catalog Write

background hydration/backfillやpackage inlineのchart-info writeは、UIの`RunChartPackageMutation(...)`とは別のcatalog mutationである。transaction ownerは`CatalogMutationOwner`のままとし、inline/fullは同じ`ApplyChartInfoStorageWrite(CatalogChartInfoStorageWriteRequest)`を使う。

- requestはinline用BMS/BMSON persistence copy、full-backfill用immutable narrow projection、`CatalogChartInfoWriteRequest`をsnapshotとして束ねる。
- inline storage rowsまたはfull-backfill用song update、`chart_digest_map`、`chart_info`、parse-failure upsert/deleteは一つのcatalog transactionで保存する。
- full backfillは`Lr2SongDbWriter.UpdateChartInfoSongProjections(...)`でpath+MD5が一致する既存BMS `song` rowのchart-info由来9列だけをupdateする。full row upsertやmissing row insertは行わず、基本列、`mode`、`judge`、user列をUPDATE句へ含めない。BMSONからLR2 `song` rowは作らない。
- durable receipt後だけcanonical storage owner、digest/index、chart-info session index、warning/digest eventを更新する。commit失敗時はcanonical/index/eventを変更せず、対象は次回もcandidateとして残る。
- parse-failure明示削除などstorage rowを伴わない処理にはfacts-onlyの`ApplyChartInfoWrite(...)`を残す。
- DB transactionやmodel/storage lockを保持したままUI/event subscriberを待たない。

## 対象操作

P0 として次の操作は共通境界を通す。

- 譜面フォルダの移動、削除、マージ、フォルダ名変更、自動リネーム、拡張子修正。
- 導入済みパッケージ record 削除。
- 保留パッケージ / 保留譜面の導入先検索、マージ先検索、導入先設定、導入先解除。
- 保留パッケージ / 保留譜面の通常導入、手動導入、削除、source 削除、スマート上書き。
- 導入先修正で既存 duplicate record を消す可能性がある操作。

これらはファイル操作の意味上も P0 長パス対応範囲であり、実際の存在確認、コピー、移動、削除、タイムスタンプ更新は `LongPathFileSystem` / `IFileMutationService` を使う。

## Dialog と Report

操作前に確認が必要な例は次のとおりである。

- 通常導入済み扱いの譜面を force install で上書きするか。
- 導入先修正で、同じ譜面を指す既存 record を削除するか。
- 譜面削除で、譜面ファイルだけでなく package folder 全体を削除するか。

これらは preflight で確認し、承認済み path や boolean decision として execution option へ渡す。decision が不足した状態で model lock 内 prompt に戻る実装は不可とする。

操作後の warning / error は report として蓄積し、`lockCopyFile` と model lock を抜けた後に dialog service で表示する。重複 warning を避けるため、同じ mutation の中で同じ message を複数 queue しない。

## 関連仕様

- [architecture.md](architecture.md): UI / model concurrency boundary。
- [path-length-and-io.md](path-length-and-io.md): 長パス対応 I/O、`LongPathFileSystem`、`IFileMutationService`。
- [warning-model.md](warning-model.md): warning の表示仕様。
