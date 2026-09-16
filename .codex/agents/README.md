# エージェントの役割設定

役割・順序・権限の運用は[開発作業の仕様](../../devdocs/spec/development/agent-workflow.md)を正本とします。各TOMLは、その役割のモデル、推論強度、実行権限、読み始める資料を指定します。

| 設定 | 役割 |
| --- | --- |
| [plan-clarifier.toml](plan-clarifier.toml) | 着手前の計画点検。 |
| [test-contract-designer.toml](test-contract-designer.toml) | 必要な独立テスト設計。 |
| [implementation-worker.toml](implementation-worker.toml) | 確定した範囲の実装。 |
| [issue-resolver.toml](issue-resolver.toml) | 実装中の重大な障害の解決。 |
| [repo-static-review.toml](repo-static-review.toml) | 凍結した変更の読取り専用レビュー。 |

全体の上限・待機方針は[config.toml](../config.toml)を参照します。説明文は日本語で書き、共通規則を各ファイルへ複製せず、役割固有の入力と禁止事項を示します。機械設定と意味を持つ応答識別子は原表記を保ちます。
