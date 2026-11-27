# 未所持行の本体候補

## 目的と適用範囲

プレイリスト詳細の未所持行について、その曲の本体フォルダを所持していると推定できるとき、URL1列の文字色と右クリックメニューで示します。差分（URL2）だけを入手すればよいと分かるようにする、読取り専用の目印です。

次は対象外です。

- 題名の類似照合、`org_md5`、URLの一致による推定。
- 絞込み、並べ替え、件数の集計、一括取込みへの利用。
- DBへの保存と設定項目。
- 候補フォルダが外部で消えていた場合の新しい扱い。開く操作は既存のエクスプローラー起動の契約に従います（[パス長とI/O](../core/path-length-and-io.md)）。
- 同じ物理フォルダが大文字小文字の違う表記で混在する場合の統合。表記ごとに別のフォルダとして扱います。

## 用語

[共通用語集](../glossary.md)に従います。

- **本体候補**: 項目の題名・作者に合う所持譜面の、直接の親フォルダです。
- **比較キー**: 導入先の推定と同じ正規化で作る、題名・作者の小文字の比較用の値です（[導入先の推定](../library/install-estimation.md)）。

## 仕様

### 照合

- 項目側の入力は、プレイリスト項目が持つ題名と作者です。表示のために譜面情報で補った値は使いません。
- 所持譜面側の入力は、導入先の推定と同じ射影です。BMSは原題と元の作者、bmsonは原題と現在の作者を使います。BMSの表示用の題名・作者や、bmsonの元の作者だけが一致しても使いません。
- 両側とも、題名・作者の比較キーを使います。題名の比較キーが空の譜面・項目は、照合の対象外です。項目側と索引側の比較キーの定義は、題名と作者それぞれの正規化の合成として一か所にまとめ、ずらしません。
- 候補の条件は、題名の比較キーが完全一致し、かつ作者の比較キーが次のいずれかを満たすことです。
  - 完全一致する。
  - 項目側の作者の比較キーが空である（譜面側の作者によらない）。
  - 作者の比較キー同士の文字2-gramのDice係数が0.50以上である（ちょうど0.50を含む）。

  項目側が空でなく、譜面側の作者の比較キーが空なら成立しません。
- 候補は、一致した所持譜面の直接の親フォルダです。同じフォルダの一致譜面は一件にまとめ、一致した譜面数を合算します。
- 並びは、一致した譜面数の降順で、同数ならフォルダパスの大文字小文字を区別しない昇順です。最大5件を返します。
- 所持行には候補を出しません。

### 索引

`CatalogOwnedCollectionOwner` が、所持譜面の現在値から作る派生索引を所有します。形は「題名の比較キー →（作者の比較キー →（親フォルダ → 所属数））」の不変の構造です。DBへ保存せず、`CaptureSnapshot` の結果と確定結果へ含めません。

- 読取りは排他を取りません。不変の索引への参照を一度読むだけです。未構築の間は候補なしで、例外・警告にしません。
- 書込みは、既存の集合排他の下で直列に行います。新しいロックとロック順を増やしません。
- 親フォルダのキーは、保存パスを物理パスへ正規化した直接の親ディレクトリから、末尾の区切りを除いたものです。作り方は、導入先の推定が使うディレクトリ別のプロフィールの失効と共通の補助にまとめます。
- 差分を反映する条件は、そのプロフィールの失効条件と同じ一つの述語です。追加・削除と、パス・原題・作者（BMSは元の作者、bmsonは現在の作者）の変更が該当します。スコア・健康度・文字コード・ハッシュだけの変更、BMSの表示用の作者だけの変更、bmsonの元の作者だけの変更では内容が変わりません。
- 差分は、確定区間の中で、確定結果を登録する前に反映します。途中で例外になったときは、索引を破棄して未構築へ戻してから、既存の流れで再送出します。確定結果の登録後に索引を触りません。変更なしの早期終了では更新しません。
- 差分の作成では、題名・作者の正規化の結果を、その確定の中だけの対応表（生文字列から比較キー）で共有します。変更前後で題名・作者の入力が同じ譜面（パスだけの変更）は、前後で同じキーを使い、同じ生文字列を繰り返し正規化しません。対応表は確定をまたいで保持せず、索引にも持たせません。
- 差分を反映した内容は、変更後の所持譜面から全構築した内容と一致します。最後の一致譜面が消えたら、フォルダ・作者・題名の空の入れ物を残しません。
- 全置換では、索引を破棄し、無効化の版を進めます。未構築の索引は、変更のためだけには構築しません。

### 全構築

全構築は、起動後の索引準備の仕事 `playlist_library_index_prewarm` の中で、参照解決索引の準備に続けて実行します。新しいレーン・予約・表示名は作りません（[起動](../runtime/startup.md)）。

