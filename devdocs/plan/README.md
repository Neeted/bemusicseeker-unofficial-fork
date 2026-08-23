# Plans And History

このディレクトリは実装計画・調査記録・移行履歴の置き場である。製品の現在仕様の正本は`../spec/`を参照する。

`BeMusicSeeker_refactoring_plans/`は完了済みのMVVM / .NET 10 / performance engineeringについて、現在の維持方針と最終状態だけを持つ。個別unitやcommit履歴はGit historyへ委ねる。

現在進行中の計画:

- `test-suite-blocking-findings-remediation-plan.md`: テスト整理後レビューで残ったrunner stream lifecycleとWPF dispatcher cleanupのBlocking findings修正計画。

長期参照する5文書:

- `00_Codex共通実行ルール.md`: 再開時に守る共通ルール。
- `BeMusicSeeker_性能回帰改善計画.md`: 完了後の性能・起動境界invariant。
- `PERFORMANCE_WORK_REGISTER.md`: current-only risk / protected completion。
- `PLAN_STATUS.md`: 最終完了判定と選択distribution。
- `POST_MIGRATION_MANUAL_ACCEPTANCE.md`: Engineering外のmanual / release prerequisite。

その他の`plan/`資料は各機能の設計履歴であり、現行仕様と食い違う場合は`../spec/`を優先する。
