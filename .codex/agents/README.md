# エージェントの役割設定

呼出し条件・共通契約・順序は[開発作業の仕様](../../devdocs/spec/development/agent-workflow.md)、各担当のモデル・推論強度・権限は各TOML、同時担当数と待機は[全体設定](../config.toml)を正本とします。

| 設定 | 用途 |
| --- | --- |
| [design-advisor.toml](design-advisor.toml) | 必要な設計判断への助言。 |
| [test-contract-designer.toml](test-contract-designer.toml) | 実装前の独立テスト設計。 |
| [plan-clarifier.toml](plan-clarifier.toml) | 実装直前の引継ぎ点検。 |
| [implementation-worker.toml](implementation-worker.toml) | 確定した範囲の実装。 |
| [issue-resolver.toml](issue-resolver.toml) | 通常の技術的障害の解決。 |
| [critical-issue-resolver.toml](critical-issue-resolver.toml) | 重大な技術的障害の解決。 |
| [repo-static-review.toml](repo-static-review.toml) | 通常変更の独立レビュー。 |
| [critical-static-review.toml](critical-static-review.toml) | 重大変更の独立レビュー。 |

通常用と重大用は代替の入口であり、両方を毎回呼びません。TOMLには役割固有の入口と禁止事項を書き、共通手順を複製しません。
