# ブランチ運用・バージョン更新・リリース

## 目的と適用範囲

公開リポジトリ `Neeted/bemusicseeker-unofficial-fork` のブランチ運用、バージョン更新、配布物の生成・公開を定めます。バージョン更新、タグ作成、送信、Release公開は、作業指示で許可された範囲で行います。

## 用語

[共通用語集](../glossary.md)を参照します。「配布版」はスクリプトが生成する実行物一式、「公開メタデータ」は `update.json`、「リリース資産」は GitHub Release に添付するZIPです。

## 仕様

### ブランチと履歴

| 対象 | 契約 |
| --- | --- |
| `dev` | 次回正式版の開発の起点・統合先。通常の作業ブランチとPRはここを対象にする。 |
| `main` | 正式公開済みのコードと配信データ。アプリの変更は正式リリース時に反映する。 |
| 作業ブランチ | `dev` から作り、検証後に `dev` へ統合する。回帰調査に意味のあるコミット単位を残す。 |
| 緊急修正 | 公開 `main` から分岐し、必要な修正だけを検証して正式版を公開する。その後 `main` を `dev` へマージする。 |
| TSV・文書の保守 | アプリの版を上げずに `main` へ反映できる。同じ変更を `dev` にもマージする。更新情報を未公開版へ変更しない。 |

`dev` と `main` は親子関係を保つマージで統合し、squashやrebaseは使いません。正式版の境界はマージコミットとタグで示し、公開後のタグ・Release・添付資産を保持します。

GitHubの既定ブランチと通常のPR先は `dev` です。閲覧・clone・外部参加の入口を開発の起点に揃え、正式版の配信は引き続き `main` を使います。保護設定で `main` と `dev` の削除・強制更新、リリースタグの削除・付け替えを禁止し、マージコミットと保守者による `dev` への直接送信・リリース時の `main` への直接送信を許可します。

保守者自身の変更は、Issue対応を含め自己PRを作らず、作業ブランチでの検証後にローカルで `dev` へ統合して送信します。外部変更はPRで受け付けます。参加・受入・Issueの完了に関する方針は [CONTRIBUTING.md](../../../CONTRIBUTING.md)を参照してください。

### 維持する配信資源

| 資源 | 契約 |
| --- | --- |
| `main/bms-md5-url-map.tsv` | URL補完用のデータ。ルート直下の配置と配信URLを維持する。 |
| `main/version.txt` | v2.1.0.0より前のアプリへの更新通知用に `2.1.6.0` を固定配信する。バージョン更新・リリース時の生成や照合の対象には含めない。 |
| `main/update.json` | 正式公開されたZIPの版・URL・サイズ・SHA-256を配信する。実装の契約は[ポータブル更新](../integration/portable-update.md)に従う。 |
| `main/docs/` | GitHub Pagesの配信元。Markdownと画像に加え、生成HTMLと `.nojekyll` を管理する。生成HTMLは直接編集しない。 |

文書だけの更新で公開サイトを変更する場合は、次のコマンドでHTMLを再生成してから反映します。

```powershell
uv run scripts/build-doc-html.py --source-root . --output-root docs --site --site-url https://neeted.github.io/bemusicseeker-unofficial-fork
```

### バージョン更新

[Properties/AssemblyInfo.cs](../../../Properties/AssemblyInfo.cs) の `AssemblyInformationalVersion` をパッケージ名、正式タグ、公開メタデータの版の正本とします。`AssemblyVersion` は互換性上の理由または明示指示がある場合だけ変更します。

バージョン更新は通常 `dev` を起点に行い、次を同じ変更で揃えます。

- `AssemblyInformationalVersion`。
- [ReleaseNotesWindow.xaml](../../../BeMusicSeeker/Views/Dialogs/ReleaseNotesWindow.xaml) の `Update_history`。
- `release notes/vX.X.X.X リリースノート.md`。GitHub Release本文として使える内容にする。

