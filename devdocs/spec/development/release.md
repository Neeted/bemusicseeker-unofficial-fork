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
| 通常の作業ブランチ | `dev` から作り、検証後に原則 `git merge --ff-only` で `dev` へ統合する。回帰調査に意味のあるコミット単位を残す。 |
| 緊急修正 | 公開 `main` から分岐し、必要な修正だけを検証して正式版を公開する。その後 `main` を `dev` へマージする。 |
| TSV・文書の保守 | アプリの版を上げずに `main` へ反映できる。同じ変更を `dev` にもマージする。更新情報を未公開版へ変更しない。 |

`dev` と `main` は親子関係を保つマージで統合し、squashやrebaseは使いません。正式版の境界はマージコミットとタグで示し、公開後のタグ・Release・添付資産を保持します。

GitHubの既定ブランチと通常のPR先は `dev` です。閲覧・clone・外部参加の入口を開発の起点に揃え、正式版の配信は引き続き `main` を使います。保護設定で `main` と `dev` の削除・強制更新、リリースタグの削除・付け替えを禁止し、マージコミットと保守者による `dev` への直接送信・リリース時の `main` への直接送信を許可します。

依頼範囲内の通常のローカルコミットは、全体ルートと単位責任者が自分の管理する作業ワークツリーで、必要な検証と独立レビューの合格後に行えます。工程ごとの利用者の明示承認は要求しません。無関係な差分を除き、意味のある単位に分け、適切なプレフィックス、日本語の現在形の件名と箇条書き本文を使います。送信、タグ、署名、公開、バージョン更新は明示指示または合意済みの手順がある場合だけ行います。

保守者自身の変更は、Issue対応を含め自己PRを作らず、作業ブランチでの検証後にローカルで `dev` へ統合して送信します。外部変更はPRで受け付けます。参加・受入・Issueの完了に関する方針は [CONTRIBUTING.md](../../../CONTRIBUTING.md)を参照してください。

### 通常の作業ブランチの統合

`dev` から分岐した通常の作業ブランチは、個々のコミットを維持して1本の履歴へ統合します。原則として `git merge --ff-only` を使い、通常作業の統合だけを目的とするマージコミットは作りません。

