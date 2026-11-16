---
paths:
  - "docs/**/*.md"
  - "release notes/**"
  - "README.md"
  - "README.ja.md"
---

# 利用者向け文書

`docs/`、`release notes/` の文書と、リポジトリルートの `README.md`・`README.ja.md` は利用者向け文書です。これらを作成・編集する前に `user-docs` スキルを呼び、注入された[編集方針](../../devdocs/README.md#利用者向け文書の編集方針)に従います。この会話で呼出し済みでも、会話の圧縮後は呼び直します。他のディレクトリの `README.md` と、読むだけの場合は対象外です。

生成HTML（`docs/*.html`）は直接編集せず、編集方針の「Markdownと生成HTML」に従って再生成します。
