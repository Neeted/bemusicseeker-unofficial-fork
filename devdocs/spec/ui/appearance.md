# 外観と共通の表示部品

## 目的と適用範囲

テーマ、標準の表示部品、設定画面とダイアログの外観を定めます。機能の受付、保存、終了順序は各機能の仕様に従い、外観の変更で変えません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 共通スタイル | `App.Canonical.*` で識別する、用途ごとに明示して採用する表示設定。 |
| DIP | Windowsの表示倍率に依存しない論理単位。 |
| DWM | Windowsのウィンドウ描画管理機構。 |

## 仕様

### テーマの保存と切替

`Settings.Default.AppearanceTheme` は、`Light`、`Dark`、`Sepia`、`Frost`、`NeonViolet`、`GreenTerminal`、`AmberTerminal`、`OledBlack`、`DeepSea`、`DosBlue` のいずれかを保存します。既定値と、不明・空・nullの正規化先は `Light` です。保存前にも正規化します。明るいテーマは `Light`、`Sepia`、`Frost` で、それ以外は標準の題名部にも暗色を指定します。テーマとは別に、一覧の文字サイズ、行の高さ、見出しの高さを設定できます。

`AppThemeService` は、起動時に正規化済みのテーマ辞書をアプリのリソースへ追加します。辞書の参照は `/BeMusicSeeker;component/Themes/` の形式に統一します。切替の対象は、登録済みの10個の配色辞書だけです。既存の配色辞書を除去して指定された辞書を追加し、同じフォルダにある共通スタイルの辞書は保持します。切替後は `ThemeChanged` を通知し、`Version` を進めます。

XAMLは原則として `DynamicResource` を使います。コードで描画する部分は、テーマの変更と版を使って、描画色・文字列・描画結果のキャッシュを破棄します。一覧の `CustomTablePalette` とスコアの色も、この規則に従います。

### 配色の定義

[明るいテーマ](../../../BeMusicSeeker/Themes/Light.xaml)、[暗いテーマ](../../../BeMusicSeeker/Themes/Dark.xaml)と追加の8個の配色辞書は、同じ用途のキーを持ちます。各配色の値は、その辞書を正本とします。

| キーの系統 | 用途 |
| --- | --- |
| `App.*` | 背景、本文、境界、無効状態、入力、操作の強弱、成功・警告・失敗、ダイアログ、メニュー、検索 |
| `Table.*` | 見出し、行、選択、現在セル、警告、未定義値、区切り線、並べ替え、チェック |
| `ScrollBar.*` | 軌道、つまみ、ボタン、境界、矢印と各操作状態 |
| `TreeViewItem.TreeArrow.*` | ツリーの展開状態 |
| `NormalBrush` など | 既存の `Simple Styles.xaml` が使う互換用のキー。新しい設定画面用の色には使わない |

新しい色は、既存の意味付きのキーで表せるかを先に確認します。設定画面やダイアログで、固定色や表専用の色を流用しません。進捗バーは、テーマの強調色とは別に、共通の緑色と薄い軌道を使います。選択された表の文字は、明るいテーマで黒、暗いテーマで白を基本とします。

### 共通スタイルの所有

アプリの辞書は `CanonicalDialogStyles.xaml` であり、そこから `CanonicalControls.xaml` を参照します。`App.Canonical.*` は、用途を明示した共通スタイルのキーです。導入しただけで採用していないメイン画面へ影響が及ばないよう、アプリ全体の型名による暗黙のスタイルとしては追加しません。

例外として `ToolTip` は、全画面で型スタイルを適用します。文字列から自動生成されるもの、明示的に生成するもの、SliderのAutoToolTipを、同じ意味のブラシへ接続します。背景は `App.PopupBackgroundBrush`、本文は `App.TextBrush`、枠は `App.BorderBrush` とし、標準のテンプレートを保って、表示中のテーマ切替にも追随します。

共通の操作・状態アイコンは、`AppFontIcon`、`App.IconFontFamily`、`App.Canonical.FontIconStyle` を明示して使います。

- Windows 11の `Segoe Fluent Icons` を第一候補、Windows 10の `Segoe MDL2 Assets` を代替とし、両方で同じ意味を持つコードポイントを選びます。Segoeのフォントファイルは、アプリへ同梱しません。
- `AppFontIcon` は、固定領域の文字ベースラインへ合わせず、解決された字形の実際の描画領域を、表示領域の中央へ配置します。
- アイコンの色は固定値にせず、本文・補助・強調・警告・失敗など、既存の意味付きのブラシへ接続します。

設定画面は、`SettingsControls` から共通辞書へ接続します。設定画面の既存キーは、`BasedOn` で対応する共通スタイルを参照し、設定画面内にだけ既定のスタイルを置きます。そのほかの独自ダイアログは、共通辞書を直接使います。辞書やテンプレートを、画面ごとに複製しません。

ボタン、入力欄、選択欄、チェック、ラジオボタン、ラベル、グループ、上部ナビゲーション、折りたたみ、メニューと区切り、一覧、スクロールなどを共通化します。標準のキーボード操作とアクセシビリティを保ち、`PART_ContentHost`、`PART_Popup`、`PART_EditableTextBox`、`PART_Track`、ScrollViewerの部品、Expanderの `HeaderSite` を欠かしません。

共通Expanderの見出しは、通常の背景に `App.ControlBackgroundBrush`、1 DIPの枠に `App.BorderBrush` を使い、表示中のテーマ切替に追随します。折畳み・展開の矢印は見出しの文字色と揃え、通常は `App.TextBrush`、無効時は `App.DisabledTextBrush` に追随します。開閉の双方向の結び付け、内容の表示、アクセシビリティ、ホバーとフォーカスの表示、余白を保ちます。

検証の対象は、同一ウィンドウ内で実際に適用された役割とテンプレート、および採用していない部品へ影響が及ばないことです。別々に読み込んだ辞書のオブジェクト同一性、ソース文字列、子要素の順序、固定座標は、契約にしません。

### 部品の形状

アプリが描画する矩形部品は、直角に統一します。共通・局所のBorder、Rectangle、Popupの枠、フォーカス枠、スクロールのつまみも、同じ規則を使います。角丸の属性・Setter・専用トリガーを持たず、既定の0を使います。形状だけの変更で、テーマ色、Binding、入力、操作、状態、寸法を変えません。角丸へ戻す設定は設けません。

次は円形を保ちます。

- 共通RadioButtonと、設定の環境選択の内側にある円形の選択印
- SliderのEllipseつまみ

次は変更の対象に含めません。

- MainWindowの既存のWindowChromeと題名部
- OSが所有する選択画面と緊急ダイアログ

