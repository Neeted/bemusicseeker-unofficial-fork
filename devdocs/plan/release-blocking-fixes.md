# リリース前の必須修正

## 目的と前提

通常操作で到達する保存情報の消失、作成・導入の失敗、設定不整合、編集・検索の誤動作を解消する。本書は未完了の実装と検証に必要な計画である。

照合対象は `dev` の `0ba3d84afd729660d77f5a514bd68c03e6d9beb4`。元資料の基準 `3e4fa9b35e20b4d1f3a8060fcbf3b3f03e47862f` との差はランプビューアの表示・関連テスト・翻訳・仕様で、以下の不具合経路に変更はない。着手時に対象版と到達経路を再確認する。

元資料は `.tmp` の `bemusicseeker-dev-release-fix-plan.md`、`bemusicseeker-release-fix-plan(2).md`、`bemusicseeker-playlist-release-fix-plan.md`。以下では識別子の重複を避け、それぞれ「設定」「一覧・導入」「プレイリスト」を付ける。再開に必要な根拠・方針は本書にまとめ、元資料の保存場所には依存しない。

残る必須対象は9件。設定の競合編集の整理は、不具合とは別の採用済み設計変更として加える。[任意の改善候補](release-follow-up-candidates.md)は、今回の実装・リリース完了条件に含めない。現行仕様の正本は [設定](../spec/runtime/settings.md)、[一覧](../spec/ui/table-view.md)、[取り込み・導入](../spec/library/drop-install.md)、[プレイリスト保存・出力](../spec/playlist/storage-and-export.md)、[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)を参照し、採用した変更は実装と同じコミットで反映する。

## 指摘の妥当性

本番の入口と保存・表示更新の接続を静的に照合した結果、以下の9件を修正対象として扱う。各項目の実装時に既存テストを拡張して再現・回帰を確認する。特にWPFのイベント順序と実Bindingの症状は、その接続を動かして確認する。

| 元項目 | 判断と現行コードの根拠 | 修正の境界 |
| --- | --- | --- |
| プレイリストF07：LR2連携時の新規保存失敗 | [保存サービス](../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)は未登録・空名draftから旧出力先を捕捉し、登録後に無条件で移行する。旧先が共通基点になり、[出力管理主体](../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs)の基点削除禁止に阻まれる。登録済みIDと未完了出力が残るため、再保存でも同じ処理に戻る。 | 初回出力と既存配置からの移行を分ける。基点保護を緩めず、同じ受理済み権限・登録済みIDで継続する。 |
| 設定R1：出力への採用で導入先が無効になる | [出力確定](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)は変更後の導入先所属を検査しない。管理出力を除いた確定BMS一覧と、[導入先の所属検査](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)が不整合になり、起動時の設定検査に通らなくなる。 | 有効な導入先を無効にする出力変更を、保存・XML・配置変更前に拒否する。競合セクションの排他だけでは解消しない。 |
| 一覧・導入R4：単体譜面を新規導入できない | [導入サービス](../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs)の宛先準備と自動命名がディレクトリ入力に限定される。単体入力・宛先未指定は明示宛先必須の分岐で失敗する。 | 新規宛先の決定と物理入力の種別を分ける。事前予約・衝突回避と実宛先を一致させ、単体の兄弟ファイルを巻き込まない。 |
| 設定R2：スコア確定でBMT移行が抜ける | [スコア確定](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)は派生BMTパスも保存するが、BMT後処理はroot変更時しか実行しない。同じrootの `tablepath` 変更では旧先を保持したpendingも消える。 | rootまたはBMTパス変更で既存の移行・URL整理・全表出力へ接続する。通常のplayer変更に全表出力を増やさない。 |
| プレイリストF05：正規表現の意味が変わる | [要求identity](../../BeMusicSeeker/ViewModels/Playlist/PlaylistRequestFactory.cs)は式全体を大文字化し、[局所更新](../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailVirtualView.cs)はその式で検索する。初回構築の生文字列と意味が異なり、`\d` と `\D` も同一要求になる。 | 検索式の意味を保存し、初回・合流判定・局所更新へ一貫して渡す。検索構文そのものや通常検索の大小文字無視は変えない。 |
| プレイリストF06：URLを解除できない | [画面側](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailEditing.cs)と[保存候補](../../BeMusicSeeker/Models/Playlist/BMSTableEntry.cs)がどちらも空欄を絶対URI検査で拒否する。既存setterはnullと対応するランタイム補完解除を表現できる。 | 両検査を同時に直す。空白だけなら未設定、非空の無効URIは従来どおり拒否する。 |
| プレイリストF04：子フォルダの選択が外れる | [フォルダノード生成](../../BeMusicSeeker/Models/Playlist/BMSTable.cs)は全ノードを置換し、[実ツリー](../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml)のItemsSourceへ渡す。[選択処理](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)と[workspaceのキー変更](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.TreeSelection.cs)には置換中の親選択抑制・新ノードへの同期が不足する。 | フォルダ構成の共通更新境界でツリー・workspace・詳細を揃える。初回選択ハンドラーの変更を修正の前提にしない。 |
| 設定R3：プリセットを続けて編集・削除できない | [プリセット保存](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)はeditorへ未正規化名を戻す一方、[ストア](../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryDisplayTarget.cs)は前後空白を除く。後続の名前照合が外れる。 | 確定する一件を正規化し、保存とeditorに同じ内容を使う。新しいIDや保存形式は不要。 |
| 設定R4：追加出力先名の空白入力 | [setter](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)の毎回の `Trim()` が [PropertyChangedのBinding](../../BeMusicSeeker/Views/Settings/Pages/PlaylistSettingsPage.xaml)と組み合わさる。確定時にも既存の正規化がある。 | 入力文字列をそのまま保持し、正規化は改名確定時だけにする。逐次入力を実Bindingで確認する。 |

