# 譜面プロパティ

## 目的と適用範囲

選択したローカル譜面のリソース不足の内訳を、必要時に読取り専用で確認します。健康度の再設定やファイルの変更は行いません。

## 用語

[共通用語集](../glossary.md)を参照します。「定義」はWAV/BMPなどの定義一件、「未定義参照番号」は、使用参照に現れるが対応するWAV/BMPまたはBMSONのBGA定義がない定義番号です。

## 仕様

### 対象と表示

一覧の右クリックの「譜面プロパティ」から開きます。実ファイルのある譜面一件だけを対象とし、複数選択・未所持・ファイル欠損では利用できません。保留パッケージ内の実ファイルも対象です。譜面本体の読取り元は元の実ファイルのまま、資源の存在確認は[保留一覧と同じ配置](../library/warnings.md#リソース状態の評価)へ向けます。メニューの表示、ホバー、ツールチップ、一覧の値取得では解析しません。

実行ごとに新しい、サイズ変更可能な非モーダルの `ThemedWindow` を開きます。同じ譜面も複数同時に表示でき、読取り中もメイン画面を操作できます。初期配置はメイン基準の中央とし、表示後のOwnerはなし、タスクバーに個別表示します。前後関係をメインへ固定しません。タイトル・アーティスト・実パス、資源の確認基準（設定先・先頭候補・現在の配置）と確認先パス、および次の三分類を共通のトップナビゲーションで表示します。[共通部品と配色](appearance.md)と[ダイアログの表示境界](dialogs.md)に従います。

| 分類 | 表示内容 |
| --- | --- |
| 不足・確認不能 | 必要な定義済み参照の定義元、定義番号、元のファイル名と末尾の理由。BMS/PMSは `#WAV01 file.wav (不足)`、`#BMP02 movie.mp4 (不足)`、`#STAGEFILE stage.png (不足)` のように表示する。省略・空指定の任意画像は含めない。 |
| 未使用リソース定義 | 発音・表示の参照に使われていないWAV/BMP定義ごとのコマンドと元パス。別番号同パスや同番号の複数定義も保持する。別の定義番号や譜面が同じファイルを使い得ると説明する。 |
| 未定義参照番号 | 参照があるが対応するWAV/BMP定義がない定義番号。`#WAV01` と `#BMP01` は別項目として、同じ名前空間内の番号だけを重複排除する。無音ノーツなどが該当すると説明する。パスが空・不正でも定義が存在する番号は含めない。 |

定義元は解析時の出所を保持し、拡張子・解析後のAudio/Image/Movie分類・CP932の復号結果から推定しません。BMSONも三分類を使い、音声の `sound_channels` / `key_channels` / `mine_channels` と `bga_header` の未使用定義を本来の項目名で表示します。補助画像も `info.eyecatch_image` 等の本来の項目名を使います。音声に架空のWAV番号を付けません。BGAの未定義参照番号は `bga_events` / `layer_events` / `poor_events` をまたいで同じ数値を一項目へ集約し、発生位置・イベント数は集計しません。不正なイベント値から0等の番号を補完しません。説明文も譜面形式に合わせます。

値と一覧は有効な読取り専用TextBoxで選択でき、Ctrl+A/Cの通常操作でコピーできます。一覧の内容はこの画面内だけ上端に揃え、通常入力の共通スタイルは変えません。パス下の欄は失敗時だけ枠付きで表示し、初期・読取り中・成功ではCollapsedで領域も余白も取りません。読取中の一覧は空欄とし、成功して空だった一覧だけに「なし」を表示します。専用コピー・再読込・閉じるボタンやF5操作は設けません。タイトルバーまたはEscで読取り中にも閉じられます。各窓は実行時に捕捉した譜面だけを一度読み、メインの選択・導入先・候補の変更や別窓の開閉、元ファイルの移動・削除で既結果を差し替えません。再確認は新しく開きます。

### 読取りと解析

開いた時点から背景で一度だけ単一ファイルを捕捉して、既存の `BmsChartFileParser` / `BmsonChartFileParser` で軽量解析します。BMS/PMSの全行・条件分岐・LNOBJ・BASE62・00・地雷・0A・生文字ペア・CP932資源解釈と表示メタデータは[譜面ファイルの読取り](../library/chart-file-reading.md)の規則を共用します。未定義参照番号から必要ファイルを生成せず、BMSONの数値受理を厳格化しません。詳細ノート解析や再生の解析には接続しません。

定義番号などは任意の診断受取先へだけ出力し、通常のライブラリ解析で保持しません。CP932復号不能は既存の厳格なバイト検査を採用済みの各定義行へ適用し、その定義番号・定義一件・使用状態と結び付けます。不正な先行バイトが改行を消費する既存の復号挙動でも実際の入力行への対応を保ちます。同一定義番号の複数定義や任意画像の採用行を区別し、未使用定義や無視した後続の任意画像を必要資源の確認不能へ含めません。健康度用の全体 `InputDiagnostic` は従来どおり残し、この診断の必須ファイルへ流用しません。存在検査は健康度と同じ正規化キーと種別別の拡張子代替を `ChartResourceExistence` で共有し、`ChartResourcePlacement` の同じ種別別照合を使います。開く時点で基準・パス・同梱と候補の照合入力を不変文脈へ捕捉します。通常所持は従来の実配置、保留は設定先・先頭候補と同梱の和集合、両者なしなら入力元です。元譜面を一回再解析した定義番号・未使用定義・未定義参照番号を保ち、存在確認だけを配置へ向けます。DB書込みや索引の再構築へ接続しません。

### 寿命と失敗

操作の分類は[操作の競合ポリシー](../core/operation-concurrency-policy.md#音声と独立した読取り)を正本とします。単一の捕捉内容を読み、その後存在を確認した結果を示します。進行中の変更や外部更新との原子的な整合は要求しません。予約・再試行・世代番号・永続キャッシュは追加しません。

取得・解析の失敗は空の成功に置き換えず、ローカライズした失敗表示を残します。表示モデルが操作名・譜面パス・元例外を既存の `NLogWrapper.FileLogger` へ一度渡し、閉鎖後の通常失敗も原因を診断できるようにします。ログ出力の失敗は局所化し、元の失敗の終端・表示を置き換えません。UI文言には例外本文を渡しません。結果は一画面の寿命に閉じ、DB・健康度・警告・自動導入判定・ライブラリ参照へ書き戻しません。閉鎖後は結果・通知を反映せず、開始済みの読取りTaskの通常失敗は表示モデルが回収します。専用の `ChartPropertiesWindowManager` がShow前に窓を登録し、Show後に表示が継続している場合だけ実読取りTaskを開始・追跡します。WindowのLoadedでは読みません。個別Close後も未終端読取りは所有し、完了済みTaskと閉じた窓は保持し続けません。メイン最初の終了受付で新規表示・読取りを拒否して全窓をCloseし、最終終了処理では先に個別Closeした窓の読取りも既存の終端処理より前にjoinします。Show中の終了再入で閉じた窓は、その後読取りを開始しません。UIの同期的な待機は行いません。

表示自体の失敗では作成済みの実WindowをCloseし、テーマ購読を含む窓の寿命を回収します。元例外・操作・譜面パスを既存ログへ渡し、終了受付前だけ既存shell失敗通知へ一度返します。終了受付後に戻るShow失敗は[終了開始後の画面反映](../runtime/shutdown.md#終了開始後の画面反映)に従って新しい通知を抑止します。取得・解析の失敗回収とログは既存表示モデルが担当します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 単一の実ファイル、保留、複数・未所持・欠損 | [`ChartPropertiesViewModel.ResolveTarget`](../../../BeMusicSeeker/ViewModels/ChartOperations/ChartPropertiesViewModel.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`ChartPropertiesViewModelTests`](../../../BeMusicSeeker.Tests/Resources/ChartPropertiesViewModelTests.cs) の条件判定、[`MainWindowSelectedChartContextMenuWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowSelectedChartContextMenuWpfTests.cs) のcompiledメニューから実manager・独立Windowへの配送、同一譜面の複数表示・個別閉鎖・選択非追従、実Show失敗の通知と終了後抑止。 |
| 定義単位・未定義参照番号・必要参照と存在規則、BMSON、取得・解析失敗 | [`ChartResourceDiagnosticsService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceDiagnosticsService.cs)、[`ChartResourceExistence`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceExistence.cs) | [`ChartResourceDiagnosticsTests`](../../../BeMusicSeeker.Tests/Resources/ChartResourceDiagnosticsTests.cs) は実軽量パーサー・小さい物理ディレクトリを使用。WAV/BMPの別名前空間・BASE62・定義単位とBMSONの音声三系統/三種BGAイベントを確認。CP932の未使用・同一定義番号複数定義・任意画像の採用行は不正な元バイトを含む入力で確認。全行と特殊IDの網羅は既存 `ChartResourceSnapshotTests`、数値受理は `BmsonSongParserTests`、健康度と導入は既存 `BmsLibraryMaintenanceServiceTests` / `InstallEstimationServiceTests` へ分担。 |
| 背景で一回、失敗表示、閉鎖後終端・非反映 | `ChartPropertiesViewModel` | `ChartPropertiesViewModelTests` はgateと実Taskで成功・遅延成功・遅延失敗を観測し、finallyで解放・回収する。 |
| 保留の不変配置と元譜面定義、確認基準・パス | `ChartResourcePlacement`、`ChartPropertiesViewModel`、`ChartPropertiesWindow` | `ChartResourceDiagnosticsTests.Read_PreservesOriginalDefinitionsWhileUsingCapturedInstallationPlacement` は異なる配置でも定義番号・未使用・未定義を保持する。`ChartPropertiesViewModelTests.LoadAsync_KeepsPlacementCapturedBeforeDestinationChanges` は読取り中の設定先変更非追従、`ChartPropertiesWindowTests` は実Binding、`MainWindowSelectedChartContextMenuWpfTests` は保留文脈の実メニュー配送を確認する。 |
| 独立窓の寿命、終了受付・実読取りjoin | [`ChartPropertiesWindowManager`](../../../BeMusicSeeker/Views/ChartProperties/ChartPropertiesWindowManager.cs)、`MainWindow` | [`ChartPropertiesWindowManagerTests`](../../../BeMusicSeeker.Tests/Resources/ChartPropertiesWindowManagerTests.cs) は個別Close後の遅延成功/通常失敗、実Show失敗・再入を確認。[`MainWindowViewHostTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewHostTests.cs) は二つのreaderの最後の実終端より前に最終終了しない接続を確認。タスクバー設定は共通表示scope介入前、Ownerなし・非モーダルは実表示後に観測する。 |
| 三分類、読取り専用・標準コピー、Esc | [`ChartPropertiesWindow`](../../../BeMusicSeeker/Views/ChartProperties/ChartPropertiesWindow.xaml) | [`ChartPropertiesWindowTests`](../../../BeMusicSeeker.Tests/Resources/ChartPropertiesWindowTests.cs) は表示scopeと共有Dispatcherで実binding、一覧の上端揃え、分類切替、失敗欄のCollapsed/枠付き表示、SelectAll/Copy可否、Esc閉鎖を確認。クリップボードの実書込み・OS入力・実モニター表示は自動検証の保証外。 |
| 通常解析への診断詳細非保持、再生・DB・索引への非接続 | 任意の `ChartResourceParseDetails` と読取サービス | 通常呼出元、`ChartFile`・DB、依存経路を静的に確認する。翻訳辞書の整合は既存 `LocalizationResourceParityTests` へ分担。 |

## 関連資料

[一覧](table-view.md)、[譜面情報](../library/chart-info.md)、[利用者マニュアル](../../../docs/manual.ja.md#譜面プロパティを確認する)。
