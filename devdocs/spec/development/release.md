# ブランチ運用・バージョン更新・リリース

## 目的と適用範囲

公開リポジトリ `Neeted/bemusicseeker-unofficial-fork` で、開発、配布物の生成、正式公開を行う契約です。開発用と公開用の別リポジトリや、両者間のファイルコピーは使用しません。バージョン更新、タグ、送信、Release公開は、作業指示で許可された範囲で行います。

## 用語

[共通用語集](../glossary.md)を参照します。「配布版」はスクリプトが生成する実行物一式、「公開メタデータ」は `update.json`、「リリース資産」は GitHub Release に添付するZIPです。`version.txt` は旧版向けの固定された互換資源であり、公開メタデータの版の正本ではありません。

## 仕様

### ブランチと履歴

| 対象 | 契約 |
| --- | --- |
| `dev` | 次回正式版の開発の起点・統合先。通常の作業ブランチとPRはここを対象にする。 |
| `main` | 正式公開済みのコードと配信データ。アプリの変更は正式リリース時に反映する。 |
| 作業ブランチ | `dev` から作り、検証後に `dev` へ統合する。回帰調査に意味のあるコミット単位を残す。 |
| 緊急修正 | 公開 `main` から分岐し、必要な修正だけを検証して正式版を公開する。その後 `main` を `dev` へマージする。 |
| TSV・文書の保守 | アプリの版を上げずに `main` へ反映できる。同じ変更を `dev` にもマージする。更新情報を未公開版へ変更しない。 |

`dev` と `main` の間はsquashやrebaseを使わず、親子関係を保つマージで統合します。正式版の境界はマージコミットとタグで示します。常設の `release` ブランチは設けません。`main` は最終タグとの常時完全一致を要求せず、TSVや文書だけの保守も含めて「公開してよい状態」を保ちます。

GitHubの既定ブランチは `main` です。通常のPR先は明示的に `dev` とします。`main` と `dev` の削除・強制更新、リリースタグの削除・付け替えを防ぎます。線形履歴やPRの必須化によって、マージコミットや下記のリリース送信を阻害しません。

過去の公開タグとReleaseは当時の公開内容を保持します。全ソースを含まない旧タグを、後から開発系列のコミットへ付け替えません。移行前のコードの回帰調査には開発系列のコミットを使い、除去済みの大容量DBに依存する古いテストは、必要な入力をローカルで供給するか調査対象からスキップします。実行不能を成功と扱いません。

### 維持する配信資源

| 資源 | 契約 |
| --- | --- |
| `main/bms-md5-url-map.tsv` | URL補完が参照する既存の配置とURLを維持する。通常のGitファイルとして管理する。 |
| `main/version.txt` | v2.1.0.0より前のアプリ向けに `2.1.6.0` を固定配信する。旧版はより大きい版の値を観測すると通知するだけなので、以後の版への追随は不要。生成・更新処理を設けず、現在版の正本・公開前提条件にもしない。 |
| `main/update.json` | 正式公開されたZIPの版・URL・サイズ・SHA-256を配信する。実装の契約は[ポータブル更新](../integration/portable-update.md)に従う。 |
| `main/docs/` | GitHub Pagesの配信元。Markdownと画像に加え、生成HTMLと `.nojekyll` を管理する。生成HTMLは直接編集しない。 |

公開サイトのURL、既存のReleaseと添付資産は維持します。文書だけの更新で公開サイトを変更する場合は、`uv run scripts/build-doc-html.py --source-root . --output-root docs --site --site-url https://neeted.github.io/bemusicseeker-unofficial-fork` でHTMLを再生成してから反映します。

### バージョン更新

[Properties/AssemblyInfo.cs](../../../Properties/AssemblyInfo.cs) の `AssemblyInformationalVersion` をパッケージ名、正式タグ、公開メタデータの版の正本とします。`AssemblyVersion` は互換性上の理由または明示指示がある場合だけ変更します。

バージョン更新は通常 `dev` を起点に行い、次を同じ変更で揃えます。

- `AssemblyInformationalVersion`。
- [ReleaseNotesWindow.xaml](../../../BeMusicSeeker/Views/ReleaseNotesWindow.xaml) の `Update_history`。
- `release notes/vX.X.X.X リリースノート.md`。GitHub Release本文として使える内容にする。

バージョン更新だけでは、タグ作成や公開を行いません。`version.txt` は更新対象に含めません。画面内の更新履歴は、新しい説明を日本語で直接記述してよい例外です。通常のダイアログ・設定・エラーへ例外を広げません。

### 配布物の生成と検証

[publish.ps1](../../../scripts/publish.ps1) はスクリプトの所在するリポジトリからビルドし、既定では `dist/` にZIPを生成します。追跡された `update.json`、`version.txt`、ブランチ、タグ、GitHub Releaseを変更しません。別リポジトリの配置は不要です。

```powershell
pwsh -NoProfile -File scripts/publish.ps1
# メタデータ同梱版も生成する場合
pwsh -NoProfile -File scripts/publish.ps1 -IncludeMetadata
```

配布・更新・リリースに関わる変更は[検証仕様](testing.md)の `Full` を実行します。Fullは検証用の `SkipDocHtml` 配布物を隔離された出力先に一回生成して共有するため、検証前に同じ配布物を重複生成する必要はありません。正式配布用の `dist/` は、確定した同じソースからHTMLを含めて生成します。ソース変更後に古い同名ZIPを再利用しません。

