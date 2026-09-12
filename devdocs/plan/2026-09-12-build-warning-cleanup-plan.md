# ビルド警告整理の仕上げ

状態: 完了（2026-09-12）

## 目的と完了条件

既存の警告削減パッチを維持し、実装の記述を固定する不要な依存テストと無効な警告抑制を整理した。実行時の動作、設定・リソース・配布物の互換性を維持し、標準検証と静的レビューを終えた未コミット差分とした。コミット、ステージ、公開、バージョン更新は未実施。

比較元は `96a027234ba9dad6273b60343fdf5bfd137fa738`。開始時の115ファイルの未コミット差分はユーザーが適用したパッチであり、無関係な整理や破棄を行わない。

## 決定事項

- `System.Resources.Extensions` と `System.Configuration.ConfigurationManager` の直接参照、無効な `NU1510` 抑制、不要になる中央バージョン宣言を削除し、同じ `win-x64` / ReadyToRun 条件で lock を再生成する。
- 2つのアセンブリは WindowsDesktop ランタイムが供給する。設定の型・保存形式、埋込みアイコン、公開配布形式は変更しない。
- `ManagedDependencyOutputPolicyTests` の2パッケージに対する直接参照・中央バージョン・lock entry の固定だけを削除する。他パッケージの検査や既存の出力構成・旧DLL混入防止は維持する。
- updater と portable layout の旧 `libs/System.Resources.Extensions.dll` リストは旧版からの更新時の除去・混入防止を担う。現在の直接参照とは責務が異なるため維持し、updater や runner は変更しない。
- 3テストファイルの5か所の `#pragma` と隣接行を formatter に合わせる。今回のパッチで追加された理由コメントは日本語に揃える。
- 既存パッチの nullable 注釈、同等APIへの置換、discard、obsolete 属性は維持する。残る nullable flow と `ServicePointManager` の仕様判断へは広げない。
- 元ログとパッチ後ログの比較で、optional parameter / event を nullable にした一方、対応 field / local / forwarding parameter が未整合のため新規警告になる箇所を確認した。既に null を sentinel として扱う6テストファイルでは注釈を一貫させる。ダイアログ生成を既存 assertion と通知回数で確認済みの1箇所は、その保証を null-forgiving 演算子で明示する。実行処理・assertion semantics は変更しない。

## テストの必要性

変更分類は build / 内部構成の整理。恒久テストの扱いは「削除のみ、代替不要」とする。2参照の存在は機能上の保証ではなく移行時の構成の固定であり、その削除を別の source 固定テストへ置き換えない。

保持する動作は `PortableSettingsPersistenceTests`、`ResourceIconContractTests`、既存の配布物・更新検証で確認できる。新規 assertion や既存の期待値の変更は行わず、Test Contract Packet と新規テストは不要。計画点検担当もこの分類を確認済み。追加の意味変更が必要になった場合だけ root に戻して再評価する。

到達経路は restore / build → WindowsDesktop 参照解決 → 起動時の設定・リソース利用、および self-contained publish → 既存データ起動・更新受入。依存宣言の除去による供給不足はビルド、既存の動作テスト、Full の配布受入で確認する。ライブラリ処理・並行性・データ規模に関する実装は変更しない。

## 実施した検証

計画点検後に単一実装担当が依存宣言、関連テスト、コメント、仕様対応を整理した。lockは `dotnet restore BeMusicSeeker.sln -r win-x64 -p:PublishReadyToRun=true -p:RestoreLockedMode=false` で再生成し、2entryの削除だけで他のバージョン変更がないことを確認した。

対象の書式確認と次の標準Quickを実行した。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ManagedDependencyOutputPolicyTests|FullyQualifiedName~PortableSettingsPersistenceTests|FullyQualifiedName~ResourceIconContractTests|FullyQualifiedName~PlaylistLampViewerSessionTests|FullyQualifiedName~SettingDialogEditCompletionTests|FullyQualifiedName~SettingsDialogBehaviorTests|FullyQualifiedName~BmsLibraryPendingLegacyMutationTests|FullyQualifiedName~BmsLibraryPendingPackageRegroupTests|FullyQualifiedName~PlaylistLampViewerWindowManagerTests|FullyQualifiedName~SettingsWindowPresentationTests|FullyQualifiedName~ResilientFileMutationServiceTests'
```

統合後に `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full` を実行し、Full内のFunctionalを最終通常検証とした。成功後に全差分を凍結して独立した静的レビューを実施し、修正必須の指摘なし。レビュー後はこの完了記録だけを更新した。

## 進捗

| 作業項目 | 状態 | 実施内容の要約・残課題・反映先 |
| --- | --- | --- |
| 計画と必要性点検 | 完了 | 削除のみ・既存動作検証の活用・Full 実行を採用。旧版DLL除去契約を維持。 |
| 依存宣言・テスト・書式の整理 | 完了 | 2参照と対応する固定検査を削除。lock差分は2entryの削除のみ。追加pragmaとnullable注釈を整合。対象Quickは298成功・6スキップ・失敗なし。 |
| 統合検証 | 完了 | Full 成功。設定・リソース、配布物の起動、更新・再起動、v2.1.6.0からの更新受入を確認。 |
| 静的レビュー | 完了 | 依存供給・設定・リソース・配布と更新経路、pragma修正、nullable注釈、テスト削除の妥当性を確認。修正必須の指摘なし。 |

## 検証結果と残課題

| 検証 | 結果 |
| --- | --- |
| 対象Quick | 298成功・6スキップ・失敗なし。書込み可能な別ボリューム不足4件、symlink作成権限不足2件。 |
| FullのFunctional | 4,757成功・11スキップ・失敗なし。252.9秒で、180秒の報告目安を超えたが300秒の上限内で成功。再実行なし。 |
| FullのProcessIntegration | 52成功・2スキップ・失敗なし。 |
| FullのReleaseAcceptance | 2成功・失敗なし。別途v2.1.6.0からの更新受入も成功。 |
| その他のFull検証 | locked restore、tool restore、format、analyzer、tool smoke、配布物作成、既存データ起動、更新・再起動がすべて成功。analyzer指摘なし。 |
| 文書・生成物 | UTF-8 / LF、参照先、lock JSON構文、`git diff --check` を確認。 |

Fullの13スキップは、書込み可能な別ボリューム不足5件、symlink作成権限不足6件、明示的な手動実行用のJava smokeと外部encoder smoke各1件。これらの実環境条件は今回未検証。

警告は元の1,166件から再コンパイルを含むQuickで683件へ減少し、NU1510とパッチ由来の新規警告は解消。残りはnullable関連679件と `ServicePointManager` のSYSLIB0014が4件（WPFの二重コンパイルを含む）。最終Fullのbuildはアプリ側の増分ビルドによりnullable関連679件。残存警告は別の仕様判断を伴うため今回の対象外とする。

診断記録は `artifacts/verification/tests-quick-20260912-225851` と `artifacts/verification/tests-full-20260912-230556`。恒久仕様の反映先は [アーキテクチャ](../spec/architecture.md)。
