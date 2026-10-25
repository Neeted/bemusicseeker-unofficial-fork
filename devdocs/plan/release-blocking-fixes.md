# リリース前の必須修正

## 目的と前提

通常操作で到達する編集・検索・選択の誤動作を解消する。本書は未完了の実装と検証に必要な計画である。

照合対象は `dev` の `0ba3d84afd729660d77f5a514bd68c03e6d9beb4`。元資料の基準 `3e4fa9b35e20b4d1f3a8060fcbf3b3f03e47862f` との差はランプビューアの表示・関連テスト・翻訳・仕様で、以下の不具合経路に変更はない。着手時に対象版と到達経路を再確認する。

追加の設定レビューは `4aaa6581` を基準とする `.tmp/release-settings-review-fixes.md` と現行コードを照合し、S2を残る必須修正として扱う。資料の修正案自体は利用者指示として扱わず、以下の利用者回答を操作契約とする。S3は任意改善へ残す。

元資料は `.tmp` の `bemusicseeker-dev-release-fix-plan.md`、`bemusicseeker-release-fix-plan(2).md`、`bemusicseeker-playlist-release-fix-plan.md`。以下では識別子の重複を避け、それぞれ「設定」「一覧・導入」「プレイリスト」を付ける。再開に必要な根拠・方針は本書にまとめ、元資料の保存場所には依存しない。

本書に残る必須対象は3件。設定操作の通常閉鎖・再表示、保存・生成・失敗状態と一覧編集は、[設定保存・生成と一覧編集の簡素化](release-settings-and-cell-edit-simplification.md)へ再計画する。[任意の改善候補](release-follow-up-candidates.md)は、今回の実装・リリース完了条件に含めない。現行仕様の正本は [設定](../spec/runtime/settings.md)、[一覧](../spec/ui/table-view.md)、[取り込み・導入](../spec/library/drop-install.md)、[プレイリスト保存・出力](../spec/playlist/storage-and-export.md)、[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)を参照し、採用した変更は実装と同じコミットで反映する。

## 指摘の妥当性

本番の入口と保存・表示更新の接続を静的に照合した結果、以下の3件を修正対象として扱う。各項目の実装時に既存テストを拡張して再現・回帰を確認する。特にWPFのイベント順序と実Bindingの症状は、その接続を動かして確認する。

| 元項目 | 判断と現行コードの根拠 | 修正の境界 |
| --- | --- | --- |
| 設定S2：欠損エンコーダーで録音形式を偽表示 | [EncoderIndex](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)のgetterは実行ファイル欠損時に保存値を変えずWAVEを返す。 | 保存済み形式を表示し、利用者が明示選択したWAVEで修復する。無効な新形式の拒否と実変換の既存代替規則は維持。 |
| 設定R3：プリセットを続けて編集・削除できない | [プリセット保存](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)はeditorへ未正規化名を戻す一方、[ストア](../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryDisplayTarget.cs)は前後空白を除く。後続の名前照合が外れる。 | 確定する一件を正規化し、保存とeditorに同じ内容を使う。新しいIDや保存形式は不要。 |
| 設定R4：追加出力先名の空白入力 | [setter](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)の毎回の `Trim()` が [PropertyChangedのBinding](../../BeMusicSeeker/Views/Settings/Pages/PlaylistSettingsPage.xaml)と組み合わさる。確定時にも既存の正規化がある。 | 入力文字列をそのまま保持し、正規化は改名確定時だけにする。逐次入力を実Bindingで確認する。 |

WPF固有の根拠は .NET 10 の [BindingExpressionBase.EndSourceUpdate](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Data/BindingExpressionBase.cs)も照合した。source更新後にsetterの値をtargetへ再転送する。実アプリのイベント接続・最終表示の確認は別途必要である。

## 採用済みの操作契約

以下は今回の利用者回答で確定した。元資料中の「採用する」という表現だけを合意根拠にしない。

### 共通の設定候補の扱い

候補切替・確認・実受付の共通契約は[設定仕様](../spec/runtime/settings.md)と[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md)を正本とする。以下の残作業でも、自分の候補と他項目の最新確定値だけを使い、関係のない候補と開始済みTaskの回収を保つ。過去の要求・成功段階の保持は新しい受入条件にせず、簡素化計画へ従う。

### 録音形式の表示

録音形式のgetterは常に保存済み形式を表示する。利用不能な別形式の選択に対する既存の警告と保存拒否を維持し、明示WAVE選択は通常保存する。不足実行ファイルを理由に表示や保存形式を自動で置き換えず、実変換時の代替規則は変更しない。