1. 集合排他の一回の保持で、所持譜面のビューと無効化の版を同時に捕捉し、構築中の確定の受取りを始めます。
2. 排他の外で、生の題名と生の作者を別々に重複除去し、並列に正規化します。出力は添字付きの配列へ書き、辞書は並列の処理の後に単一のスレッドで作ります。並列度は CPU数−1（最小1）で、取消を接続します。
3. 排他の外で、譜面ごとに集計します。同じ親フォルダを表す文字列は、この構築の中で一つのインスタンスへ集約し、譜面ごと・（題名, 作者, フォルダ）の組ごとに別の文字列を残しません。
4. 設置時に無効化の版を照合し、違えば結果を破棄します。構築中に局所の確定があれば、受け取った差分を設置前に反映し、設置される内容を設置時点の所持譜面からの全構築と一致させます。
5. 設置して排他を解放した後に、画面へ再評価を依頼します。

再試行は行わず、取消されたら未構築のまま終わります。結果は並列度によりません。本番で全置換が起きる入口は、保存済みの目録の読込みと、ファイル走査の結果の置換です。どちらも、起動・全再初期化・差分再読込の操作の中で行います。これらの操作が成功して完了を公開すると、共通の完了処理が、スコアのみの再読込を除く操作の完了ごとに、この仕事を予約します（差分が0件の再読込と表の再読込でも予約し、構築済みなら何も構築しません）。ライブラリを作り直してデータ元が交換された場合は、新しいデータ元で予約します。したがって、全置換の後は、その操作の完了までの間だけ候補がなく、完了後の準備で再構築します。

予約されないのは、操作が失敗・終了要求で終わった場合です。全置換の後に操作が失敗すると、次に成功した操作の完了まで候補がありません。また、同じデータ元の準備が実行中の間に全置換と完了が重なると、予約は実行中の準備へ合流し、その準備は構築結果を破棄します。この場合も、次の操作の完了まで候補がありません。

### 画面

