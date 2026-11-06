# 譜面プロパティ

## 目的と適用範囲

選択したローカル譜面のリソース不足の内訳を、必要時に読取り専用で確認します。健康度の再設定やファイルの変更は行いません。

## 用語

[共通用語集](../glossary.md)を参照します。

| 用語 | 意味 |
| --- | --- |
| 定義 | WAV/BMPなどの定義一件。 |
| 未定義参照番号 | 使用参照に現れるが、対応するWAV/BMPまたはBMSONのBGA定義がない定義番号。 |

## 仕様

### 対象と表示

一覧の右クリックの「譜面プロパティ」から開きます。実ファイルのある譜面一件だけを対象とし、複数選択・未所持・ファイル欠損では利用できません。保留パッケージ内の実ファイルも対象です。譜面本体は元の実ファイルから読み、リソースの存在確認は[保留一覧と同じ配置](../library/warnings.md#リソース状態の評価)に対して行います。メニューの表示、ホバー、ツールチップ、一覧の値取得では解析しません。

実行ごとに、サイズ変更可能な非モーダルの `ThemedWindow` を新しく開きます。同じ譜面も複数同時に表示でき、読取り中もメイン画面を操作できます。初期配置はメイン画面基準の中央とし、表示後は親ウィンドウを持たず、タスクバーに個別表示します。メイン画面との前後関係は固定しません。画面には次を表示します。

- タイトル、アーティスト、実パス
- リソースの確認基準（設定先・先頭候補・現在の配置）と確認先パス
- 次の三分類

外観は[共通部品と配色](appearance.md)と[ダイアログの表示境界](dialogs.md)に従います。

| 分類 | 表示内容 |
| --- | --- |
| リソースの問題 | 定義元・番号・原文・論理行番号と分類理由。必要参照と非空の任意画像の不足・不正指定、およびCP932で解釈した場合の独立した問題を原文順に示す。CP932で別の本文になる問題は「CP932で解釈した場合の問題」の見出しで区別する。 |
| 未使用リソース定義 | 発音・表示の参照に使われていないWAV/BMP定義ごとのコマンドと元パス。別番号で同じパスの定義や、同番号の複数定義も保持する。別の定義番号や譜面が同じファイルを使い得ることを説明する。 |
| 未定義参照番号 | 参照があるが対応するWAV/BMP定義がない定義番号。`#WAV01` と `#BMP01` は別項目とし、同じ名前空間内の番号だけを重複排除する。無音ノーツなどが該当することを説明する。パスが空・不正でも定義が存在する番号は含めない。 |

定義元は解析時の出所を保持し、拡張子、解析後のAudio/Image/Movie分類、CP932の復号結果から推定しません。

BMSONも三分類を使います。音声の `sound_channels` / `key_channels` / `mine_channels` と `bga_header` の未使用定義を、本来の項目名で表示します。補助画像も `info.eyecatch_image` などの本来の項目名を使い、音声に架空のWAV番号を付けません。BGAの未定義参照番号は、`bga_events` / `layer_events` / `poor_events` をまたいで同じ数値を一項目へ集約し、発生位置とイベント数は集計しません。不正なイベント値から0などの番号を補いません。説明文も譜面形式に合わせます。

値と一覧は有効な読取り専用TextBoxで選択でき、Ctrl+A/Cの通常操作でコピーできます。一覧の内容はこの画面内だけ上端に揃え、通常入力の共通スタイルは変えません。専用のコピー・再読込・閉じるボタンやF5操作は設けず、タイトルバーまたはEscで、読取り中にも閉じられます。

| 状態 | パス下の欄 | 三つの一覧 |
| --- | --- | --- |
| 初期・読取り中 | Collapsed（領域も余白も取らない） | 空欄 |
| 成功 | Collapsed | 結果を表示。成功して空だった一覧だけ「なし」 |
| 失敗 | 枠付きで失敗を表示 | 成功した空一覧の「なし」に置き換えない |

各ウィンドウは、開いた時点で捕捉した譜面だけを一度読みます。メインの選択・導入先・候補の変更、別のウィンドウの開閉、元ファイルの移動・削除で、既存の結果を差し替えません。再確認はウィンドウを新しく開いて行います。

開いている同じウィンドウで言語を変更すると、次を現在の言語へ更新します。

- 資源の確認基準
- 不足・確認不能の理由
- 成功した空一覧の「なし」
- BMS/BMSONごとの説明
- 失敗表示

配置基準の種別と確認先パス、定義元・番号・元パス・診断理由の事実、タイトル・アーティスト・実パスは変えません。再読取り・再解析・診断ログの再出力もしません。初期・読取り中に言語を変更しても三つの一覧と失敗欄は空のままで、読取りが終わった時点の言語で結果を整形します。失敗を成功の空一覧へ置き換えず、翻訳のために元の例外を保存しません。

三つの一覧TextBoxは、自身の可視枠をスクロールバーの基準とし、右・下に表示状態へ連動しない16 DIPの保護域を持ちます。左・上の入力余白と併せ、`TextBox.Padding` が一回だけ所有します。画面外周の16 DIPと上部選択の内容余白は重ねず、ウィンドウを拡大すると一覧の表示領域が広がります。

### 読取りと解析

窓を開いた時点から背景で一度だけ単一ファイルを捕捉し、既存の `BmsChartFileParser` / `BmsonChartFileParser` で軽量解析します。BMS/PMSの解析規則は[譜面ファイルの読取り](../library/chart-file-reading.md)と共用します。対象は、全行、条件分岐、LNOBJ、BASE62、00、地雷、0A、生の文字ペア、実際に採用した本文のリソース、表示メタデータ、独立したCP932診断です。未定義参照番号から必要ファイルを生成せず、BMSONの数値受理は厳格化しません。詳細ノート解析や再生の解析には接続しません。

定義元・論理行番号・原文行は、任意の診断受取先へだけ出力し、通常の解析では長期保持しません。リソースの問題の一覧には、使用する通常リソースと任意画像の非空定義から、実際の問題とCP932の問題を原文順に集めます。

- 実際の問題は、不足・空指定・不正パス・不正構文などです。
- 任意画像は、先頭・後続・分岐内を問わず全ての非空定義を確認します。正常・省略・空白だけの指定は問題にしません。
- 通常リソースの空指定は問題に残します。
- 未使用の通常定義の問題は、未使用欄だけに残します。

同じCP932本文に関する事実は元定義の理由へ併記し、充足している定義でもCP932の問題を残します。別の本文になる場合と、先頭BOM専用の独立した診断は、「CP932で解釈した場合の問題」の見出しと説明を一回だけ置き、CP932自身の原文・論理行を表示します。対象が0件なら見出しも出しません。別本文どうしをIDや行番号で結合しません。

BMSは、左端に桁数を揃えた行番号、元の原文一回、短い理由を表示します。原文のインデント・パス・括弧を保持し、アプリが加える理由の括弧は、六言語とも半角 `()` 一組にまとめます。未使用欄も同じ形式です。BMSONは、保持済みの項目位置・ID・元パスを一回ずつ表示し、JSON行番号を創作しません。リソース一覧だけ等幅フォントを使い、TextBox標準の選択・コピー・横スクロールを保ちます。

診断表示は、採用状態や要求数の計数説明を保持しません。通常解析の先頭非空値・CP932保存画像・健康度と要求数は従来どおりです。存在確認は健康度と同じ三分類・対応拡張子・配置照合を使い、捕捉した具体的な固定文字コードと配置を一度の読取りへ渡します。言語変更は結果の整形だけを行います。

### 寿命と失敗

操作の分類は[操作の競合ポリシー](../core/operation-concurrency-policy.md#音声と独立した読取り)を正本とします。結果は、単一の捕捉内容を読み、その後に存在を確認したものです。進行中の変更や外部更新との原子的な整合は要求せず、予約・再試行・世代番号・永続キャッシュは追加しません。

取得・解析の失敗は空の成功に置き換えず、翻訳済みの失敗表示を残します。表示モデルが、操作名・譜面パス・元の例外を `NLogWrapper.FileLogger` へ一度渡し、閉鎖後の通常失敗も原因を診断できるようにします。ログ出力の失敗は局所化し、元の失敗の終端と表示を置き換えません。UI文言には例外本文を渡しません。

結果は一画面の寿命に閉じ、DB・健康度・警告・自動導入判定・ライブラリ参照へ書き戻しません。ウィンドウの `Closed` から表示モデルの `Close` へ接続して言語購読を解除し、閉鎖後は言語変更も遅延した成功・失敗も結果・通知へ反映しません。開始済みの読取りTaskの通常失敗は、表示モデルが回収します。

専用の `ChartPropertiesWindowManager` がウィンドウと読取りの寿命を所有します。

- ウィンドウはShow前に登録し、Show後に表示が継続している場合だけ、実際の読取りTaskを開始して追跡します。`Loaded` では読みません。
- 個別に閉じた後も、終端していない読取りは所有します。完了済みのTaskと閉じたウィンドウは保持し続けません。
- メインが最初に終了を受け付けた時点で、新規の表示と読取りを拒否して全ウィンドウを閉じます。最終終了処理では、先に個別に閉じたウィンドウの読取りも、既存の終端処理より前に終端まで待ちます。
- Show中の終了再入で閉じたウィンドウは、その後に読取りを開始しません。
- UIスレッドで同期的に待機しません。

表示自体が失敗した場合は、作成済みの実Windowを閉じ、テーマ購読を含むウィンドウの寿命を回収します。元の例外・操作・譜面パスをログへ渡し、終了の受付前だけ、画面の枠組みの失敗通知へ一度返します。終了受付後に戻るShowの失敗は、[終了開始後の画面反映](../runtime/shutdown.md#終了開始後の画面反映)に従って新しい通知を抑止します。取得・解析の失敗の回収とログは、表示モデルが担当します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 一覧の局所枠と固定保護域 | `ChartPropertiesWindow.xaml` の三つのResourceList | `ChartPropertiesWindowTests` の既存表示・Copyケースで、読取り専用の選択、共通表示リソースへの接続、右下16 DIPを確認する。長い多数行での末尾文字の保護は、同じ共通TextBoxを使う `PlaylistImportDialogTests` の実入力ケースが代表する。 |
| 単一の実ファイル、保留、複数・未所持・欠損 | [`ChartPropertiesViewModel.ResolveTarget`](../../../BeMusicSeeker/ViewModels/ChartOperations/ChartPropertiesViewModel.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`ChartPropertiesViewModelTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Resources/ChartPropertiesViewModelTests.cs) / [`ChartPropertiesViewModelTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Resources/ChartPropertiesViewModelTests.cs) は条件判定を確認する。[`MainWindowSelectedChartContextMenuWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowSelectedChartContextMenuWpfTests.cs) は、コンパイル済みメニューから実managerと独立Windowへの配送、同一譜面の複数表示、個別閉鎖、選択に追従しないこと、実Show失敗の通知と終了後の抑止を確認する。 |
| 定義単位・未定義参照番号・必要参照と存在規則、BMSON、取得・解析失敗 | [`ChartResourceDiagnosticsService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceDiagnosticsService.cs)、[`ChartResourceExistence`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceExistence.cs) | [`ChartResourceDiagnosticsTests`](../../../tests/BeMusicSeeker.Core.Tests/Resources/ChartResourceDiagnosticsTests.cs) は、実際の軽量パーサーと小さい物理ディレクトリで、WAV/BMPの別名前空間、BASE62、定義単位、BMSONの音声三系統と三種のBGAイベントを確認する。CP932の未使用定義、同一番号の複数定義、任意画像の採用行は、不正なバイトを含む入力で確認する。全行と特殊IDの網羅は `ChartResourceSnapshotTests`、数値受理は `BmsonSongParserTests`、健康度と導入は `BmsLibraryMaintenanceServiceTests` / `BmsLibraryInstallEstimationServiceTests` が分担する。 |
| 背景で一回、現在の言語での再整形、失敗表示、閉鎖後の終端・非反映 | `ChartPropertiesViewModel` | `ChartPropertiesViewModelTests` が、BMS/BMSONの全理由、元のコマンド・番号・パス、形式説明、読取り中と完成後の言語変更、空成功の三一覧、失敗の再翻訳、捕捉した設定先、閉鎖後の言語通知0件、遅延した成功・失敗の非反映を分担する。gateと実Taskを使い、finallyでClose・解放・回収して言語を復元する。 |
| 保留の不変配置と元譜面定義、確認基準・パス | `ChartResourcePlacement`、`ChartPropertiesViewModel`、`ChartPropertiesWindow` | `ChartResourceDiagnosticsTests.Read_PreservesOriginalDefinitionsWhileUsingCapturedInstallationPlacement` は、異なる配置でも定義番号・未使用・未定義を保持することを確認する。`ChartPropertiesViewModelTests.LoadAsync_KeepsPlacementCapturedBeforeDestinationChanges` は読取り中の設定先変更に追従しないこと、`ChartPropertiesWindowTests` は実Binding、`MainWindowSelectedChartContextMenuWpfTests` は保留文脈の実メニュー配送を確認する。 |
| 独立窓の寿命、終了受付・実読取りの終端待機 | [`ChartPropertiesWindowManager`](../../../BeMusicSeeker/Views/ChartProperties/ChartPropertiesWindowManager.cs)、`MainWindow` | [`ChartPropertiesWindowManagerTests`](../../../tests/BeMusicSeeker.Windows.Tests/Resources/ChartPropertiesWindowManagerTests.cs) は、実Windowの個別Close後の言語通知0件、遅延成功・通常失敗の非反映、読取り終端までの終了待機、実Show失敗と再入を確認する。[`MainWindowViewHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowViewHostTests.cs) は、二つの読取りの最後の実終端より前に最終終了しない接続を確認する。タスクバー設定は共通表示scopeの介入前、親なし・非モーダルは実表示後に観測する。 |
| 三分類、読取り専用・標準コピー、Esc | [`ChartPropertiesWindow`](../../../BeMusicSeeker/Views/ChartProperties/ChartPropertiesWindow.xaml) | [`ChartPropertiesWindowTests`](../../../tests/BeMusicSeeker.Windows.Tests/Resources/ChartPropertiesWindowTests.cs) は、表示scopeと共有Dispatcherで、同じ窓の言語変更による確認基準・理由・なし・形式説明と失敗表示の実Binding、一覧の上端揃え、分類切替、失敗欄のCollapsed/枠付き表示、SelectAll/Copyの可否、Esc閉鎖を確認する。クリップボードへの実書込み、OS入力、実モニターでの表示は自動検証の保証外とする。 |
| 通常解析への診断詳細非保持、再生・DB・索引への非接続 | 任意の `ChartResourceParseDetails` と読取サービス | 通常の呼出元、`ChartFile`・DB、依存経路を静的に確認し、言語更新が再解析・診断ログの再出力・例外保持へ接続しないことも確認する。翻訳辞書の整合は `LocalizationResourceParityTests` が分担する。 |
| 原文・論理行・実問題・三分類・同本文と別解釈のCP932診断 | `ChartResourceDiagnosticsService`、`ChartPropertiesViewModel` | `ChartResourceDiagnosticsTests` は、三用途の正常な先頭値を除き、後続の欠損・親参照の実問題を原文順に一回出力することを確認する。同じ実バイトの境界で、CP932の行境界・先頭BOM、同本文の一回表示とCP932問題だけの通常充足、別本文の改行消費・通常側なしも確認する。`ChartPropertiesViewModelTests` は、使用・未使用・任意画像の表示先、別解釈のラベルと独立した原文・論理行、六言語、任意画像の実問題と原文の整形、固定指定の捕捉と言語変更時の読取り一回を確認する。辞書の整合は `LocalizationResourceParityTests` が分担する。 |

## 関連資料

[一覧](table-view.md)、[譜面情報](../library/chart-info.md)、[利用者マニュアル](../../../docs/manual.ja.md#譜面プロパティを確認する)。