タグ作成・公開は、下記のリリース手順で行います。画面内の更新履歴は日本語で直接記述できます。その他の表示文言は[表示リソースの規則](../../../AGENTS.md#ログと表示文言)に従います。

### 配布物の生成と検証

[publish.ps1](../../../scripts/publish.ps1) はスクリプトの所在するリポジトリからビルドし、既定では `dist/` にZIPを生成します。生成対象は配布物だけです。

```powershell
pwsh -NoProfile -File scripts/publish.ps1
# メタデータ同梱版も生成する場合
pwsh -NoProfile -File scripts/publish.ps1 -IncludeMetadata
```

配布・更新・リリースに関わる変更は[検証仕様](testing.md)の `Full` を実行します。Fullは検証用の `SkipDocHtml` 配布物を隔離された出力先に生成・共有します。正式配布用のZIPは、検証済みのソースからHTMLを含めて生成し、ソースを変更した場合は再生成します。

### 正式リリースの準備

作業ツリーをクリーンにし、最新の公開 `main` に対象の `dev` をローカルでマージします。各コマンドの成功を確認してから次へ進みます。

```powershell
git fetch origin --tags
git switch dev
git pull --ff-only
# ここで対象のdevのコミットを確定する
git switch main
git pull --ff-only
git merge --no-ff dev
pwsh -NoProfile -File scripts/verify-refactor.ps1 -Mode Full
pwsh -NoProfile -File scripts/publish.ps1
pwsh -NoProfile -File scripts/release.ps1 -CreateDraft
```

公開 `main` への反映はRelease公開後に行います。準備中のHEADは公開 `main` を祖先に持つことが必要です。緊急修正では、上記の `dev` の代わりに `main` から分岐した修正ブランチをマージします。

### 下書きと公開

[release.ps1](../../../scripts/release.ps1) は `dist/` と版別リリースノートを使います。正式版の準備・公開は、作業ツリーがクリーンな `main` 上で実行します。

| 操作 | 処理と照合 |
| --- | --- |
| `-CreateDraft` | 通常版ZIPを必須として公開メタデータとPages HTMLを生成し、生成物だけをコミットする。HEADへ正式タグを付け、タグだけを送信して下書きを作成・照合する。 |
| `-PublishDraft` | HEADとローカル・リモートタグ、公開メタデータとローカルZIPの版・名前・サイズ・SHA-256、リモート資産の名前・サイズ・集合を照合する。公開mainがHEADの祖先であることを確認し、Release公開、mainへの通常送信、リモートブランチとの一致確認の順に進める。 |

下書きのタイトル・本文はGitHub上で編集できます。確認した内容で公開し、`main` を `dev` へマージして生成物・修正を引き継ぎます。

```powershell
pwsh -NoProfile -File scripts/release.ps1 -PublishDraft
git switch dev
git merge main
git push origin dev
```

同名タグが別コミットを指す場合は停止します。下書き作成後にソースを修正する場合は、新しい版の候補として準備します。

Releaseの公開に失敗したらmainを送信しません。mainへの送信だけが失敗した場合は、同じRelease・タグ・資産を照合して `-PublishDraft` を再実行できます。公開mainが分岐している場合は停止します。

下書きの資産更新では、余剰資産を除去してローカルZIPと一致させます。公開成功はReleaseとリモートブランチの照合で判定します。

### プレビュー版

`dev` の確定したソースからパッケージを作り、`release.ps1 -CreatePrereleaseDraft -PreviewSuffix preview.1` を実行します。HEADにプレビュータグを付け、prereleaseの下書きを作成します。内容を確認し、GitHub上で公開します。追跡中の公開メタデータと公開mainは変更しません。

次の候補では `preview.2` など新しい接尾辞を使います。`-PublishDraft` は正式版専用です。

## 実装とテストの対応

| 仕様項目 | 実装箇所 | テスト・確認方法 |
| --- | --- | --- |
| ブランチと作業起点 | [AGENTS.md](../../../AGENTS.md)、[開発手順](../../setup.md)、GitHubのブランチ・タグ設定 | 文書と設定を照合する。 |
| バージョンの正本・互換ファイル不変・公開順序 | [release.ps1](../../../scripts/release.ps1) | [ReleaseScriptVersionSourceTests](../../../BeMusicSeeker.Tests/Verification/ReleaseScriptVersionSourceTests.cs): 隔離repoと実Git、GitHub CLIの代替を使い、本番スクリプトの入口から生成物・参照・失敗時の副作用を確認する。 |
| 配布物生成と引渡し | [publish.ps1](../../../scripts/publish.ps1)、[verify-refactor.ps1](../../../scripts/verify-refactor.ps1) | Fullで配布物生成、既存データ利用、更新・起動を確認する。 |
| 表示リソースの整合 | [Resources.resx](../../../BeMusicSeeker/Properties/Resources.resx)、[翻訳](../../../lang) | [LocalizationResourceParityTests](../../../BeMusicSeeker.Tests/Localization/LocalizationResourceParityTests.cs)。履歴内容は差分で確認する。 |
| 実公開 | [release.ps1](../../../scripts/release.ps1) | 自動テストでは実GitHubを変更しない。許可された公開でタグ・資産・リモートブランチの照合結果を確認する。 |

## 関連資料

[ポータブル更新](../integration/portable-update.md)、[検証](testing.md)、[アーキテクチャ](../core/architecture.md)、[開発環境の構築](../../setup.md)。
