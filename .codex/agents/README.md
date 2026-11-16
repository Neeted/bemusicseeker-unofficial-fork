# エージェントの役割設定

呼出し条件・共通契約・順序は[開発作業の仕様](../../devdocs/spec/development/agent-workflow.md)、各担当のモデル・推論強度・権限は各TOML、同時に開ける子担当スレッドの容量と待機は[全体設定](../config.toml)を正本とします。担当数と作業ワークツリーの容量上限を区別し、ワークツリーの運用は[並列作業](../../devdocs/spec/development/agent-workflow.md#並列作業)、重い検証の調整は[検証仕様](../../devdocs/spec/development/testing.md#複数ワークツリーの検証)に従います。

全体ルート・単位責任者・担当の分担は[用語集](../../devdocs/spec/glossary.md#開発作業の分担)と[役割と順序](../../devdocs/spec/development/agent-workflow.md#役割と順序)に従います。単位責任者には既存の `default` 担当を使えます。

| 設定 | 用途 |
| --- | --- |
| [design-advisor.toml](design-advisor.toml) | 必要な設計判断への助言。 |
| [test-contract-designer.toml](test-contract-designer.toml) | 実装前の独立テスト設計。 |
| [plan-clarifier.toml](plan-clarifier.toml) | 実装直前の引継ぎ点検。 |
| [implementation-worker.toml](implementation-worker.toml) | 確定した範囲の実装・差し戻し修正。 |
| [issue-resolver.toml](issue-resolver.toml) | 再計画後の実装単位の修正・再実装。 |
| [preflight-static-review.toml](preflight-static-review.toml) | 呼出しごとに新しい担当で行う凍結範囲の予備レビュー。 |
| [repo-static-review.toml](repo-static-review.toml) | 通常変更の独立レビュー。 |
| [critical-static-review.toml](critical-static-review.toml) | 重大変更の独立レビュー。 |

予備レビューは呼出しごとに新しい `preflight-static-review` を新規コンテキストで使います。最終レビューの通常用と重大用は代替の入口であり、両方を毎回呼びません。レビューの開始条件、反復回数、再計画後の `issue-resolver` への切替は開発作業の仕様に従います。`developer_instructions` には役割固有の補足だけを書き、共通契約・順序・検証規則を複製しません。