標準のアプリウィンドウの外枠は、[標準ウィンドウの題名部](#標準ウィンドウの題名部)のDWM要求で扱います。

### 通常版と省スペース版

`CompactButtonStyle`、`CompactComboBoxStyle`、`CompactSliderStyle` は、それぞれの `App.Canonical` の通常スタイルから、寸法だけを変更します。描画テンプレート、意味色、ホバー・押下・無効状態、表示中のテーマ変更を共有します。文字サイズは縮めず、最小寸法と余白で省スペースにします。ComboBoxの候補項目は、通常の共通項目スタイルを維持します。

再生メニューの文字ボタン、ランプビューアの「最新」、プレイログの表示対象選択は、省スペース版を明示して採用します。選択欄自身が枠と背景を所有し、外側に装飾用のBorderを重ねません。再生帯のアイコン操作とランプの「割合／曲数」は、それぞれの役割のスタイルを保ちます。ランプの切替は、RadioButtonによる相互排他、選択時の強調色、左右間の境界線を保ち、隣接する「最新」と同じ最小高24 DIP・Padding 8,2を使います。別の用途が残る旧Button／ComboBoxの暗黙スタイルは維持し、全画面を一括では変更しません。

設定と再生のすべてのSliderは、通常版・省スペース版とも、同じ軌道とつまみを使います。

| 部位・状態 | ブラシ |
| --- | --- |
| 通常のつまみと進行側 | `App.AccentBrush` |
| 未進行側 | `App.BorderBrush` |
| ホバー | `App.AccentHoverBrush` |
| ドラッグ | `App.AccentPressedBrush` |
| 無効なつまみ | `App.DisabledTextBrush` |

軌道とつまみには輪郭線を付けず、つまみの部品寸法を描画寸法に揃えて、周囲に透明な余白を設けません。用途固有の時刻は、共通のつまみに表示を差し込む方法で扱い、Trackを複製しません。

すべてのSliderは、縦横とも `FocusVisualStyle=null` とし、テンプレートにも、外周のフォーカス枠やフォーカストリガーを持たせません。フォーカス、Tab移動、方向キー、Home／End、PageUp／PageDown、個別の刻み・値のBinding・反転・目盛を維持します。枠を抑止するために、フォーカスを移動・解除しません。

### 日付部品の表示

`App.Canonical.DatePickerStyle` は、標準DatePickerの入力面・カレンダーボタン・PopupとCalendarを、共通テーマへ接続します。24 DIP以上の最小高を使い、省スペースButtonと中央を揃えます。テキスト欄は読取り専用で、`PART_TextBox`、`PART_Button`、`PART_Popup`、Calendarの標準のセル・月年の移動・キー操作を維持します。Calendarの背景・枠・文字・選択・ホバー・無効状態は、動的な意味ブラシで描画します。

表示する文化圏は、`ResourceService.DisplayCulture` が `Resources.Culture` から都度導出します。未指定のときは、既存の文字列リソースと同じ `CurrentUICulture` を使います。言語を別のフィールドや設定へ保存せず、サービスと起動時の文化圏設定の生成順に依存しません。アプリの言語変更は、文化圏のプロパティも通知します。プロセスとスレッドの文化圏は変更しません。

`DatePickerPresentation` は、日付の選択状態に触れず、言語・週の開始曜日・短い日付の表示だけを更新します。

- 選択日、表示中の月、表示モード、取得の要求を保ち、表示中の同じポップアップとカレンダーへ反映します。言語の変更で、確定済みの日付を別の日付へ再解析しません。
- 案内文は入力欄とは別の表示に置き、入力欄の文字列として扱いません。
- 暦と書式はWPFの標準の規則に揃えます。言語から具体的な文化圏を求め、Gregorian以外の暦の文化圏では、利用できるGregorianの暦のうち `Localized` を優先し、なければ最初の候補を使います。候補がない場合だけ、Invariantの文化圏のGregorianの書式を使います。
- 追加の言語ファイルでth-THやar-SAを選んでも、表示後のフォーカスの離脱やカレンダーを開く操作で、別の日付へ再解析しません。受け付ける言語の範囲は変えません。

日付の入力欄は常に読取り専用とし、日付はカレンダーから選びます。標準の部品が再有効化のときに読取り専用を解除しても、共通の表示接続が読取り専用へ戻します。履歴の取得中やLoadingからReadyへの復帰でも、文字入力を許可しません。

WPFの日付部品は言語の変更で表示を整形し直さず、文字列の変更で日付を再解析するため、この表示接続が必要です。対象のWPFの版での理由と実装の手順は `DatePickerPresentation` のXMLドキュメントを正本とします。

### スクロールとフォーカス

#### 共通のスクロールバー

縦横のスクロールバーは、`App.Canonical.VerticalScrollBarTemplate` / `HorizontalScrollBarTemplate` と `ScrollBarStyle` が、方向、軌道、つまみ、無効状態を所有します。`SimpleScrollBar` は互換名として共通テンプレートを参照します。端の矢印ボタンは表示せず、`PART_Track` のページ送りボタン、標準のLine・Pageコマンド、アクセシビリティを保ちます。ScrollViewerの部品名、内容のスクロール、位置・可視範囲・最大値も維持します。

バーは内容領域に重ね、表示と非表示の切替で、内容の測定幅・高さを変えません。透明な入力領域は、縦は16 DIP幅、横は16 DIP高に固定します。既存用のバーは次のとおりです。

| 状態 | 背景 | つまみ |
| --- | --- | --- |
| 通常 | 表示しない | 外側の端に、直角の3 DIP |
| 入力領域全体のホバー、または軌道内のキャプチャ中 | 12 DIP、Opacity 0.35 | 8 DIP |

ドラッグやページ送りのキャプチャ中は、ポインターが領域の外へ出ても、背景12 DIPとつまみ8 DIPを一組で維持し、解除後は実際のホバーに従います。背景とつまみは、同じWPFの状態から拡大の条件を得ます。通常・ホバー・ドラッグの色には既存の `ScrollBar.Thumb*BackgroundBrush` を使い、ホバーよりドラッグを優先します。内部の描画だけを変え、部品の寸法や表の再計算へ波及させません。

つまみの長さは28 DIP以上を基本とし、それより短い軌道では、軌道内に縮めます。両軸を表示するときの交点は縦バーへ割り当て、横バーを右16 DIPだけ短くして、入力が重ならないようにします。不透明な角は置きません。スクロールが不要なときの非表示は従来の可視性の判定を使い、完全な自動非表示のタイマーや保存設定は持ちません。メイン画面と表は、配置の丸めと画素への整列を有効にします。

#### 一覧用のスクロールバー

一覧用の `App.Canonical.ListScrollBarStyle` は、同じテンプレートを使い、背景を細い状態から不透明に表示します。

- 背景は長手方向に続く帯で、通常は7 DIP、拡大時は12 DIPです。外端を所属枠の内縁に固定して内側へ広げます。
- 背景は `ScrollBar.TrackBackgroundBrush` を動的に参照するため、テーマ切替に追随します。
- 無効時もバー全体を減光せず、背景の実効Opacityは1のままです。無効の表示はつまみにだけ掛けます。
- 表示の指定は、添付プロパティ `ScrollBarChrome.IsOpaqueBackground` で渡します。ScrollBarの派生クラス、設定項目、保存状態は設けません。
- 対象は `CustomTableView` が所有する縦横2本です。ツリー、設定本文、入力欄、候補Popupは、既存用のままです。

背景の中で、つまみの両側（縦は左右、横は上下）には、通常・拡大とも各2 DIPの内余白を設けます。背景は、通常7 DIP（2+3+2）、拡大時12 DIP（2+8+2）で、背景の外端は所属枠の内縁に接します。つまみと所属枠の内縁との間の2 DIPも、背景が埋めます。

- 所属枠は、ウィンドウ本文ではクライアント領域または本文ペイン、入力欄と一覧では自身の可視枠、候補・メニュー・初期言語カードではその枠です。OSの影や不可視のサイズ変更領域は含めません。
- 内余白の2 DIPは、共通テンプレートが定義します。
- `ScrollBar.Padding` は、所属枠の外枠の補正（0または1 DIP）だけを表し、背景とつまみの描画へ一回ずつ適用します。縦は右、横は下だけに適用し、背景だけ、またはつまみだけには重ねません。
- 外側の位置を固定したまま内側へ太くし、長手方向には余白を足しません。透明なThumb・Track・ページボタンと、16 DIPの入力領域は縮めません。

#### スクロール内容の測定

標準のScrollViewerの内容は全面を使い、スクロールバーは内容に重ねます。バーの表示の切替で内容の測定条件が変わらないため、WPFの仮想化パネルが前提とする再測定が省かれることがあります。そこで共通テンプレートの縦横のバー（`OverlayScrollBar`）は、表示の切替時に、論理スクロールの内容だけを再測定させます。

- 対象は、バーの `Visibility` が変わり、かつ `CanContentScroll=true` の場合だけです。
- 再測定するのは、WPFと同じ選び方で決まる既存のスクロール内容の一つと、そこから内容の表示部までの経路だけです。未実体化・対象なし・条件に合わない場合・物理スクロールでは何もしません。
- 別の候補への代替、項目の走査、追加の状態、予約、明示的な測定は行いません。ホバー・ドラッグ・値の変更では再測定しません。

候補の選び方と再測定の手順は `OverlayScrollBar` のXMLドキュメントを正本とします。

`SimpleScrollViewer` も同じ共通テンプレートを実体化時に解決し、辞書の読込順を保ちます。内容の余白は、`ScrollViewer.Padding` からpresenterへ一回だけ渡します。ItemsPresenterと既存のスクロール所有者の接続、論理・物理スクロールの選択、仮想化を維持します。TextBoxの入力余白は、`TextBox.Padding` をWPFが内部の `PART_ContentHost` へ転送する経路に集約し、外側のBorderやホストのMarginには重ねません。表の末尾をバーの手前へ出す範囲の計算は、[一覧仕様](table-view.md#スクロール領域)に従います。

#### フォーカスの表示

WPF既定の点線のフォーカス飾りは使いません。表は選択と現在セル、ツリーは選択背景と文字、入力欄は `App.InputFocusBorderBrush`、その他の部品は選択・重ね合わせ・押下の状態で示します。分割線はTab移動の対象外にし、ドラッグによるサイズ変更を保ちます。

### 内容余白と間隔

| 対象 | 余白 |
| --- | --- |
| 通常の本文・フォームの外周 | 16 DIP |
| セクション間 | 24 DIP |
| フィールド間、主要な列間 | 16 DIP |
| 関連する部品間 | 8 DIP |
| ラベルと、短い説明・検証文との間 | 4 DIP |
| 高密度の操作帯 | 4 / 8 DIP |

枠の厚さ、アイコン、インデント、行・列・画像・外部再生面の寸法は、用途に応じて定めます。

ウィンドウの外枠には、内容用のPaddingを置きません。本文用の `App.Canonical.WindowBodyScrollViewerStyle` はペイン端まで配置し、固定の本文Padding 16を所有します。スクロールのないフォームは、一つの内容コンテナが外周16を持ちます。ヘッダーとフッターは本文の外に固定し、左右16に揃えます。横バーは本文領域の下端に置き、フッターへは延ばしません。

区切り線のない領域の間は、本文の上下Paddingが間隔を所有し、隣接するヘッダーの下やフッターの上には重ねません。可視の区切り線がある場合は、各領域の内容を線から16 DIP離します。セクションとフィールドの共通の間隔に、呼出し側のMarginを重ねず、末尾の間隔は解除します。非表示になる説明・検証・操作群の余白は、同じ可視性の要素が所有します。

本文用の余白を、すべてのScrollViewerへは適用しません。局所の一覧・入力・Popupは自身の枠を維持し、必要な保護域だけを、表示状態と無関係に持ちます。

- URL取込みと譜面プロパティの複数行TextBoxは、右と下の16 DIPで、末尾の文字を保護します。
- 検索候補は、右の16 DIPで行内操作を保護します。上下の内容余白を含めて、保存候補5行・補完10行の上限、短い候補の自然な高さ、独立したviewportと仮想化を保ちます。

共通の本文余白とセクション・フィールドの間隔は、配色辞書から独立した `App.Layout.*` リソースに置き、テーマを変更してもレイアウトを変えません。

### メイン画面

メイン画面は、背景と文字色をテーマへ接続します。外周は、内容の測定範囲を縮めない1 DIPの表示専用の枠とし、入力を受け取りません。非アクティブ時は控えめな文字色、アクティブ時は強調色です。最大化時は、既存の8 DIPの余白に合わせて内側へ移し、サイズ変更の判定を妨げません。

キャプションボタンは、常に控えめな文字色です。通常の背景は透明で、`IsMouseOver` のときだけ `App.ControlHoverBrush` を表示します。押下やキーフォーカスだけでは背景を点灯させず、影・白色光沢・パネル全体の常時背景は付けません。既存の位置、標準の `WindowChrome`、`SystemCommands` と専用の入力判定を使い、サイズ変更の判定幅5を保ちます。

#### 一覧用スクロールバーの外枠補正

表2個とサイドバーのツリー2個の `ScrollBar.Padding`（外枠補正）は、次のとおりです。

| 対象 | 右端 | 下端 |
| --- | --- | --- |
| 表2個 | 1 DIP（外枠または区切り線1 DIPの内縁へ背景とつまみを合わせる） | `ProgressHub.HasRows=false`（進捗行が折り畳まれ、外枠に接する）なら1 DIP、`HasRows=true`（進捗行が見え、外枠に接しない）なら0 |
| 下側のツリー | 1 DIP | 表と同じ |
| 上側のツリー | 1 DIP | 常に0（横分割線がツリーの外に描かれる） |
| Popup祖先を持つバー、MainWindow祖先を持たない配置 | 0 | 0 |

- メイン画面のリソースで、ツリーには共通のスクロールバーのスタイルの別名、表2個には `App.Canonical.ListScrollBarStyle` の別名として適用します。
- 表は `CustomTableView` が、本体の縦横のバーへ、このキーを明示的に参照させます。暗黙のScrollBar Styleは置かないため、編集候補Popupなどの他のバーへ、一覧用の背景は波及しません。
- ポップアップの中のバーは、メイン画面の中に配置されていても補正しません。下端の1 DIPも、ポップアップの外で、かつ実際のメイン画面の `HasRows=false` の場合に限ります。メイン画面が見つからない場合を `HasRows=false` として扱いません。
- 方向と進捗行の条件は描画だけに反映し、表の描画面・編集領域・末尾の露出範囲と、内容の測定寸法を変えません。

#### 再生パネルとの境界

再生パネルは、操作専用の独立したテーマリソースと同梱画像を使います。操作帯、画像上の文字、seekと音量の共通Slider、操作ボタンのフォーカス装飾の除去は、[再生パネル](playback-panel.md#テーマ画像とフラットな操作表示)に従います。

再生パネルと、その下の左サイドバー・一覧との境界には、下側領域の上端の全幅に、1 DIPの線を重ねます。色は左右のペイン間と同じ `App.SeparatorBrush` を使い、テーマ変更に追従します。表示専用として入力を受け取らず、既存の領域寸法とSplitterの操作領域を維持します。ツールバーと一覧ヘッダーの境界は、[一覧側の上枠](table-view.md#外観の更新)が所有します。

#### ツリーと検索欄

ツリーの通常・選択・無効の文字色は、`TextBlock` と `EditableTextBlock` の両方へ明示します。アイコン付きのツリー項目の本文は、表示でも編集でも、行内の上下中央に揃えます。サイドバーの分割線は、操作範囲5・見える線1とし、透明な操作範囲を重ねます。余分な余白で、ツリーと表、スクロールバーを離しません。

左サイドバーの二つのツリーでは、データから生成する項目（プレイリストの表とフォルダ、ライブラリのフォルダ、インストールの新規・保留、プレイ履歴のアーカイブ、重複検索のグループとフォルダ）の選択表示を、手動選択・コードからの選択とも、実際に選択している一つの項目だけに付けます。フォーカスの有無やスクロール位置によらず、選択項目が画面外へ出て戻っても選択は他の項目へ移らず、選択項目自身に表示が残り、所属する上位の項目も展開されたままです。スクロールだけでは選択も一覧の要求も変えません。

WPFの仮想化は、項目の入れ物を再利用するとき選択状態を保存・復元せず入れ物に残します。そのため、選択中の項目と、選択項目の祖先の項目だけを再利用の対象から外し、他の入れ物は従来どおり解放します。再利用の通知は項目を所有する段にだけ届くため、データ項目を生成する段ごとに接続します。XAMLに直接書いた固定項目は、項目自身が入れ物なので対象外です。

メイン画面右上の通常検索欄とプレイリスト一覧の検索欄は、次のように示します。

- 非フォーカス時の枠に、テーマ別の `App.SearchBorderBrush` を使い、入力領域を識別できる控えめな輪郭を示します。全10個の配色辞書に `SolidColorBrush` として定義し、両検索欄から `DynamicResource` で参照して、テーマ切替に追随します。
- 各検索エディター内にキーボードフォーカスがあるときだけ、枠を既存の `App.InputFocusBorderBrush` へ切り替えます。ヘルプなど周辺のボタンへのフォーカスは、この判定に含めません。
- 高さ24 DIP、枠線1 DIPの直角の枠とし、横幅、余白、アイコンの配置、候補の表示、検索操作は保ちます。
- 背景は、空入力のときに `App.ControlBackgroundBrush`、文字があるときに `App.ControlBackgroundActiveBrush` を使い、絞込み中であることを示します。明るいテーマは薄い桃色、暗いテーマは落ち着いた注意色とし、構文警告の `!` とは区別します。候補の操作は、検索支援の仕様に従います。

検索欄の絞込み・構文警告・ヘルプ・クリアと、左サイドバーのプレイリスト・フォルダ・検索・パッケージ・処理中の状態は、共通のアイコン体系に揃えます。`[NO SONG]` は通常のフォルダと同じ表示にせず、未所持の特殊フォルダと分かるフォルダ系のアイコンを使います。処理中は、通常のアイコンを同期アイコンに置き換えて回転させ、処理状態の判定や寿命は、既存の管理主体から変えません。

### 設定画面の構造と即時反映

設定画面の区画には `SettingsSection` を使い、区画間の下余白は `App.Layout.SectionMargin` で揃えます。区画内の補足を折りたたむ `Expander` は、共通の `SettingsExpanderStyle` を使います。親が区画間の下余白を持ち、展開内容との間隔は共通テンプレートが持つため、呼出し側で同じ余白を重ねません。

一般ページのLR2パス設定の説明文は、LR2ディレクトリ設定の「詳細」内で、個別パスより前に表示します。文字色は、設定画面の説明文と同じ `App.DisabledTextBrush` を動的に参照します。

設定画面は、左のカテゴリ一覧、見出し、本文、下部の操作から成り、上部のタブは使いません。カテゴリ、見出し、下部の操作を固定し、選択中の本文だけを、一つの縦ScrollViewerでスクロールします。横スクロールは使わず、長い翻訳のラベルや選択文は折り返します。

カテゴリは、直角の選択背景と左端の強調線で選択を示し、マウス、キーボード、無効の状態も識別できるようにします。大きな節を入れ子のカードにせず、節の間と内部は、共通の内容余白と間隔に揃えます。

テーマは、`AppearanceThemeOptions` の表示名と識別子を、表示領域の幅に広がる単一の選択欄で扱い、`AppearanceTheme` の双方向の結び付けで選択します。開くたびに作る画面は、表示ツリーへ接続して現在値を受け取ります。未接続の部品へローカル値を設定して、結び付けを壊しません。選択時にテーマだけを保存して即時に適用し、閉じても保存済みの選択を保ちます。保存に失敗した場合は、元の確定値と配色を保ちます。

一覧の文字サイズと、行・見出しの高さは、スライダーのドラッグまたはキー操作の完了時に、その項目だけを保存し、`MainWindowViewSettingsStore` と実際のメインの表へ反映します。別の固定データの見本表は置きません。初期化は、三つの値を既定値として保存し、閉鎖時にも保存済みの表示を保ちます。

### 設定用の表示部品

`SettingsSection` は、見出し、任意の説明、内容の一つのテンプレートを所有します。有限の高さでは、見出しと説明を除いた残りを内容へ渡し、自動調整の画面では、内容の必要な高さを保ちます。人工的な余白や、画面ごとの複製は作りません。

`SettingsField`、`SettingsOptionRow`、`SettingsPathPicker`、`SettingsListEditor`、`SettingsStatusBanner` は、表示用のプロパティとイベントだけを公開します。サービスの探索、業務命令、保存は所有しません。表示へ接続するだけで、編集値を変更しません。再生設定は選択中のプレイヤーの詳細だけを表示し、選択を変更しても、非表示のプレイヤーの設定を書き換えません。

`SettingsField` と `SettingsPathPicker` は、入力検証の意味を、`ValidationStatus` と説明文で受け取ります。

- `Warning` は `App.WarningTextBrush`、`Error` は `App.ErrorTextBrush` を使います。パス入力と、検証に対応を明示した入力部品では、入力枠にも同じ意味色を反映します。
- 状態は色だけに依存させず、可視の説明文と、`AutomationProperties.ItemStatus` / `HelpText` を併用します。
- 正常時は状態と説明文を空にし、説明文が空なら検証文言を表示しません。
- 表示部品自身は、Warning / Errorの業務上の判定を行いません。

`SettingsField.Header` は、実際の入力部品のアクセシビリティ名を補うためにも使います。自身を参照元とする一方向の結び付けを所有し、その結び付け自体で所有を判定します。呼出元の値や別の結び付けは、同じ表示値でも上書きしません。見出しの変更や取り外しのときも、自分が所有するものだけを更新・除去します。

セクションが入力対象を示す場合は、同じ意味の直下の見出しを重ねません。説明も検証も持たない単純な囲みは取り除き、実際の入力へ翻訳済みの名前を明示します。一覧編集と検証欄は、`SettingsField`／`SettingsListEditor` の `IsHeaderVisible`（既定はtrue）で表示だけを抑止し、`Header` の意味、入力名、説明、検証の状態と文言を保持します。非表示の見出しと専用の余白は、領域を占有しません。表示文字列の一致や、先頭の入力かどうかによる自動判定は行いません。具体的な項目名、置換パターンの説明、状態や操作を示すチェックボックスとボタンの文字は、それぞれの役割として残します。

環境選択は、標準のRadioButton自身を、等幅二列・同じ高さのカードにし、名称を一回、折り返す説明をその下に表示します。

- 通常設定と初期環境設定は、同じ表示を使い、最小ウィンドウ幅でも横スクロールを生じません。
- 背景を含む全表面と標準のキー入力から、既存の `Click` による確認へ、一回接続します。
- 選択はラジオマークとテーマ対応の枠・背景、フォーカスは入力フォーカス色の枠、無効時は薄い表示で示します。
- 既存の一方向の選択Binding、有効条件、確認の取消や失敗による復元を保持します。

状態表示は、空または空白の文字列を畳み、構造を持つ内容と呼出元の非表示指定を保ちます。内容が戻れば表示を回復します。メッセージの全文をアクセシビリティ名、意味を `ItemStatus`、更新を控えめな通知として公開し、装飾アイコンを重複して読み上げません。音声機器の利用不可の理由やテスト結果などの長文だけは、専用の複数行スタイルで、幅に合わせて折り返します。

ComboBoxは、非編集型では、選択内容・中央・矢印の全体で開けます。編集型は、前面の入力欄、カーソル、キー、アクセシビリティ、標準のポップアップを維持します。どちらも、開いた時点の実測幅を候補の外側の幅に使い、閉じて幅を変更した後も追随します。内容幅、固定値、初回の値、最小幅だけでは代用しません。

下部の操作と各セクションの操作は次のとおりです。保存の単位は[設定の局所候補](../runtime/settings.md#局所候補)に従います。

| 操作 | 表示 |
| --- | --- |
| 通常の下部の操作 | 「閉じる」だけを置く。 |
| BMS・BMT・環境・recordの確定と取消 | 各セクションに置く。環境は、適用できる差分があるときだけ確定を強調し、差分がなければ確定も取消も無効にする。 |
| スコア・音声・再生方式・画像の有効な選択 | 自動で保存するため、確定の操作を置かない。失敗したときだけ必要な再試行を表示する。 |
| 初回の「この環境で開始」 | 強調色の系列を使い、フォーカスの枠は各強調色と3:1以上の明暗差を持たせる。 |

設定画面の中に進捗行は置きません。危険な操作とアイコンの操作も共通の状態の表示を使い、命令や有効条件を変えません。スライダーは縦横とすべての目盛の位置を保ち、目盛は `App.SliderTickBrush` で描きます。

### 標準ウィンドウの題名部

独自の外枠を所有するメイン画面以外のアプリのウィンドウは、XAMLでもコード生成でも `ThemedWindow` を使います。標準のWPFの題名部を保ち、ハンドルの作成後に、現在のテーマと、題名・文字・境界の色をDWMへ要求します。表示中の変更に追随し、終了時に購読を解除します。`ThemedMessageBox` を含め、同じ適用経路で、`DWMWA_WINDOW_CORNER_PREFERENCE`（33）へ `DWMWCP_DONOTROUND`（1）を要求し、外枠を直角にします。値と意味は、[MicrosoftのDWM資料](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)に従います。メイン画面には、この経路を追加しません。

OSや属性が非対応の場合、APIが存在しない場合、HRESULTが失敗した場合は、標準のWindows表示を使います。一方、リソース読込みや接続処理の予期しない例外は隠しません。

- 初回の接続中なら、購読・接続状態・ハンドルを戻して、同じ管理器で再試行できるようにします。
- 接続後のテーマ変更中なら、接続を維持して、後続の変更と終了を可能にします。
- 購読の解除は一回だけです。

### ダイアログの表示面

標準ウィンドウは、本文またはフォームの内容コンテナが余白を所有し、余白だけの外周Borderを置きません。内側へ、もう一つ塗りつぶし・境界・カードを重ねません。設定画面の本体は、標準の表示領域の端まで延ばし、内部の機能ごとの間隔は保ちます。

メイン画面内に重ねる初回の言語選択は、`DialogOverlayStyle` と `DialogSurfaceStyle` を明示して使います。カードの見出しと言語選択、説明本文、操作領域が、それぞれ内容余白を所有し、説明のバーは、カードの内縁を基準にします。プレイリスト取込みは、標準の独立したウィンドウを使います。OSのファイル・フォルダ選択と緊急ダイアログは対象外です。所有元、モーダルの結果、取消、終了の抑制、処理と寿命の所有は、表示部品の統一で変えません。

通常運用の `ThemedMessageBox` は、質問・情報・警告・失敗を共通のアイコン体系で表示します。質問と情報は強調色、警告は警告色、失敗は失敗色を使います。緊急経路のOSネイティブのダイアログは、この統一の対象に含めません。

### 個別画面の寸法と読み上げ

| 画面 | 契約 |
| --- | --- |
| リリースノート | `FlowDocumentScrollViewer`、`Meiryo UI`、文字サイズ12。本文は通常、版の見出しは太字。選択可能、ツールバーなし、縦は自動、横は無効の一つの表示範囲を持つ。既定テンプレートの内部ScrollViewerへ共通オーバーレイを適用し、文書のPagePadding 16はズームに従う。バーの位置と入力領域はズームから独立する |
| プレイリスト取込み | 候補と選択済みの二ペイン、URL入力、固定の下部操作を持つ。各一覧・入力の枠内でスクロールし、文字の拡大時も本文末尾と操作へ到達できる |
| プレイリスト一括編集 | 正の最小幅・高さ、サイズ変更、本文の縦スクロールを持ち、見出しと終了操作を固定する |
| プレイリストのプロパティ | サイズ変更可能。上部選択・更新日時・下部操作を固定し、選択ページだけ縦スクロールする。採用する最小寸法でも末尾の入力へ到達でき、横スクロールは使わない |

リリースノートの右クリックは、標準の `ContextMenuService` に任せ、コピーと全選択を含む標準の役割を維持します。アプリ独自のメニューや文言を追加せず、選択・クリップボード・物理カーソルを横取りしません。

プレイリストのプロパティの上部は、「全般」「フォルダ」「カスタムフォルダ」の単一選択です。共通の上部ナビゲーションを明示的に使い、標準の選択・項目選択のアクセシビリティとキー操作を保ちます。選択は下線で示し、ページ全体を囲むカードは作りません。

「カスタムフォルダ」ページでは、13種類のまとめ方と説明を持つ出力フォルダの区画を先に、出力基点・フォルダ名・ルート形式の出力先の区画を後に置きます。上から自然に並べ、ウィンドウを高くしただけで後半が下端へ移動しないようにします。

### 対象外

OS標準の選択画面、利用者定義のテーマ、個別色の編集、OSテーマへの自動追随は、この仕様の提供範囲に含めません。新しい画面も既存の共通スタイルを優先し、機能のない将来の設定を追加しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 矩形部品の直角化と円形部品の例外、ランプ切替の寸法 | `CanonicalControls.xaml`、`CanonicalDialogStyles.xaml`、`Simple Styles.xaml`、各画面の局所スタイル | 製品XAML全体の角丸属性・Setter・トリガー、円形選択印とSliderつまみ、未使用のタブスタイルと参照の不在、ランプ切替の最小高・Padding・相互排他・選択アクセント・境界線を静的確認する。形状・寸法の現在値を固定する恒久テストは追加せず、既存のSettingsControlPresentation / SettingsWindowPresentation / MainWindowChartPresentationWpf / MainWindowPlayHistoryWpf / MainWindowContextMenuResource / PlaylistLampViewerWindowPresentation / PlaylistImportDialog系列で入力・選択・配色・操作・末尾到達を回帰確認する。 |
| 本文と局所枠の余白所有、固定領域、最小寸法での到達性 | `CanonicalControls.xaml` の `App.Layout.*` / `WindowBodyScrollViewerStyle`、各Windowと `SettingsControls.xaml` | `SettingsWindowPresentationTests` は本文端と一回のinset、全ページの横方向の到達性を、`SettingsControlPresentationTests` は本文スクロール時の固定領域を確認する。`PlaylistSummaryBulkEditTests` と `DialogPresentationTests` はプロパティとカードの末尾到達・固定操作を分担する。各画面の間隔の役割、末尾Margin解除、非表示要素の余白所有は対象XAMLの静的確認で分担する。 |
| 入力Paddingの一回適用と局所編集枠の保護 | 共通TextBoxテンプレート、`Simple Styles.xaml`、`CustomTableView.CreateCellEditorTemplate`、取込み・譜面プロパティ | `MainWindowChartPresentationWpfTests` は適用後のPadding変更と表示・編集位置を、`PlaylistImportDialogTests` は長い多数行の末尾文字とバーの非重複を確認する。`ChartPropertiesWindowTests` は三欄の同じ表示資源への接続と保護域を担当し、表の編集開始・操作は既存 `CustomTableEditingTests` に残す。 |
| Popupの枠基準、行内操作の保護、候補の行上限 | `Simple Styles.xaml` の一覧・候補・メニュー、`KeywordSearchEditor` | `MainWindowContextMenuResourceTests` は実部品の枠からのつまみ位置、`MainWindowChartPresentationWpfTests` は保存5/6行・補完10/11行の境界、独立viewport、短い候補の自然高、仮想化を分担する。 |
| 文書余白のズーム追従とバー位置の独立 | `ReleaseNotesWindow` の文書・内部ScrollViewer | `DialogPresentationTests.ReleaseNotes_PreservesReadOnlySelectionAndScrollViewport` は実内部ホスト、ズーム前後の非空選択・スクロール・バー位置を確認する。標準選択メニューは既存の `ReleaseNotes_UsesFrameworkOwnedSelectionMenuRoles` が担当する。 |
| 共通オーバーレイ、固定入力幅、細いつまみ、標準操作 | `CanonicalControls.xaml` のScrollBar・ScrollViewer、`Simple Styles.xaml` の互換名 | [`MainWindowContextMenuResourceTests.CanonicalScrollViewer_MaterializesSharedScrollBarsAndPreservesBehavior`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowContextMenuResourceTests.cs) はcompiled部品（Simple辞書を先に読む配置を含む両軸）の16 DIP入力幅、通常時の背景非表示と7 DIP帯・つまみ3 DIP・背景内両側2 DIPの実矩形、短い軌道内への収まり、交点の非重複、なし/縦/横/両軸で内容領域一定、Lineコマンドの移動・ページボタンのInvoke・Scroll Automation・offset/value・viewport/maximum同期・無効表示を確認する。hover/dragの12/8 DIP一致・ドラッグ色の優先・領域外キャプチャ・解除後の復帰は、OSポインターを合否にせず静的確認と実機確認へ分担する。 |
| 一覧用の不透明背景、外枠補正、候補Popupへの非波及 | 共通ScrollBarのPaddingと `ScrollBarChrome`、`MainWindow.xaml` の局所ScrollBar Style、`CustomTableView` のStyle参照 | [`MainWindowChartPresentationWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs) の `CustomTableScrollBarsUseCompiledThemeAndOverlayContract` が実体化した一覧用の両軸で、背景の不透明（色・実効Opacity）、7 DIP帯・外端・つまみ両側2 DIP、透明部分のhit-testと交点、無効時の背景実効1、テーマ切替での背景・つまみの追随と選択・offset・F2で始めた編集中draftの保持を確認する。`MainWindowListScrollBars_AlignBothTablesToVisibleFrameAndKeepPopupOnSharedStyle` が実MainWindowの2表で可視外枠の内縁との一致と進捗行の有無による下端の差、ツリー用Styleを当てたprobeでの外枠補正（MainWindow内の縦横・進捗行あり/なし・Popup内・別Windowの祖先なし）と、表Resourcesから暗黙解決されるバーへ一覧用・外枠補正が波及しないことを、`CustomTableEditSuggestionPopup_KeepsSharedScrollBarStyle` が編集候補Popup内の通常背景非表示・枠内2 DIPを確認する。実MainWindowの編集入口は操作受付と実モーダルに依存するため、Popupは表単体で確認する。16 DIP入力と内容寸法・終端露出計算の不変は `CustomTableViewportTests` が担い、Styleの優先順位と辞書の非循環は静的差分確認で分担する。実ポインター操作や配色・100〜200%の高DPIでの見え方は自動検証済みと扱わない。 |
| オーバーレイの論理スクロール再測定、リサイズ時の仮想化維持 | [`OverlayScrollBar`](../../../BeMusicSeeker/Views/OverlayScrollBar.cs)、共通ScrollViewerのPARTバー | [`DialogPresentationTests.PlayHistoryPresetDialog_UsesBoundedResizablePlaylistViewport`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs) は未実体化の末尾へスクロールして選択した後、Window拡大で論理viewportが増えることを確認する。物理・論理の標準操作とItemsPresenterのconsumerは既存MainWindowContextMenuResource/SettingsControlPresentation/DialogPresentation系列へ分担し、候補の選択順・不適格時の無処理・祖先経路だけの無効化・状態なしと発火条件は静的確認する。 |
| 表の共通バーとテーマ切替、交点 | `CustomTableView`、共通ScrollBarテンプレート | [`MainWindowChartPresentationWpfTests.CustomTableScrollBarsUseCompiledThemeAndOverlayContract`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs) は実テーマ切替の表・実バーへの反映と、全面寸法・交点の非重複を確認する。設定・ダイアログの実採用は既存 `SettingsControlPresentationTests` / `DialogPresentationTests` のconsumer検査へ分担する。OS入力、領域外ドラッグの実操作、配色の見え方と高DPIは実機確認と分担する。 |
| テーマの切替と共通スタイルの保持 | [`AppThemeService`](../../../BeMusicSeeker/Models/Settings/AppThemeService.cs) | [`NativeWindowThemeContractTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Themes/NativeWindowThemeContractTests.cs)、[`NativeWindowThemeContractTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Themes/NativeWindowThemeContractTests.cs) は各配色への切替で、辞書のキー整合、共通スタイルの解決、変更通知と版更新、題名部の明暗判定を確認する。 |
| 通常検索・プレイリスト一覧検索の控えめな通常枠、フォーカス枠、入力状態の背景と配置 | [`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) の `KeywordSearchComposite` / `PlaylistSummaryKeywordSearchComposite`、全10配色辞書の `App.SearchBorderBrush` | `NativeWindowThemeContractTests` は全テーマのキー整合、[`MainWindowChartPresentationWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs) は検索操作とアイコン配置を回帰確認する。全10辞書の検索枠ブラシのキー・型・配色、両通常枠の動的参照、エディター専用のフォーカス判定、背景トリガーと寸法・余白の維持は変更時の静的差分確認で分担する。実環境での色の見え方は自動検証の対象に含めない。 |
| 全ToolTipの表示中テーマ追随 | `CanonicalControls.xaml` の型スタイル | `NativeWindowThemeContractTests.ToolTip_VisibleContentTracksThemeWithoutExplicitStyle` はIsOpenで表示した明示StyleなしToolTipで明→暗→明の実効背景・枠・本文、文字列と折返しTextBlockの文字継承・内容保持を確認する。文字列からの自動生成、CustomTableViewの明示生成、SliderのAutoToolTipが同じ型スタイルを使う接続は静的確認で分担する。 |
| テーマの切替、標準題名部の接続・解除と失敗、直角の外枠要求 | [`AppThemeService`](../../../BeMusicSeeker/Models/Settings/AppThemeService.cs)、[`ThemedWindow`](../../../BeMusicSeeker/Views/ThemedWindow.cs)、[`NativeWindowTitleBarController`](../../../BeMusicSeeker/Views/NativeWindowTitleBar.cs) | [`NativeWindowThemeContractTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Themes/NativeWindowThemeContractTests.cs)、[`NativeWindowThemeContractTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Themes/NativeWindowThemeContractTests.cs)、[`NativeWindowTitleBarTests`](../../../tests/BeMusicSeeker.Core.Tests/Themes/NativeWindowTitleBarTests.cs) が既存のテーマ・接続・終了・失敗処理を担う。DWMの33/1要求、既存のTrySetAttribute経由での失敗処理、ThemedMessageBoxを含む適用先とMainWindowへ追加しないことは静的確認する。実OSでの外枠の見え方は自動検証済みと扱わない。 |
| 共通Expander見出しの背景・枠・文字・矢印と動的テーマ切替 | [`CanonicalControls.xaml`](../../../BeMusicSeeker/Themes/CanonicalControls.xaml) の `App.Canonical.ExpanderHeaderToggleStyle` / `ExpanderHeaderToggleTemplate`、[`SettingsControls.xaml`](../../../BeMusicSeeker/Views/Settings/SettingsControls.xaml) の `SettingsExpanderStyle` | [`SettingsControlPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsControlPresentationTests.cs) の `SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes` は同じ表示済みExpanderで明→暗→明の背景・1 DIPの枠と、折畳み・展開・無効時の文字・矢印の実効色、双方向開閉、内容表示と `IExpandCollapseProvider` を確認する。ホバー・フォーカスの表示と有効条件、余白は既存トリガーとテンプレートの差分確認で維持を確認する。 |
| 日付部品の表示言語・テーマと値の保持 | [`DatePickerPresentation`](../../../BeMusicSeeker/Views/DatePickerPresentation.cs)、[`ResourceService`](../../../BeMusicSeeker/ViewModels/ResourceService.cs)、共通DatePicker／Calendarスタイル | `PlaylistLampViewerWindowPresentationTests.Viewer_datePresentationTracksApplicationCultureWithoutChangingTheQuery` は未選択／選択済みの同じ実PART・開いたPopup、サービス生成順、OS側との文化圏不一致、同梱言語と非Gregorianの追加言語・明暗テーマで表示更新、標準の再解析経路を通した日付通知・取得・表示モードの不変を確認する。翻訳のキー集合は `LocalizationResourceParityTests` が担う。 |
| 通常／省スペースの共通描画、Sliderの縦横・反転・目盛・枠抑止・キー相当入力とテーマ追従 | [`CanonicalControls.xaml`](../../../BeMusicSeeker/Themes/CanonicalControls.xaml) | `SettingsControlPresentationTests.CanonicalControls_CompactVariantsSharePresentationAndPreserveSliderInput` は実部品の共通テンプレート・相対寸法・意味色・一回の命令と標準Sliderコマンドを確認する。`SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes` は通常／省スペースの全表面、編集入力と候補幅を確認する。OSの実Tab・画像上の視認性・異なるDPIは実機推奨へ分担する。 |
| 設定の表示部品、検証状態、アクセシビリティ、候補幅、項目保存と即時反映 | [`SettingsWindow`](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs)、[`SettingsField`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs)、[`SettingsPathPicker`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs)、[`SettingsSection`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs) | [`SettingsWindowPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowPresentationTests.cs) の `SettingsPages_RequiredValidationBindingsUseSharedWarningAndErrorPresentation`、`SettingsValidationPresentation_ExposesWarningAndErrorWithoutRelyingOnColorAlone`、および既存の表示・アクセシビリティ検査、[`SettingsWindowCompiledBehaviorTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowCompiledBehaviorTests.cs)、[`SettingsControlPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsControlPresentationTests.cs)、[`SettingsDialogBehaviorTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsDialogBehaviorTests.cs) |
| 環境カードの二列・同高・名称一回・全表面入力・言語／テーマ追従と選択不可 | `GeneralSettingsPage.xaml`、設定辞書の `SettingsEnvironmentRadioStyle` | `SettingsWindowPresentationTests.SettingsWindow_EnvironmentCardsFitAndKeepWholeSurfaceInputAcrossLanguages` は通常／初期設定と全6言語・最小幅で実測し、同じカードへの明暗テーマ反映とライブラリ操作中の無効化・復帰を確認する。明示した選択状態とClickから確認・取消への接続は `SettingDialogEditCompletionTests.OperationModeRadio_*`、標準キーとフォーカス枠の接続は型・イベント・テンプレートの静的確認、実Space・Tab入力と異なるDPIの見え方は実機推奨へ分担する。 |
| 見出し抑止と入力名・説明・検証・占有領域の保持 | `SettingsField.IsHeaderVisible`、各設定ページ | `SettingsWindowPresentationTests.SettingsField_HiddenHeaderPreservesMeaningDescriptionAndValidationWithoutEmptySpace` はField／ListEditorの実テンプレートを測定する。既存の `SettingsField_*` は可視／非表示の両方で、明示名・別Binding・所有の引取り・取り外しを確認する。実ページの採用と入力名は `SettingsPages_HeadingsAppearOnceAndEditorsKeepLocalizedAutomationNames`、検証配送と全ページ最小幅は既存ケースが担う。 |
| ダイアログの表示面、標準メニュー、結果とサイズ | [`ThemedMessageBox`](../../../BeMusicSeeker/Views/ThemedMessageBox.cs) | [`DialogPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs)、[`ThemedMessageBoxTests`](../../../tests/BeMusicSeeker.Core.Tests/Dialogs/ThemedMessageBoxTests.cs)、[`UiDialogCoordinatorWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/UiDialogCoordinatorWpfTests.cs) |
| 表とツリーへの配色・表示、共通アイコンの描画 | [`CustomTablePalette`](../../../BeMusicSeeker/Views/CustomTable/CustomTablePalette.cs)、[`AppFontIcon`](../../../BeMusicSeeker/Views/AppFontIcon.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowChartPresentationWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs)、[`MainWindowTreePresentationWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowTreePresentationWpfTests.cs) |
| 左ツリーのデータ項目の選択表示の維持、仮想化の再利用からの除外 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の `treeSelectionCleanUpVirtualizedItem`、[`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) のデータ項目を生成する各段の `CleanUpVirtualizedItem` 接続 | [`MainWindowTreePresentationWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowTreePresentationWpfTests.cs) の `DataTreeSelectionDisplayStaysOnSelectedItemAcrossVirtualizedScroll` は、ツリー外にフォーカスがある状態でライブラリのフォルダ、インストールの新規・保留、アーカイブの年と日を選び、近傍の入れ物が解放される位置までスクロールして戻しても、選択表示が選択項目だけに残り、祖先が展開されたまま、左の選択と要求が変わらないことを確認する。[`MainWindowPlaylistWorkspaceWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `PlaylistTreeSelectionDisplayStaysOnSelectedItemAcrossVirtualizedScroll` は、実MainWindowで表と多数のフォルダを持つ表のフォルダについて同じことを確認する。重複検索のグループとフォルダは[重複ファイル](../library/duplicate-files.md)の対応表のテストが分担する。 |
| 実キャプションからの状態変更と終了 | [`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowViewHostTests.MainWindowPlayerDrainKeepsDispatcherResponsiveAndDefersTerminalClose`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowViewHostTests.cs) は同じ実Windowで最大化・復元・最小化をAutomation Invokeから実状態へ照合し、通常・更新準備済みのCloseを同じキャプションから受付、player排出、UI保存、実Closedまで確認する。`MainWindowPlaybackWpfTests.PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner` は通常の透明な背景と影なしを実templateで確認する。hoverだけの背景接続と押下・focusだけでは点灯しないことは静的確認する。OSポインターによるhit-testとサイズ変更はこの自動検証の対象に含めない。 |

## 関連資料

[ダイアログの表示境界](dialogs.md)、[一覧表示](table-view.md)、[検索支援](keyword-search.md)、[設定と保存](../runtime/settings.md)、[表示テスト](../development/testing.md)。