並列開発中はメイン作業ツリーを `dev` の統合専用として原則クリーンに保ち、各作業ブランチの新しい実装・テスト・仕様・計画は作業ワークツリーで完結させます。全体ルートは依存関係・必要な検証・独立レビュー・ブランチ内コミットが揃った単位から `dev` への統合を一件ずつ管理し、依存のない単位を着手順に揃えるために保留しません。先の統合後の最新 `dev` に対して次のブランチを確認します。ワークツリーの所有と独立性、未完了差分の安全な移管は[並列作業](agent-workflow.md#並列作業)、重い検証の開始調整と同一版の結果再利用は[検証仕様](testing.md#複数ワークツリーの検証)に従います。

複数ワークツリーのstashは同じリポジトリで共有されます。追跡済みファイルだけの退避commitには、`git stash create` が直接返す不変のcommit IDを優先します。`push` が必要なら一意のメッセージ・親コミット・自分の差分を照合して生成commitを特定し、一致しなければ適用しません。同じ呼出し内の `push` と `refs/stash` の再取得も原子的ではなく、後から `stash@{0}` や `pop` で適用対象を選びません。回収も自分の同じIDを確認し、可変indexの確認から `drop` までの間に他担当の退避を削除するおそれが残る場合は保全して担当間で後の回収時点を調整します。専用のlockや台帳は追加しません。

単位責任者は次を簡潔に引き継ぎます。別の台帳は作りません。

- ブランチ、最終コミット、基準にした `dev` の版。
- 変更内容と、他の単位・共有契約への影響。
- 検証した版・範囲・結果と、その根拠。
- 独立レビューの対象版・範囲・結果。
- 未確認点と残る引継ぎ。
- 一時資料とワークツリー・ブランチの後片付け。

全体ルートは引継ぎと差分から、契約の整合、最新 `dev` との差、単位間の接続を確認します。確認済みの調査やログ解析を一から繰り返さず、新しい基底や共有依存が影響する範囲を確認します。

検証後に作業ツリーをクリーンにし、統合先の `dev` を最新にしてから全体ルートが統合します。各コマンドの成功を確認してから次へ進みます。

```powershell
git switch dev
git pull --ff-only
git merge --ff-only codex/変更内容
```

作業ブランチの統合がfast-forwardできない場合は停止し、自動的にマージコミットやsquashへ切り替えません。未公開で他者が依存していない作業ブランチは、そのブランチ上で `git rebase dev` により最新の `dev` へ更新します。競合があれば解消し、更新後の差分と基底の影響に応じて必要な[検証](testing.md)と[レビュー](agent-workflow.md#最終レビュー)を行ってから、`dev` 上で `git merge --ff-only` による統合を再実行します。

`dev`・`main` と公開済みの履歴はrebaseしません。公開・共有済みの作業ブランチなど、履歴を書き換えられない場合は更新方法を相談します。通常作業でマージコミットを残すのは、他者が依存する履歴の維持や分岐経緯の記録など、理由を明示して利用者または保守者と合意した場合に限ります。

統合後は全体ルートが必要資料と残る引継ぎを確認して、作業ワークツリーを再利用または回収します。再利用する場合は、次の単位と作業ワークツリーを割り当て、最新の `dev` から次の作業用ブランチを作って切り替え、完了したブランチを次の作業へ使い続けません。

```powershell
# 再利用する作業ワークツリーへ新しいブランチを割り当てる。
git -C '作業ワークツリーのパス' switch -c codex/次の変更 dev
# 完了したブランチがどのワークツリーでも使われていない状態で削除する。
git branch -d codex/完了した変更
```

回収する場合は、必要な未追跡・除外ファイルを保全してワークツリーを回収し、その後に完了したローカル作業ブランチを削除します。削除対象は `dev` へ統合済みの作業ブランチだけとし、`dev`・`main`・未統合ブランチを含めません。`git branch -d` が拒否した場合は理由を確認し、自動で強制削除へ切り替えません。他のチャットのワークツリーは、明示許可なく変更・回収しません。

正式リリースの `dev` から `main` への統合は、下記の手順どおり `--no-ff` を使います。緊急修正や公開後の `main` から `dev` への取り込みも親子関係を保つマージを使い、両ブランチ間の祖先関係を維持します。

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

[ApplicationVersion.props](../../../ApplicationVersion.props) の `BeMusicSeekerAppVersion` を通常公開版の正本とし、appの `AssemblyInformationalVersion`、パッケージ名、正式タグ、公開メタデータへ同じ値を使います。InformationalVersionだけをbuild時に生成し、[AssemblyInfo.cs](../../../BeMusicSeeker/Properties/AssemblyInfo.cs) の `AssemblyVersion` とその他の属性は維持します。`AssemblyVersion` は互換性上の理由または明示指示がある場合だけ変更します。

バージョン更新は通常 `dev` を起点に行い、次を同じ変更で揃えます。

- `ApplicationVersion.props` の通常版。
- [ReleaseNotesWindow.xaml](../../../BeMusicSeeker/Views/Dialogs/ReleaseNotesWindow.xaml) の `Update_history`。
- `release notes/vX.X.X.X リリースノート.md`。GitHub Release本文として使える内容にする。

タグ作成・公開は、下記のリリース手順で行います。画面内の更新履歴は日本語で直接記述できます。その他の表示文言は[表示リソースの規則](../../../AGENTS.md#ログと表示文言)に従います。

### 配布物の生成と検証

公開候補は `prepare-candidate` で先に生成し、そのcatalogをFullへ渡します。候補の出力先は存在しない専有の絶対パスを指定します。

```powershell
$candidate = Join-Path (Get-Location) 'artifacts/candidates/release-1'
dotnet run --project tools/BeMusicSeeker.Verify -- prepare-candidate --output $candidate
# メタデータ同梱版も生成する場合
dotnet run --project tools/BeMusicSeeker.Verify -- prepare-candidate --output "$candidate-with-metadata" --include-metadata
```

候補にはHTML付き通常Current ZIP、固定Legacy、試験Nextを含め、完成後だけ出力直下へ `catalog.json` を保存します。`--include-metadata` はCurrentの同じpublish出力から同梱版も生成し、通常版と追加metadata以外の内容が同じことを確認します。元データの指定には `--metadata-source <path>` を使います。

内部で呼ぶ [publish.ps1](../../../scripts/publish.ps1) は単独でも利用でき、既定では `dist/` へZIPだけを生成します。通常ZIPのみの生成が正常完了した時点で、同版・標準名の `-with-metadata.zip` 一つを退役します。他版ZIPとmetadata元データは保持します。`-IncludeMetadata` は同じ今回の出力から両ZIPを生成します。単独publishの成功はFull合格の代わりにはなりません。

更新検証用の実行限定版は `-ApplicationVersion` に四成分の数値版を渡します。専有 `-ArtifactRoot` を必須とし、`-SkipBuild` との併用は拒否します。app/updaterそれぞれのrestore・build中間出力は指定root内のproject別 `obj/`・`bin/` に分離し、配布出力は `app/`・`updater/`・`dist/` へ置きます。既存の通常 `obj/`・`bin/` をsourceとして再取り込みません。版変更時には生成属性も更新し、正本の版値やAssemblyVersionを書き換えません。

appの通常restoreは `BeMusicSeeker/packages.lock.json`、配布profileのrestore/publishは `BeMusicSeeker/packages.publish.lock.json` を使います。後者はSingleFile analyzerのSDK依存を含み、通常版・実行限定版・metadata同梱版で共有します。依存更新時は `dotnet restore BeMusicSeeker/BeMusicSeeker.csproj --runtime win-x64 --force-evaluate -p:Platform=x64 -p:Configuration=Release -p:PublishProfile=WinX64SelfContained` で配布lockも明示更新し、解決版の差分を確認します。通常のpublish経路はlocked restoreを維持し、lock欠落は出力変更前に拒否し、不一致時に再生成へ切り替えません。

候補生成ではCurrentは通常版、NextはCurrentの実バイナリから読んだ版の末尾をcheckedで1増やした版です。両variantを各一回publishし、各240秒の範囲内で成果物も検査します。Fullは指定候補へCore・Windows・Scenarios・Desktopと既存データ起動・現行更新の実配布受入を接続し、製品publishやZIP生成を繰り返しません。環境suiteはCompatibilityで別に明示実行します。releaseは正本の版を使い、試験Nextを公開対象にしません。単独publishの `-SkipBuild` 利用でも、既存appの実版とパッケージ版の一致を確認します。

正式リリース前は `full --distribution-catalog <absolute catalog>` で公開予定の候補を検証し、合格した同じZIPを公開します。Fullの既存summaryに記録する `passed` と `releaseArtifacts` を、公開時にcatalogおよび実ZIPの版・パス・サイズ・SHA-256と照合します。合格後の再buildやZIP再生成を同一候補の検証済みとして扱いません。

テストだけを変更し、製品・依存・配布設定・同梱資料や内容が変わらない場合は、通常のGit差分を確認して候補を再利用できます。それらが変わった場合は新しい候補を生成しFullを再実行します。Fullの整形による変更も同じ判断の対象です。通常開発のコミット・`dev` への統合と影響に応じた検証は[実行範囲の選択](testing.md#実行範囲の選択)に従います。

実機確認の推奨と未実施の扱いは[検証方針](testing.md#検証方針)に従います。実機確認の結果待ちをリリース完了の条件にはしません。Fullの必須自動検証と、公開時のタグ・資産・リモートブランチの照合は維持します。

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
$candidate = Join-Path (Get-Location) 'artifacts/candidates/release-1'
dotnet run --project tools/BeMusicSeeker.Verify -- prepare-candidate --output $candidate
dotnet run --project tools/BeMusicSeeker.Verify -- full --distribution-catalog "$candidate/catalog.json"
# Fullが表示した結果ディレクトリの合格summaryを指定する
$fullSummary = 'C:/path/to/full-run/summary.json'
pwsh -NoProfile -File scripts/release.ps1 -CreateDraft -DistributionCatalog "$candidate/catalog.json" -FullSummary $fullSummary
```

公開 `main` への反映はRelease公開後に行います。準備中のHEADは公開 `main` を祖先に持つことが必要です。緊急修正では、上記の `dev` の代わりに `main` から分岐した修正ブランチをマージします。

### 下書きと公開

[release.ps1](../../../scripts/release.ps1) は明示したcatalogのCurrentと任意のmetadata同梱版、合格Full summary、版別リリースノートを使います。すべてのモードで `-DistributionCatalog` と `-FullSummary` に絶対パスが必要です。候補・summary・ZIPの公開集合が一致しない場合は公開準備に進みません。正式版の準備・公開は、作業ツリーがクリーンな `main` 上で実行します。

| 操作 | 処理と照合 |
| --- | --- |
| `-CreateDraft` | 通常版ZIPを必須として公開メタデータとPages HTMLを生成し、生成物だけをコミットする。HEADへ正式タグを付け、タグだけを送信して下書きを作成・照合する。 |
| `-PublishDraft` | HEADとローカル・リモートタグ、公開メタデータとローカルZIPの版・名前・サイズ・SHA-256、リモート資産の名前・サイズ・集合を照合する。公開mainがHEADの祖先であることを確認し、Release公開、mainへの通常送信、リモートブランチとの一致確認の順に進める。 |

下書きのタイトル・本文はGitHub上で編集できます。確認した内容で公開し、`main` を `dev` へマージして生成物・修正を引き継ぎます。

```powershell
pwsh -NoProfile -File scripts/release.ps1 -PublishDraft -DistributionCatalog "$candidate/catalog.json" -FullSummary $fullSummary
git switch dev
git merge main
git push origin dev
```

同名タグが別コミットを指す場合は停止します。下書き作成後にソースを修正する場合は、新しい版の候補として準備します。

Releaseの公開に失敗したらmainを送信しません。mainへの送信だけが失敗した場合は、同じRelease・タグ・資産を照合して `-PublishDraft` を再実行できます。公開mainが分岐している場合は停止します。

下書きの資産更新では、余剰資産を除去してローカルZIPと一致させます。公開成功はReleaseとリモートブランチの照合で判定します。

### プレビュー版

`dev` の確定したソースから候補を生成しFull合格後、`release.ps1 -CreatePrereleaseDraft -PreviewSuffix preview.1 -DistributionCatalog <absolute catalog> -FullSummary <absolute summary>` を実行します。HEADにプレビュータグを付け、prereleaseの下書きを作成します。内容を確認し、GitHub上で公開します。追跡中の公開メタデータと公開mainは変更しません。

次の候補では `preview.2` など新しい接尾辞を使います。`-PublishDraft` は正式版専用です。

## 実装とテストの対応

| 仕様項目 | 実装箇所 | テスト・確認方法 |
| --- | --- | --- |
| ブランチと作業起点・コミット・統合方法 | [AGENTS.md](../../../AGENTS.md)、[CONTRIBUTING.md](../../../CONTRIBUTING.md)、[エージェント運用](agent-workflow.md)、[開発手順](../../setup.md)、GitHubのブランチ・タグ設定 | 文書と設定を照合する。統合手順は隔離したリポジトリの実Gitでfast-forward、分岐時の停止、作業ブランチのrebase、公開履歴のマージ、最新 `dev` からの次ブランチ作成と統合済み・全ワークツリー未使用の完了ブランチ削除を確認する。責任者による検証・独立レビュー後のローカルコミット、メイン作業ツリーの統合専用運用、準備済み単位からの統合、更新後の必要な検証・レビュー、統合引継ぎの版・根拠と全体ルートの確認は担当間の連絡と対象版を照合する。 |
| バージョンの正本・互換ファイル不変・公開順序 | [release.ps1](../../../scripts/release.ps1) | [ReleaseScriptVersionSourceTests](../../../tests/BeMusicSeeker.Core.Tests/Verification/ReleaseScriptVersionSourceTests.cs): Coreの日常検証で17条件を実行する。実Gitと専有local remoteを使い、gh・文書生成のみ代替して実公開は行わず、本番スクリプトの入口から生成物・参照・失敗時の副作用を確認する。 |
| 正式リリース前の配布物生成と引渡し | [publish.ps1](../../../scripts/publish.ps1)、[検証入口](../../../tools/BeMusicSeeker.Verify/README.md) | prepare-candidateで生成・構成検査し、指定した同じ候補のFullで既存データ利用、更新・起動を確認する。公開scriptが合格summaryと同一ZIP集合を照合する。 |
| 表示リソースの整合 | [Resources.resx](../../../BeMusicSeeker/Properties/Resources.resx)、[翻訳](../../../lang) | [LocalizationResourceParityTests](../../../tests/BeMusicSeeker.Core.Tests/Localization/LocalizationResourceParityTests.cs)。履歴内容は差分で確認する。 |
| 実公開 | [release.ps1](../../../scripts/release.ps1) | 自動テストでは実GitHubを変更しない。許可された公開でタグ・資産・リモートブランチの照合結果を確認する。 |

## 関連資料

[ポータブル更新](../integration/portable-update.md)、[検証](testing.md)、[アーキテクチャ](../core/architecture.md)、[開発環境の構築](../../setup.md)。
