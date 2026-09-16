# バージョン更新とリリース

## 目的と適用範囲

明示的に依頼されたバージョン更新・配布・公開の手順を定めます。文書整理や通常実装から、タグ・送信・公開を自動的に行うことはありません。

## 用語

[共通用語集](../glossary.md)を参照します。ここでの「配布版」はスクリプトが生成する実行物一式、「公開メタデータ」は更新確認に使う `version.txt` と `update.json` です。「公開ブランチ」は公開用リポジトリの `main`、「リリース資産」は GitHub Release に添付する配布ZIPを指します。

## 仕様

### バージョンの正本

[Properties/AssemblyInfo.cs](../../../Properties/AssemblyInfo.cs) の `AssemblyInformationalVersion` を、パッケージ名、タグ、公開メタデータの正本とします。`AssemblyVersion` は互換性上の理由または明示指示がない限り変更しません。

### 同時に更新する情報

バージョン更新時は、`AssemblyInformationalVersion`、[ReleaseNotesWindow.xaml](../../../BeMusicSeeker/Views/ReleaseNotesWindow.xaml) の `Update_history`、`release notes/vX.X.X.X リリースノート.md` を同じ変更で揃えます。リリースノートは GitHub Release の本文に使える内容にします。

画面内の更新履歴は、新しい説明文を日本語で直接記述してよい例外です。通常のダイアログ・設定・エラー等にこの例外を広げません。公開する製品の更新履歴は利用者向け配布情報であり、開発計画の完了記録とは区別します。

### 生成と検証

`version.txt` と `update.json` は手編集せず、[publish.ps1](../../../scripts/publish.ps1) と [release.ps1](../../../scripts/release.ps1) が生成します。配布物は[アーキテクチャ](../core/architecture.md)の配置、更新は[ポータブル更新](../integration/portable-update.md)の契約に従います。

関連する `ReleaseScriptVersionSourceTests` と `LocalizationResourceParityTests` を確認し、リリース前には[検証仕様](testing.md)の `Full` を使います。パッケージ作成、下書き、タグ、送信、公開は、それぞれ利用者が許可した操作だけを行います。

### 下書き作成と公開

`release.ps1` は公開用リポジトリの `main` ブランチで実行することを確認し、下書き作成と公開を分けます。下書きの作成は、公開ブランチを送信する許可を兼ねません。

| 操作 | 処理と照合 |
| --- | --- |
| `-CreateDraft` | `AssemblyInformationalVersion` と公開用リポジトリの `dist/` にあるZIPから公開メタデータを生成し、リリース用コミットとタグを用意する。タグだけを送信してローカルとの一致を確認し、リリースノートを使って下書きを作成・更新する。公開ブランチは送信しない。 |
| `-PublishDraft` | 対象の GitHub Release が存在し、リモートタグ・ローカルタグ・現在の `HEAD` が同じコミットを指し、リリース資産の名前・サイズが公開メタデータの作成元のローカルZIPと一致することを確認する。下書きなら先に公開し、その後に同じコミットを `origin/main` へ送信して、リモートブランチの一致を確認する。既に公開済みなら公開処理を重ねず、同じ照合とブランチへの反映を行う。 |

この順序で、公開ブランチの `update.json` が未公開のリリース資産を指す状態を避けます。`raw.githubusercontent.com` の配信内容は、キャッシュやブランチ参照の反映が遅れる可能性があるため、公開成功の判定に使いません。リモートブランチが対象コミットを指すことを確認します。

既存の下書きを更新する場合は、今回のローカルZIPにない余剰資産を削除してから再アップロードし、名前・サイズを照合します。古いメタデータ同梱版などを残したまま公開しません。

## 実装とテストの対応

| 仕様項目 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| バージョンの参照元 | [AssemblyInfo.cs](../../../Properties/AssemblyInfo.cs)、[リリーススクリプト](../../../scripts/release.ps1) | [ReleaseScriptVersionSourceTests](../../../BeMusicSeeker.Tests/ReleaseScriptVersionSourceTests.cs): スクリプトが同じ版の正本を使うこと。 |
| 表示リソースの整合 | [Resources.resx](../../../BeMusicSeeker/Properties/Resources.resx)、[翻訳](../../../lang) | [LocalizationResourceParityTests](../../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs): キーと置換項目の整合。履歴内容は差分で確認する。 |
| 下書き作成・公開の順序と照合 | [リリーススクリプト](../../../scripts/release.ps1) の `Invoke-CreateDraft`、`Invoke-PublishDraft`、`Sync-ReleaseAssets`、`Assert-ReleaseAssetsMatchLocal` | 自動テストによる実公開は行わない。手順点検でタグだけの送信、タグ・HEAD・資産の照合、資産公開からブランチ送信への順序を確認する。許可された実公開では、同スクリプトのリモート照合結果も確認する。 |
| 配布・更新の受入 | [verify-refactor.ps1](../../../scripts/verify-refactor.ps1) | [検証仕様](testing.md)の `Full` と更新受入。公開操作の許可は作業指示で確認する。 |

## 関連資料

[ポータブル更新](../integration/portable-update.md)、[検証](testing.md)、[アーキテクチャ](../core/architecture.md)。
