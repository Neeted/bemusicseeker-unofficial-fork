# GitHubのスター依頼

## 目的と適用範囲

しばらく使い続けた利用者へ、このフォーク版のGitHubリポジトリにスターを付けるよう一度だけ依頼する画面です。起動の数え方、表示する条件と時機、画面の配置、選択の保存、失敗の扱いを定めます。

依頼は任意の案内です。起動の成否、操作の受付、他の案内の表示を変えません。スターが実際に付いたかは確認しません。

## 用語

[共通用語集](../glossary.md)を参照します。

| 用語 | 意味 |
| --- | --- |
| スター依頼 | 本書の画面と、その表示・保存の一連の処理。 |
| 数える起動 | 通常起動の必須初期化が成功し、完了を公開して操作を解禁した起動（`StartupInitializationResult.IsCompletedSuccessfully` が真で、操作種別が `Startup`）。全再初期化・スコアだけの再読込み・差分の再読込みは数えない。 |
| 依頼の終了 | 利用者がスターを付けに行った、または今後表示しないを選んだ状態。以後は表示しない。 |
| 次の表示予定 | この日時以降でなければ表示しない、UTCの日時。 |

## 仕様

### 保存する項目

`config/user.config` に次の4項目を保存します。保存は[設定ファイルの保存と回復](../runtime/settings-persistence.md#共通の保存と公開)の `Settings.Commit` を使います。項目がない既存の設定では既定値から始まるため、移行処理は持ちません。

| 項目 | 型と既定値 | 意味 |
| --- | --- | --- |
| `GitHubStarPromptState` | 文字列、既定は空 | 空は未終了。`Starred` はスターを付けに行った、`Declined` は今後表示しないを選んだ。 |
| `GitHubStarPromptStartupCount` | 整数、既定は0 | 前回の基準時点から数えた起動の回数。表示に必要な回数で頭打ちにする。 |
| `GitHubStarPromptNextEligibleUtc` | 文字列、既定は空 | 次の表示予定。InvariantCultureの往復形式（`O`）でUTCを保存する。 |
| `GitHubStarPromptShownCount` | 整数、既定は0 | 画面を表示した回数。 |

読取りの扱いは次のとおりです。

- `GitHubStarPromptState` が空・`Starred`・`Declined` 以外の値なら、依頼の終了として扱い、その値を書き換えません。新しい版が追加した値を古い版で消さないためです。
- `GitHubStarPromptNextEligibleUtc` が空か解釈できなければ、未設定として扱います。
- 回数の項目が負か、整数として読めなければ0として扱います。

### 起動の数え方

未終了で表示回数が上限未満のとき、数える起動ごとに次を行い、変わった項目だけを保存します。

1. 次の表示予定が未設定なら、現在時刻の14日後を設定します。
2. 起動の回数が10未満なら1を加えます。10に達した後は加えず、保存もしません。

依頼の終了後と表示回数の上限に達した後は、読み取るだけで保存しません。数える起動に当たらない起動は、回数も予定も変えません。

### 表示の条件

次をすべて満たす起動で、一度だけ表示します。条件を満たさない起動では見送り、後で表示するための順番待ちを残しません。

| 条件 | 内容 |
| --- | --- |
| 保存値 | 未終了、表示回数が3未満、起動の回数が10以上、現在時刻が次の表示予定以降。 |
| 起動の結果 | 数える起動で、LR2の失敗（`StartupRequiredInitializationResult.Lr2Failure`）がなく、LR2ルート未設定の警告（`ShowRootWarning`）を出していない。 |
| 更新確認 | 起動時の更新確認が「更新なし」（`StartupUpdateWorkflowOutcome.NoUpdate`）で終わった。更新を案内した起動、確認に失敗した起動、取り消した起動では表示しない。 |
| 回数の保存 | この起動の保存値の読取りと、回数・予定の保存が失敗していない。 |
| 画面の状態 | メイン画面が読込み済み・表示中で、終了を受け付けておらず、Dispatcherが停止していない。 |
| 実行形態 | 検証起動（`ApplicationRuntimeOptions.TestSession` がnullでない）ではない。検証起動では保存値を読み書きせず、何もしない。構成（`ApplicationComposition`）の既定は無効で、本番の起動入口（`App`）が検証起動でない場合だけ有効にする。 |

初回起動は、起動の回数と次の表示予定の条件により表示されません。

### 表示の時機

起動の完了を待ってから表示します。

1. 通常起動の初期化の実Task（`ShellActivationWorkflowOwner.StartupCompletion`）の終端を待ちます。この終端は、初回の完了案内や起動時の警告の表示を終えた後です（[初回の完了案内](../runtime/startup.md#初回の完了案内)）。
2. 数える起動なら、起動の回数と次の表示予定を保存します。
3. 起動時の更新確認の終端（`StartupUpdateWorkflowOwner.WaitForTerminalAsync`）を待ち、終端の種類を確認します。
4. 表示の条件を確認してから、表示を依頼します。

初期化の終端と更新確認の終端は、どちらが先でも同じ結果です。スター依頼は起動の結果・解禁・追加処理を待たせず、それらの完了条件にも含まれません。

表示できるかの確認と表示の依頼は、UIスレッドで行います。昇格したプロセスの警告（描画後のアイドル時に開始）とは順序を取りません。表示の窓口は画面を直列化しないため、両方が同じ起動で表示される場合は、後から届いた画面が表示中の画面を所有元として、その上に重ねて表示されます。

### 画面

更新案内と同じく、共通の表示窓口の `ShowWindowAsync` でメイン画面を所有元とするモーダルの子ウィンドウとして表示します。

上から次の順に、中央揃えで並べます。

| 部品 | 内容 |
| --- | --- |
| 星のアイコン | アイコンフォントの星の輪郭（`App.IconGlyph.Star`）。強調色。 |
| 見出し | 「アプリを気に入っていただけましたか？」 |
| 本文 | このフォーク版のリポジトリへのスターで応援してほしいこと。 |
| 主要の操作 | 「GitHubでスターを付ける」。幅いっぱいの主要ボタンで、星のアイコンを添える。既定のボタン。 |
| 二つの操作 | 「あとで」と「今後表示しない」を同じ幅で横に並べる。「あとで」は取消のボタン。 |

寸法と余白は既存の設定画面・ダイアログの値を使います。

- 本文の余白は `App.Layout.BodyPadding`、本文と操作の間隔は `App.Layout.SectionMargin` を使います。
- 見出しは設定の節の見出しと同じ文字サイズ17・SemiBoldです。
- 幅は初回の言語選択と同じ520で、高さは内容に合わせます。サイズ変更とタスクバーへの表示はしません。
- 題名部にはアプリ名を表示し、閉じる操作は標準の題名部のボタンを使います。
- 文言はすべて表示リソースから取得し、6言語をそろえます。

### 選択の反映

表示が成立した場合は、選択にかかわらず表示回数に1を加えます。

| 操作 | 結果 |
| --- | --- |
| GitHubでスターを付ける | `https://github.com/Neeted/bemusicseeker-unofficial-fork` を既定のブラウザで開く。開けた場合は依頼を `Starred` で終了する。 |
| あとで | 起動の回数を0へ戻し、次の表示予定を現在時刻の30日後にする。 |
| 今後表示しない | 依頼を `Declined` で終了する。 |
| 題名部の閉じる、Esc | 「あとで」と同じ。 |

表示回数が上限の3に達した後は、未終了のままでも表示しません。

ブラウザで開けなかった場合は、元の原因を診断へ残し、画面を所有元として失敗を一回通知します。その後、画面を閉じて「あとで」と同じに扱います。通知自身の失敗は診断へ残すだけで、再通知しません。

### 失敗と終了の扱い

スター依頼の失敗は、すべて局所の診断に留めます。利用者への通知（ブラウザで開けなかった場合を除く）、起動の結果の変更、再試行はしません。

| 状況 | 扱い |
| --- | --- |
| 保存値の読取りの失敗、起動の回数・予定の保存の失敗 | 診断へ残し、この起動では保存も表示もしない。 |
| 表示が成立しなかった（終了中、画面が表示できる状態でない、UIスレッドへ渡せない） | 診断なしで見送り、保存値を変えない。 |
| 表示の失敗（所有元がない、画面の生成・表示の例外、表示先がない） | 診断へ残し、保存値を変えない。 |
| 選択の保存の失敗 | 診断へ残す。この起動では再表示しない。次の起動では保存済みの値で判断する。 |
| 初期化の失敗・取消、更新確認の待機の失敗 | 表示しない。初期化と更新確認の失敗の通知は、それぞれの所有者が行う。 |

表示の直前と、表示の窓口へ渡す直前に、画面の状態を確認します。終了を受け付けた後は表示しません。表示中のモーダル画面はメイン画面の閉じる操作を受け付けないため、スター依頼は終了処理の受付を持ちません。

### 設定画面のリンク

設定画面の「このアプリについて」のリンクの節に、同じURLを開く「GitHubでスターを付ける」ボタンを置きます。いつでも利用でき、スター依頼の保存値は変えません。起動の失敗は既存のリンクと同じく一回通知します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 起動の数え方、表示の条件（保存値）、選択の反映、合意した流れ（初回の10回・14日、あとでの10回・30日、表示3回まで） | [`GitHubStarPromptPolicy`](../../../BeMusicSeeker/Models/Settings/GitHubStarPromptPolicy.cs) | [`GitHubStarPromptPolicyTests`](../../../tests/BeMusicSeeker.Core.Tests/Settings/GitHubStarPromptPolicyTests.cs): 予定の一度だけの設定と回数の頭打ち、終了・上限での不変、境界を含む表示可否、選択ごとの値、時刻の列による通しの流れ。 |
| 保存する項目の既定値・保存形式・不正値・未知の状態の保持・保存失敗の伝播 | [`SettingsGitHubStarPromptStore`](../../../BeMusicSeeker/Models/Settings/SettingsGitHubStarPromptStore.cs)、[`Settings`](../../../BeMusicSeeker/Properties/Settings.cs) | [`GitHubStarPromptStoreTests`](../../../tests/BeMusicSeeker.Core.Tests/Settings/GitHubStarPromptStoreTests.cs): 実際のproviderで、欠損時の既定、異なる文化圏での往復とXMLの値、不正値・未知の状態を読んでも書き換えないこと、読取り専用ファイルでの例外。 |
| 表示の時機、起動の結果と更新確認の条件、検証起動、失敗の扱い、UIスレッドでの確認 | [`GitHubStarPromptWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/GitHubStarPromptWorkflowOwner.cs) | [`GitHubStarPromptWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Startup/GitHubStarPromptWorkflowOwnerTests.cs): 計数の保存が更新確認の終端より先なこと、終端の順序によらない結果、表示しない条件ごとの計数の保存と依頼なし、数えない起動、無効時にストアへ触れないこと、保存・表示の失敗。 |
| 起動との接続（初期化の後に開始し、起動のTaskを待たせない） | [`ShellActivationWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/ShellActivationWorkflowOwner.cs) | [`ShellActivationWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/ShellActivationWorkflowOwnerTests.cs): 依頼を保留したまま起動のTaskと `StartupCompletion` が完了すること。 |
| 画面の選択の結果、ブラウザで開けない場合の通知、表示部品の役割 | [`GitHubStarPromptDialog`](../../../BeMusicSeeker/Views/Dialogs/GitHubStarPromptDialog.xaml.cs) | [`GitHubStarPromptDialogTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/GitHubStarPromptDialogTests.cs): スター・あとで・今後表示しない・×の選択と、スターだけが開くURL、成功時に通知しないこと、開けない場合の一回の通知（通知の失敗を含む）と通知中の画面の維持。Enter・Esc はOSのキー入力ではなく、スターの `IsDefault` とあとでの `IsCancel` の属性で確認し、ボタンは自動化の `Invoke` で押す。[`DialogPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs): 主要・通常の操作の共通スタイルと、内容が画面内に収まること。 |
| メイン画面での表示・結果の写像、構成の有効化、配置と寸法、設定画面のリンク | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[`ApplicationComposition`](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs)、[`App`](../../../BeMusicSeeker/App.cs)、[`AboutSettingsPage`](../../../BeMusicSeeker/Views/Settings/Pages/AboutSettingsPage.xaml) | 静的確認（更新案内と同じ表示経路、既存のリンクと同じ処理）。 |
| 表示文言の6言語 | `Resources.resx`、`lang/` | [`LocalizationResourceParityTests`](../../../tests/BeMusicSeeker.Core.Tests/Localization/LocalizationResourceParityTests.cs) |

## 関連資料

[起動・初期化・再読込み](../runtime/startup.md)、[ダイアログの表示境界](dialogs.md)、[外観と共通の表示部品](appearance.md)、[設定ファイルの保存と回復](../runtime/settings-persistence.md)、[ポータブル自動更新](../integration/portable-update.md)。
