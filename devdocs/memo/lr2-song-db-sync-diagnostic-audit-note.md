# LR2 song.db 同期診断の監査メモ（superseded）

このファイルは、旧 startup scan diagnostic / repair route を調査した履歴メモです。記載された
`Lr2StartupScanDiagnosticResult`、date sentinel、resume/verifier、cleanup/retry の提案は現行契約ではありません。

現行の durable contract は [lr2-song-db-generation.md](../spec/lr2-song-db-generation.md)、採用理由と
退役 route は [lr2-song-db-one-shot-reconciliation.md](../decisions/lr2-song-db-one-shot-reconciliation.md)、
実行時の検証記録は [lr2-song-db-one-shot-reconciliation-plan.md](../plan/lr2-song-db-one-shot-reconciliation-plan.md)
を参照してください。

このメモは historical pointer としてのみ保持し、新しい実装・テスト・運用判断の根拠には使用しません。
