# Claude Code の設定

Claude Code 向けの指示・スキル・ルール・フック・担当定義の構成を示します。開発手順と制約の正本は [AGENTS.md](../AGENTS.md) と[開発資料](../devdocs/README.md)で、ここにある設定は、必要な場面で正本の該当節をコンテキストへ読み込ませるための入口です。契約を設定側へ複製しません。Codex 向けの設定は [.codex/agents/README.md](../.codex/agents/README.md) を参照します。

## 構成

| 設定 | 読み込まれる時機 | 用途 |
| --- | --- | --- |
| [CLAUDE.md](../CLAUDE.md) | 会話の開始時と圧縮後。担当にも渡る。 | `AGENTS.md` の取込みと、工程の入口、担当の呼出しと待ち方、圧縮後の確認。 |
| [tests/CLAUDE.md](../tests/CLAUDE.md)、[scripts/CLAUDE.md](../scripts/CLAUDE.md) | そのディレクトリのファイルを読み書きした時。 | 同じディレクトリの `AGENTS.md` の取込み。 |
| [rules/](rules/) | `paths` に一致するファイルを読み書きした時。 | 対象ファイルに応じたスキルの呼出しと、揃えて変更する対象の案内。 |
| [skills/](skills/) | 説明文は常時。本文は呼出し時と、圧縮後の再添付時。 | 工程ごとに、正本の該当節を取り出して注入する。 |
| [agents/](agents/) | 担当の起動時。 | 役割ごとのモデル・推論強度・権限と、事前に読み込むスキル。 |
| [hooks/](hooks/) と [settings.json](settings.json) の `hooks` | 圧縮の直前、コミットと `dev` への統合の直前。 | 要約へ残す工程の状態の指示と、完了判定の確認事項の提示。実行は止めない。 |
| [scripts/print-sections.ps1](scripts/print-sections.ps1) | スキルの呼出し時、または手動。 | 正本のMarkdownから指定した見出しの節を取り出す。 |

`CLAUDE.md` がある場合、Claude Code は `AGENTS.md` を自動では読みません。各 `CLAUDE.md` の先頭の `@AGENTS.md` を削除しないでください。

## スキル

| スキル | 使う場面 | 注入する正本の節 |
| --- | --- | --- |
| `unit-workflow` | 作業ツリーを変更する依頼に着手する。 | [エージェント運用](../devdocs/spec/development/agent-workflow.md)の工程の適用範囲、役割と順序、担当の共通契約、要件と到達可能性、重大変更の判定、設計相談、テスト設計の要否、実装担当への入力、引継ぎの点検。 |
| `review-cycle` | 凍結・レビューへ進む。指摘や検証失敗から修正へ戻る。 | 同じ仕様のレビューの進行、予備・最終レビュー、不備の差し戻し、反復回数と再計画、完了判定。 |
| `verify-scope` | 検証の範囲を選ぶ。失敗・時間超過・不安定な結果を扱う。 | [検証](../devdocs/spec/development/testing.md)の検証方針、実行範囲の選択、実行期限と失敗分類。[テスト設計](../devdocs/spec/development/test-authoring.md)の非同期処理と不安定なテスト。 |
| `parallel-units` | 複数の実装単位を並列に進める。`dev` へ統合する。 | 並列作業、担当の完了待ち、複数ワークツリーの検証、通常の作業ブランチの統合。 |
| `user-docs` | 利用者向け文書を書く。 | [開発資料の案内](../devdocs/README.md)の利用者向け文書の編集方針、文書変更の確認。 |
| `dev-docs` | `devdocs/` の文書を書く。 | 同じ案内の現行情報の維持、書き方、文書変更の確認。[仕様の入口](../devdocs/spec/README.md)の仕様書の書式。 |
| `agent-contract`、`implementation-contract`、`review-contract` | 担当定義の `skills` による事前読込み。 | 担当の共通契約と、役割ごとの節。 |

圧縮後に再添付されるスキルの本文には上限があります。一つのスキルへ注入する節は、役割ごとに必要な範囲へ絞ります。

## 保守

- 正本の見出しを変更・移動したときは、`skills/`・`agents/`・`rules/`・`hooks/` と `CLAUDE.md` がその見出しを参照していないか検索し、同じ変更で更新します。見つからない見出しは、スキルの本文に「節が見つかりません」と出力されます。
- 手順や制約を追加・変更するときは、正本の仕様を編集します。スキル・ルール・フックへは、読み込む節の指定と、場面の案内だけを書きます。
- 担当定義のモデル・推論強度・権限・参照する節は、[役割と順序](../devdocs/spec/development/agent-workflow.md#役割と順序)と一致させます。
- 設定を変更したら、JSON・YAMLの構文、参照する見出しの存在、UTF-8・LF を確認します。スキルの注入は、スキルを一度呼び出して本文が展開されることで確認できます。読み込まれた指示ファイルは `/context` で確認できます。