WPF固有の根拠は .NET 10 の [TreeViewItem.OnItemsChanged](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/TreeViewItem.cs) と [BindingExpressionBase.EndSourceUpdate](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Data/BindingExpressionBase.cs)も照合した。前者は選択子コンテナが外れたResetで親を選択し得る。後者はsource更新後にsetterの値をtargetへ再転送する。実アプリのイベント接続・最終表示の確認は別途必要である。

## 採用済みの操作契約

以下は今回の利用者回答で確定した。元資料中の「採用する」という表現だけを合意根拠にしない。

### 出力設定とインストール先

LR2連携で、変更前に有効な現在の `BMSInstallDir` が今回の通常・追加・ROOT出力配置に取り込まれる変更は、副作用前に拒否する。同一または配下のパスを既存の正規化済み比較で検出し、別の確定BMSルートへ導入先を変更してから再選択するよう案内する。代替先の自動選択、新しい代替先候補・永続状態は追加しない。

拒否時は未受理の出力候補を戻し、既存のインストール先欄から修正・再操作できるようにする。未完了出力の修復中なら受理済みTarget・改名情報へ戻す。元から未完成・不正な初回設定を、無関係な出力項目の段階保存まで一律に拒否する条件にはしない。導入先ではないユーザールートの採用も維持する。

### 競合する設定候補

BMS、出力配置、LR2環境に競合する未確定編集を共存させない。BMS確定・取消、LR2詳細の確定・取消、出力処理の完了によって、関連する操作を再び許可する。ページ移動と無関係な通常項目保存を妨げず、受理済み処理を取消で破棄しない。

- BMSは候補と確定一覧の差、代替導入先候補、既存の保存・再読込み未完了情報から判定する。
- 出力は既存の処理中状態と `pendingOutputChange` を使う。選択・追加・削除・改名は一操作で確定し、未受理候補は終端で戻す。改名欄の入力文字列だけでは占有しない。
- LR2は `HasEnvironmentChanges` と既存の遷移状態から判定する。beatoraja・BMTへ排他を広げない。
- UIの可否と実入口に同じ条件を使い、ピッカーを開く前・戻り値を採用する前・候補生成前に検査する。禁止操作を予約しない。初期表示や確定後同期などの内部更新は利用者入力と区別する。
- 未受理候補の復元と、受理済み処理の失敗後の明示修復を分ける。既存pendingの修復Target・改名・完了フラグは受付・登録検証の成功前に書き換えない。

出力公開後は確定BMS一覧へ同期する。未確定BMS候補の捕捉と `RebaseBmsRootCandidatesAfterOutputPublication` の差分再適用を削除する。未受理出力候補用の `HasNormalOutputChanges` 等3プロパティ、`retry:output-normal`・`retry:output-root`・`retry:output-additional` と対応分岐を削除し、受理済み未完了処理の `retry:output` は維持する。

LR2 XMLの単一正本、保存済み値と公開値の分離、限定公開、媒体ごとの部分成功、成立済み改名・移行情報は引き続き必要であり削除しない。初回設定もセクションを順に確定して完了できるようにする。

### 選択フォルダが消える場合