- 行の判定 `PlaylistDetailRow.HasBaseCandidateUrl1Highlight` は、未所持で、URL2が絶対URIで、候補が1件以上のときだけ真です。URL2は実行時のURL補完を反映した実効値で、URL2列の取得アイコン表示と同じ値です。安い判定を先に行い、候補は現在の索引へ問い合わせます。行には項目側の比較キーだけを保持し、候補の結果は保持しません。
- URL1列は、判定が真のときだけテーマの `Table.BaseCandidateTextBrush` の色で描き、それ以外は従来の色です。列幅が40のアイコン表示でも41以上の文字表示でも同じです。判定が真のURL1は、行が選択されていても、そのセルが現在セルでも、専用色で描きます（選択時・現在セルの文字色へ置き換えません）。背景は行の選択・現在セルの規則のままです。判定が偽のURL1と他の列の色は従来どおりです（[文字色の規則](../ui/table-view.md)）。
- 再評価のきっかけは二つです。一つは、ライブラリ変更の適用の最後に、その変更に差分の反映の条件へ該当するものがあるとき。もう一つは、索引の設置の完了を画面へ渡したときです。どちらも、現在のビューの実体化済みの未所持行へ変更通知を出すだけです。元の行・依存・確定結果・要求の識別には何も加えません。行ごとの厳密な時機は保証しません。
- 未所持行の右クリックメニューに「本体候補」のサブメニューを置きます。メニューを開いた行の候補（最大5件）を並べ、見出しはフォルダのフルパスです。`_` をアクセスキーにせずそのまま表示します（[メニューの見出し](../ui/table-view.md#見出しとメニュー)）。URL2の有無によらず、候補が0件（未構築を含む）なら表示しません。
- 項目を選ぶと、そのフォルダをエクスプローラーで開きます。ライブラリのフォルダツリーの「エクスプローラーで開く」と同じく、構成が注入するフォルダの存在確認と起動の関数を使います。呼出し前に存在を確認し、なければ何もせず、起動結果は破棄します。
- 候補の算出は `PlaylistWorkspaceViewModel` が持ち、`MainWindow` には置きません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 照合の規則、並び、上限、正規化の適用 | [`CatalogBaseCandidateIndex`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogBaseCandidateIndex.cs)、[`InstallEstimationMetadataNormalizer.BuildPairKey`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/InstallEstimationMetadataProfile.cs) | [`CatalogBaseCandidateIndexTests`](../../../tests/BeMusicSeeker.Core.Tests/Catalog/CatalogBaseCandidateIndexTests.cs) の `FullBuild_AppliesTitleAndArtistMatchingRules` は、題名・作者の各条件、Dice係数の境界、BMS・bmsonの射影、親フォルダの集約と並びをリテラルの期待値で確認する。 |
| 差分の反映と全構築との一致、無関係な変更 | [`CatalogOwnedCollectionOwner.BaseCandidates`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.BaseCandidates.cs)、[`CatalogMetadataDependency`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMetadataDependency.cs) | `LocalCommits_MatchFullBuildAfterEachDependentChange`（変更の種類ごと）は、各確定の後に全構築と内容・入れ物の数が一致し、期待する候補とも一致することを確認する。`UnrelatedChanges_KeepContents` は索引の参照が変わらないことを確認する。 |
| 全置換と未構築、構築中の確定・全置換・取消 | 同上 | `FullReplacement_DiscardsAndNextPrepareRebuilds`、`ColdIndex_LocalCommitDoesNotBuild`、`CommitDuringBuild_InstalledContentMatchesCurrentFullBuild`、`FullReplacementDuringBuild_DiscardsResult`、`CancelledBuild_RemainsCold`、`ParallelAndSequentialBuilds_AreEqual`、`SingleChange_WorkDoesNotScaleWithBackground`（背景16件・128件）。 |
| 索引準備の仕事の中での構築と取消 | [`PlaylistWorkspaceViewModel.LibraryIndexPrewarm`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.LibraryIndexPrewarm.cs) | [`PlaylistWorkspaceDetailRefreshTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceDetailRefreshTests.cs) の `PlaylistLibraryIndexPrewarm_BuildsBaseCandidatesAfterResolveIndexInSameWork` と `PlaylistLibraryIndexPrewarm_CancelledWorkDoesNotBuildBaseCandidates`。 |
| 行の判定、メニューの候補、確定後の収束 | [`PlaylistDetailRow`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailRow.cs)、[`PlaylistDetailVirtualView`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailVirtualView.cs)、[`PlaylistWorkspaceViewModel.DetailChanges`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailChanges.cs) | [`PlaylistDetailBaseCandidateTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistDetailBaseCandidateTests.cs) の `Url1Highlight_RequiresUnownedAbsoluteUrl2AndCandidate`、`MenuCandidates_UseContextRowIndependentOfUrl2`、`CatalogCommits_ConvergeRealizedUnownedRows`（背景16件・128件）。 |
| URL1列の文字色 | [`CustomTableColumnFactory`](../../../BeMusicSeeker/Views/CustomTable/CustomTableColumn.cs)、[`CustomTablePalette`](../../../BeMusicSeeker/Views/CustomTable/CustomTablePalette.cs) | [`CustomTableColumnFactoryTests`](../../../tests/BeMusicSeeker.Core.Tests/CustomTable/CustomTableColumnFactoryTests.cs) の `CreateMainColumns_Url1ForegroundFollowsBaseCandidateHighlight` は、幅40・41以上で判定の真偽に従う色を色の値で確認する。[`CustomTableRenderingTests`](../../../tests/BeMusicSeeker.Windows.Tests/CustomTable/CustomTableRenderingTests.cs) の `PlaylistUrl1BaseCandidateColor_IsKeptOverSelectionAndCurrentCell` は、幅40・200で、未選択・行の選択・現在セルが各セルのとき、候補あり行のURL1が専用色と規則どおりの背景で描かれ、候補なし行のURL1とURL2が従来の色であることを物理画素で確認する。変更通知による行の再描画は、既存の行の通知の検証が分担する。 |
| サブメニューの表示と、候補のフォルダを開く操作 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[`MainWindowPlaylistBaseCandidateTerminal`](../../../BeMusicSeeker/Views/MainWindow/MainWindowPlaylistWorkspaceTerminals.cs) | [`MainWindowSelectedChartContextMenuWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowSelectedChartContextMenuWpfTests.cs) の `MissingPlaylistBaseCandidateSubmenu_ListsCandidatesAndOpensClickedFolder`（`_` を含むパス）と `MissingPlaylistBaseCandidateSubmenu_HiddenWithoutCandidates`。 |
| 表示文言の6言語の整合 | `Playlist_base_candidates`（[`Resources.resx`](../../../BeMusicSeeker/Properties/Resources.resx)と `lang/`） | 既存の `LocalizationResourceParityTests`。 |

## 関連資料

[データと索引](../core/data-and-indexes.md)、[導入先の推定](../library/install-estimation.md)、[操作の競合ポリシー](../core/operation-concurrency-policy.md)、[起動](../runtime/startup.md)、[一覧の表示](../ui/table-view.md)、[一覧の列](../ui/table-columns.md)、[URL・外部APIからの取得と導入引渡し](downloads.md)を参照します。
