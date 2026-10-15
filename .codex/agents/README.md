# エージェントの役割設定

呼出し条件・共通契約・順序は[開発作業の仕様](../../devdocs/spec/development/agent-workflow.md)、各担当のモデル・推論強度・権限は各TOML、同時担当数と待機は[全体設定](../config.toml)を正本とします。

| 設定 | 用途 |
| --- | --- |
| [design-advisor.toml](design-advisor.toml) | 必要な設計判断への助言。 |
| [test-contract-designer.toml](test-contract-designer.toml) | 実装前の独立テスト設計。 |
| [plan-clarifier.toml](plan-clarifier.toml) | 実装直前の引継ぎ点検。 |
| [implementation-worker.toml](implementation-worker.toml) | 確定した範囲の実装・差し戻し修正。 |
| [issue-resolver.toml](issue-resolver.toml) | 通常修正を試した担当が報告した技術的な行き詰まりの解決。 |
| [preflight-static-review.toml](preflight-static-review.toml) | 新規担当の全体予備レビューと、その指摘への局所修正の再確認。 |
| [repo-static-review.toml](repo-static-review.toml) | 通常変更の独立レビュー。 |
| [critical-static-review.toml](critical-static-review.toml) | 重大変更の独立レビュー。 |

全体予備レビューは毎回別の新しい `preflight-static-review` を新規コンテキストで使います。その指摘への局所修正は同じ担当が再確認できますが、解消後は新しい担当による全体予備レビューへ戻し、その全体確認で指摘がない場合だけ最終レビューへ進みます。最終レビューの契約を保つ局所修正は、同じ最終レビュー担当で指摘が解消するまで確認できます。再計画・領域単位の凍結と反復回数の扱いは、開発作業の仕様に従います。最終レビューの通常用と重大用は代替の入口であり、両方を毎回呼びません。`developer_instructions` には役割固有の補足だけを書き、共通契約・順序・検証規則を複製しません。