## 着手順とコミット単位

先に設定保存・生成と一覧編集の簡素化を進め、その後に本書の残る表示・入力修正を行う。表は残作業の直列順であり、同じファイルを触ることだけで独立した修正を一コミットへまとめない。各単位に必要な実装・回帰テスト・仕様・翻訳を含め、検証・レビュー後に `dev` へコミットする。

| 順 | コミットの主題 | 項目・主な変更先 | 完了を判定する代表ケース |
| --- | --- | --- | --- |
| 14 | `fix: 録音形式を保存値どおりに表示する` | 設定S2。EncoderIndexと実録音ページ、既存設定/変換snapshot。 | 保存済みMP3で実行ファイル欠損でもMP3を表示。明示WAVE選択で保存/再表示/snapshot一致、別の利用不能形式拒否でも表示と保存値一致。 |
| 15 | `fix: 履歴表示プリセットの確定名を保存値と揃える` | 設定R3。プリセット確定、必要な既存正規化の再利用。 | ` 練習 ` を保存後、画面を開き直さず対象変更・改名・削除でき、JSONと表示が同じ一件を指す。取消・空名・重複名の検証を保つ。 |
| 16 | `fix: 追加出力先の改名入力で途中の空白を保持する` | 設定R4。入力setterと実Bindingの回帰。 | `My`→`My `→`My Charts` を保持する。前後空白は確定時だけ除き、空白名を拒否し、改名後の出力先・保存値・表参照を揃える。 |

プレイリストのサマリー・詳細を識別追従へ一律に変えるF01は未採用であり、共通部品を扱うことを理由にその挙動まで広げない。

追加設定修正・プリセット・改名入力は設定ViewModelとWindowを共有するため、書込みは原則直列とする。独立して進める場合も許可パスと共有依存を分ける。

## 検証と実装開始時の引継ぎ

本書は受入条件と分割の方針であり、[独立したテスト設計](../spec/development/agent-workflow.md#テスト設計の要否)や[実装直前の引継ぎ点検](../spec/development/agent-workflow.md#引継ぎの点検)の代替ではない。実装の指示後、確定した契約から不足する恒久テストを設計し、承認・点検してから実装する。

既存テストは以下を起点とし、同じ責務のケースを拡張する。実装から正解を作らず、上表の結果を判定する。Windowの条件網羅や新しい汎用テスト基盤は増やさない。

| 領域 | 既存の検証先と補う保証 |
| --- | --- |
| 設定 | `SettingDialogOutputConfirmationTests`、`SettingDialogLr2OwnerTests`、`SettingDialogEnvironmentConfirmationTests`、`SettingDialogEditCompletionTests`、`SettingDialogCustomFolderOutputBaseTests`、`SettingsWindowCompiledBehaviorTests` と既存補助操作系列。欠損encoderの実選択と変換snapshot、プリセット/改名入力を補い、独立保存・実Bindingと開始済みTask回収の保証を保つ。旧要求固定・成功段階省略の保証は簡素化計画へ合わせて置換する。 |

新しい通知文言は `Resources.resx`、`Resources.cs` と6言語辞書を同じ単位で揃える。各単位で必要な機能仕様と[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md)を更新し、不要になった説明・経路を削除する。

反復は標準入口 `scripts/verify-refactor.ps1` の対象を絞った `Quick`、通常開発のコミット・統合前は `Functional`、最終公開候補は `Full` とする。[実行範囲と実行回数](../spec/development/testing.md#実行範囲の選択)に従い、同じ最終版の成功済み検証を理由なく繰り返さない。自動WPF検証と利用者環境での実機確認を区別し、実機確認を必須条件にしない。

14・15・16は局所的な表示整合・正規化の修正なら `repo-static-review` を予定する。各単位の最終差分で[重大変更判定](../spec/development/agent-workflow.md#重大変更の判定)をやり直し、影響が広がれば切り替える。いずれも凍結・予備レビュー後に独立した最終レビューを行う。

## 完了条件と対象外

- 残る3件の受入条件を満たし、コード・保存値・出力・表示を必要な境界で確認する。未実施・失敗を成功扱いにしない。
- 全件走査・仮想行の全実体化、新しい永続フラグ、意味の変わるフォールバック、自動再試行を追加しない。
- コミット、統合、公開、バージョン更新は別途指示された範囲でのみ行う。
- 完了した項目は必要な契約・判断を正本へ移して本書から除く。全完了時は本書を削除し、[計画一覧](README.md)を更新する。