プレイリストの選択フォルダ削除では、内容を移した空名フォルダを選ぶ。そのノードが残らなければ表本体を選び、ツリーとworkspaceを揃える。改名・接頭辞変更は確定した対応へ追従し、別フォルダの更新・特殊ノード選択を保つ。更新中の新しい明示選択を古い復元で上書きしない。

## 着手順とコミット単位

まず保存情報の消失、続いて作成・起動継続・導入の停止を扱い、その後に設計整理とP2修正を行う。表は残作業の直列順であり、同じファイルを触ることだけで独立した修正を一コミットへまとめない。各単位に必要な実装・回帰テスト・仕様・翻訳を含め、検証・レビュー後に `dev` へコミットする。

| 順 | コミットの主題 | 項目・主な変更先 | 完了を判定する代表ケース |
| --- | --- | --- | --- |
| 3 | `fix: 新規プレイリストの初回出力を移行から分離する` | プレイリストF07。`PlaylistPropertySaveService`、`BMSPlaylist`、既存出力入口。 | LR2有効の実draft作成→保存が成功し、通常・追加・ROOTを扱える。共通基点と兄弟表のファイル・LR2行を保つ。実出力／検索ルート保存失敗後も同じIDで継続し、既存表の旧先整理を維持する。 |
| 4 | `fix: 出力先の変更によるインストール先の無効化を防ぐ` | 設定R1。出力確定前検査、未受理候補の局所復元、表示リソース。 | 通常・追加・ROOTの同一先と包含親を副作用0で拒否する。別BMSルートへ導入先を変更後は採用に成功し、初期化検査に通る。修復時の受理済み候補を保つ。 |
| 5 | `fix: 単体譜面の新規インストールを成立させる` | 一覧・導入R4。宛先準備、物理計画、変更セッションへの反映。 | 宛先未指定のBMS/BMSON、単体・フォルダ混在、同名宛先競合を実配置・DB・所持・新規／保留・install表で確認する。兄弟ファイルは保持し、明示宛先導入も保つ。 |
| 6 | `refactor: 競合する設定セクションの未確定編集を排他する` | 採用済み設計整理。設定ViewModel、Windowと各設定ページ、仕様・翻訳。4の候補復元を再利用する。 | 候補生成前の双方向拒否、確定・取消・完了後の解除、初回設定、出力確認取消・受付拒否・修復拒否からの再操作を確認する。旧差分再適用と候補再確定UIを削除する。 |
| 7 | `fix: スコア設定の確定時もBMT出力先を移行する` | 設定R2。`CommitScoreSettingsAsync` の旧新比較と既存出力への接続。 | 同じrootでtablepathをA→Bへ変え、score使用OFFまたはplayer変更から新先出力・旧先／URL整理の終端まで待つ。管理外データと未確定BMTオプションを保つ。 |
| 8 | `fix: プレイリスト検索式の意味を更新間で維持する` | プレイリストF05。要求identity、初回・局所検索、既存期待値。 | `\d`↔`\D`、`\s`↔`\S`、`\w`↔`\W`を区別する。無関係なMEMO編集で集合が変わらず、関係属性の編集には同じ式で加入・離脱する。 |
| 9 | `fix: プレイリストのURLを空欄で解除できるようにする` | プレイリストF06。画面側検査と `BMSTableEntry.ApplyEditDraft`。 | URL1/2の有効値→空欄を正本・表示・DBで確認する。旧ランタイム補完が保存直後に復活せず、他方の有効URL材料化、無効URI拒否、Escape、外部同期制限を保つ。 |
| 10 | `fix: フォルダ構成更新後もツリーの選択を同期する` | プレイリストF04。フォルダ更新境界、workspace選択、MainWindowの表示同期。 | 実ツリーの子選択→改名／別フォルダ更新／削除／接頭辞変更後、ツリー・workspace・詳細が一致する。特殊ノード、新しい明示選択、通常の初回選択・再クリック・キーボードを保つ。 |
| 11 | `fix: 履歴表示プリセットの確定名を保存値と揃える` | 設定R3。プリセット確定、必要な既存正規化の再利用。 | ` 練習 ` を保存後、画面を開き直さず対象変更・改名・削除でき、JSONと表示が同じ一件を指す。取消・空名・重複名の検証を保つ。 |
| 12 | `fix: 追加出力先の改名入力で途中の空白を保持する` | 設定R4。入力setterと実Bindingの回帰。 | `My`→`My `→`My Charts` を保持する。前後空白は確定時だけ除き、空白名を拒否し、改名後の出力先・保存値・表参照を揃える。 |