### 正式リリースの準備

作業ツリーを整理し、リモートを取得してから対象の `dev` を確定します。次はローカルでリリース準備をする例です。コマンドごとに成功を確認し、失敗したら後続を実行しません。

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

`main` へのマージはこの段階ではローカルだけです。GitHub上のPRを先にマージして、新しい更新情報を配信しないでください。複数の未送信コミットやマージコミットがあっても、公開 `main` を祖先としていれば準備できます。緊急修正では `dev` 全体ではなく、`main` から作った修正ブランチを統合します。

### 下書きと公開

[release.ps1](../../../scripts/release.ps1) は同じリポジトリの `dist/` と版別リリースノートを使います。正式版の準備・公開は `main` 上で実行します。

| 操作 | 処理と照合 |
| --- | --- |
| `-CreateDraft` | 通常版ZIPを必須として公開メタデータを生成し、Pages HTMLを生成する。必要な生成物だけをリリースコミットに含め、現在のHEADへ正式タグを付ける。タグだけを送信し、下書きを作成・照合する。公開mainは送信しない。無関係な未コミット差分を取り込まない。 |
| `-PublishDraft` | ローカル・リモートタグとHEAD、追跡manifestとローカルZIPの版・名前・サイズ・SHA-256、リモート資産の名前・サイズ・集合を照合する。公開mainがHEADの祖先であることを確認し、資産公開の後に同じコミットをmainへ通常送信する。最後にリモートブランチとの一致を確認する。 |

下書き作成後のタイトル・本文だけの修正はGitHubの下書き画面で行います。正式公開は下書き状態だけを解除し、そこで編集したタイトル・本文をローカルのノートで上書きしません。本文再反映専用モードは設けません。下書きの確認後、許可された公開操作として実行します。

```powershell
pwsh -NoProfile -File scripts/release.ps1 -PublishDraft
git switch dev
git merge main
git push origin dev
```

`main` の変更を `dev` へ戻し、リリース時の生成物・修正も次の開発へ引き継ぎます。正式公開のタグは動かしません。同名タグが別コミットを指す場合は失敗させ、履歴をresetしたりタグを削除したりして継続しません。旧 `-RecreateDraft` は使用しません。下書き作成後にソースを修正する必要がある場合は、新しい版の候補として準備します。

資産の公開に失敗したらmainを送信しません。mainへの送信だけが失敗した場合、既に公開した同じReleaseとタグ・資産を照合して `-PublishDraft` を再実行できます。公開mainが分岐している場合は停止し、他の変更を上書きしません。新たな候補が必要なら版を分けて準備します。

公開前の下書きの資産更新では、ローカルにない余剰資産を除去して内容を一致させます。公開済みReleaseの資産を下書き更新として置換しません。`raw.githubusercontent.com` の応答はキャッシュ反映が遅れるため、公開成功の判定には使いません。

### プレビュー版

`dev` の確定したソースからパッケージを作り、`release.ps1 -CreatePrereleaseDraft -PreviewSuffix preview.1` を使います。プレビュータグはそのソースのHEADを指し、正式版タグとは別に作成します。GitHub Releaseはprereleaseの下書きとして確認し、許可された操作で公開します。追跡 `update.json` と `version.txt`、公開mainは変更しません。

次の候補では `preview.2` など新しい接尾辞を使い、既存タグを付け替えません。正式公開用の `-PublishDraft` をプレビュー公開に使用しません。

## 実装とテストの対応

| 仕様項目 | 実装箇所 | テスト・確認方法 |
| --- | --- | --- |
| ブランチと作業起点 | [AGENTS.md](../../../AGENTS.md)、[開発手順](../../setup.md)、GitHubのブランチ・タグ設定 | 文書と設定を照合する。履歴移行はコミット対応、親子関係、対象外ファイルの一致を個別確認する。 |
| バージョンの正本・互換ファイル不変・公開順序 | [release.ps1](../../../scripts/release.ps1) | [ReleaseScriptVersionSourceTests](../../../BeMusicSeeker.Tests/ReleaseScriptVersionSourceTests.cs): 隔離repoと実Git、GitHub CLIの代替を使い、本番スクリプトの入口から生成物・参照・失敗時の副作用を確認する。 |
| 配布物生成と引渡し | [publish.ps1](../../../scripts/publish.ps1)、[verify-refactor.ps1](../../../scripts/verify-refactor.ps1) | Fullの配布作成・既存受入。別公開cloneなしで実パッケージを生成し、更新・起動を確認する。 |
| 表示リソースの整合 | [Resources.resx](../../../BeMusicSeeker/Properties/Resources.resx)、[翻訳](../../../lang) | [LocalizationResourceParityTests](../../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs)。履歴内容は差分で確認する。 |
| 実公開 | [release.ps1](../../../scripts/release.ps1) | 自動テストでは実GitHubを変更しない。許可された公開でタグ・資産・リモートブランチの照合結果を確認する。 |

## 関連資料

[ポータブル更新](../integration/portable-update.md)、[検証](testing.md)、[アーキテクチャ](../core/architecture.md)、[開発環境の構築](../../setup.md)。