4は6に依存せず安全な変更として完成させる。6で同じ未受理候補復元を全出力入口へ接続する。元資料の「セクション排他とR1を一括」に対し、独立して再現・検証できる不整合防止と操作制限の変更を分ける。

プレイリストのサマリー・詳細を識別追従へ一律に変えるF01は未採用であり、共通部品を扱うことを理由にその挙動まで広げない。

3と4・6は同じLR2出力周辺、4・6・7は同じ設定確定ファイル、6・11・12は同じ設定ViewModelを扱う。書込みは原則直列とし、独立して進める場合も許可パスと共有依存を分ける。新規作成から詳細編集・ドロップまでの接続も代表フローとして確認する。

## 検証と実装開始時の引継ぎ

本書は受入条件と分割の方針であり、[独立したテスト設計](../spec/development/agent-workflow.md#テスト設計の要否)や[実装直前の引継ぎ点検](../spec/development/agent-workflow.md#引継ぎの点検)の代替ではない。実装の指示後、確定した契約から不足する恒久テストを設計し、承認・点検してから実装する。

既存テストは以下を起点とし、同じ責務のケースを拡張する。実装から正解を作らず、上表の結果を判定する。Windowの条件網羅や新しい汎用テスト基盤は増やさない。

| 領域 | 既存の検証先と補う保証 |
| --- | --- |
| 単体新規導入 | `BmsLibraryPackageInstallServiceTests` の新規保留導入・宛先準備・単体物理確定。既存の単体成功例は明示宛先ありなので、未指定新規を追加する。 |
| プレイリスト作成・ドロップ | `PlaylistWorkspaceActionWorkflowTests`、`MainWindowPlaylistWorkspaceWpfTests`。既存のdraft再保存テストはLR2無効のBMT失敗であり、正常なLR2初回保存を補う。 |
| 検索・URL・ツリー | `PlaylistViewPipelineTests`、`PlaylistWorkspaceDetailRefreshTests`、`PlaylistCatalogViewChangeTests`、`PlaylistUrlCompletionTests`、`PlaylistEntryCommitTests` と代表WPF接続。大文字化を同一性の正解とした既存期待値を見直す。ツリーはキー変更単体だけで合格にしない。 |
| 設定 | `SettingDialogOutputConfirmationTests`、`SettingDialogLr2OwnerTests`、`SettingDialogEnvironmentConfirmationTests`、`SettingDialogEditCompletionTests`、`SettingDialogCustomFolderOutputBaseTests`、`SettingsWindowCompiledBehaviorTests`。競合共存を前提としたテストは拒否・解除へ置換し、旧管理登録を復活させない保証を移す。実保存・XML・出力・台帳・URL、正規化後の連続操作、実Bindingを分担する。 |

新しい通知文言は `Resources.resx`、`Resources.cs` と6言語辞書を同じ単位で揃える。各単位で必要な機能仕様と[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md)を更新し、不要になった説明・経路を削除する。

反復は標準入口 `scripts/verify-refactor.ps1` の対象を絞った `Quick`、通常開発のコミット・統合前は `Functional`、最終公開候補は `Full` とする。[実行範囲と実行回数](../spec/development/testing.md#実行範囲の選択)に従い、同じ最終版の成功済み検証を理由なく繰り返さない。自動WPF検証と利用者環境での実機確認を区別し、実機確認を必須条件にしない。

残る単位3〜7は保存・物理変更・受付・継続の保証に関わるため、重大変更として `critical-static-review` を使う。8・11・12は局所的な意味保存・正規化の修正なら `repo-static-review`、9は保存とランタイム補完の契約、10は通知順序・選択revisionを扱うため `critical-static-review` を予定する。各単位の最終差分で[重大変更判定](../spec/development/agent-workflow.md#重大変更の判定)をやり直し、影響が広がれば切り替える。いずれも凍結・予備レビュー後に独立した最終レビューを行う。

## 完了条件と対象外

- 残る9件と採用済み設計整理の受入条件を満たし、コード・保存値・出力・表示を必要な境界で確認する。未実施・失敗を成功扱いにしない。
- 新規作成後の通常編集、ドロップ、フォルダ更新と、出力拒否後の導入先変更・再操作を含む領域間の接続を確認する。
- 全件走査・仮想行の全実体化、新しい永続フラグ、意味の変わるフォールバック、自動再試行を追加しない。
- コミット、統合、公開、バージョン更新は別途指示された範囲でのみ行う。
- 完了した項目は必要な契約・判断を正本へ移して本書から除く。全完了時は本書を削除し、[計画一覧](README.md)を更新する。
