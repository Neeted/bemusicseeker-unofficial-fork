# キーワード検索の入力支援

## 目的と適用範囲

検索欄の候補、履歴、お気に入り、キーボード操作と保存条件を定めます。検索式そのものの解析・一致判定は既存の検索処理を使い、入力支援の追加によって意味を変更しません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 適用範囲と状態の所有

通常の譜面一覧、プレイリスト詳細、プレイ履歴は一つの保存範囲を共有します。プレイリスト一覧は独立した保存範囲を使います。候補は表示先の検索条件に適合するものだけを表示し、現在の画面では使えない保存済みの検索式を削除しません。

`KeywordSearchAssistanceOwner` は入力文字列、カーソル位置、表示先、変更不能な候補集合とその版、候補を閉じた条件を保持します。入力支援の計算は同期的に行い、キー入力ごとにDBを読むことや外部のロック取得をしません。通常の検索結果の更新に使う500ミリ秒の遅延は、候補の更新には適用しません。

`KeywordSearchSavedQueryOwner` は保存用の接続先を明示的に受け取ります。保存先の設定値の更新に成功した後で、メモリ上の順序、候補、変更通知を更新します。設定値の更新が失敗した場合は、全て従前の状態を保ちます。操作ごとに `Settings.Save` を呼びません。

`KeywordSearchEditor` は候補の表示、候補間の選択、区画ごとの仮想スクロール、IME、フォーカス、行内操作を担当します。画面の親は二つの検索欄を構成しますが、ポップアップの `IsOpen` をViewModelへ双方向で結び付けません。

### 候補の内容

入力欄にフォーカスを当てたとき、文字列、カーソル位置、表示先、候補集合の版が変わったときに候補を再計算します。

| 入力の状態 | 表示する区画 |
| --- | --- |
| 空 | 空でない「お気に入り」「現在の画面で使える履歴」「検索項目」をこの順に表示する |
| 検索項目の名前を入力中 | 一致する検索項目を表示する |
| 候補値を持つ項目の値を入力中 | 定義済みの値、または現在のプレイリスト等から受け取った値を表示する |
| 任意文字列、正規表現、候補に一致しない入力 | ポップアップを閉じる |

プレイリスト一覧専用の項目には `output:` を含みます。通常一覧の項目を無条件に共用しません。お気に入りに追加した検索式は履歴の保存値から消さず、表示上の重複を抑えます。お気に入りから外すと、現在の画面に適合する履歴として再び表示できます。

### 候補値

| 検索項目 | 挿入する値 |
| --- | --- |
| 通常一覧のクリア状態 | `nosong NP F AE LAE EC NC HC EXH FC PF MAX`。`defined` / `undefined` は出さない |
| 履歴の `clear` / `oldclear` / `newclear` | 上記と `defined undefined` |
| `rank` / `djlevel` / `dj` | `F E D C B A AA AAA MAX defined undefined` |
| 難易度 | `beginner normal hyper another insane defined undefined` |
| 判定 | `veryhard hard normal easy veryeasy defined undefined` |
| `judge%` / `judgepct` | `defined undefined` |
| 譜面の特徴 | `ln mine random lnmode cn hcn stop scroll defined undefined` |
| レベル、BPM、長さ、ノート数、ロングノート数、スクラッチ数、TOTAL、T/N、密度、最大密度、終盤密度、変速回数 | `defined undefined` |
| 未定義を許す率・得点・コンボ・BP | `defined undefined` |
| 履歴の確定状態 | `true false` |
| 履歴の月 | `1` ～ `12` |
| 履歴の `type` / `kind` | `score bp clear combo play` |

`playlist` / `ref` / `table` の値は、変更不能なプレイリスト候補集合から得ます。大小文字を区別せず前方一致で絞り、重複を除いて並べます。必要な引用符とエスケープを挿入します。解析側の別名 `undef` / `null` が使えても、候補からの挿入は `undefined` に統一します。未所持は `nosong` を挿入します。

### 候補の適用と古い候補の拒否

検索項目の適用は、現在入力中の項目名だけを `field:` の形へ置換します。先頭の否定記号と前後の文字列を残し、末尾の空白は追加しません。

値の適用は現在入力中の値だけを置換します。引用符、エスケープ、後続の検索式を維持し、直後の区切りを一つのASCII空白へ整えて、その後ろへカーソルを移します。履歴・お気に入りの適用は検索欄全体を置換し、その検索式を履歴へ保存します。

候補は、文字列、カーソル位置、表示先、候補集合の版、置換範囲に対応します。適用時にいずれかが異なる場合は適用を拒否して再計算します。古い置換範囲を新しい文字列へ使いません。

### 閉じる操作とフォーカス

フォーカスを失うか、ウィンドウが非アクティブになると候補を閉じます。Escで閉じた場合は、同じ文字列・位置・表示先・候補集合の版に限り再表示を抑制します。いずれかが変われば抑制を解除し、Ctrl+Spaceでは明示的に再計算して開きます。

上下キーは区画をまたいで候補を選び、入力欄にキーボードフォーカスを残します。EnterまたはTabで選択中の候補、選択がなければ最初の候補を適用します。IMEの変換確定に使ったEnterは候補の適用に使いません。

ウィンドウ内で入力欄とそのポップアップ以外をクリックすると、ポップアップが既に閉じていてもキーボードフォーカスを解除します。履歴保存を伴う通常の離脱処理は `LostKeyboardFocus` に集約します。

検索欄のクリア操作は、入力イベントの受付時に対応する検索文字列を同期的に空にし、フォーカスを維持・復帰させ、空入力の候補を直ちに表示します。タイマーで後から戻さず、二つの検索欄を相互に操作しません。検索結果側の遅延は変えません。

### 履歴とお気に入りの保存

お気に入りの件数に上限はなく、最後に追加したものを先頭に置きます。履歴は正規化後の文字列を大小文字を区別せず重複排除し、新しい順に20件まで保存します。保存形式は一行ごとのBase64で、不正な行を許容する既存の読込み規則を維持します。削除は現在の保存範囲で一致する正規化済みの検索式だけを対象にします。

お気に入りの行には削除、履歴の行にはお気に入りへの追加と削除のボタンを持たせます。これらの操作は検索式を適用せず、検索条件も変えません。マウス操作で入力欄から不要にフォーカスを奪いません。

### 行内ボタンと表示

お気に入り・履歴は各区画で最大5行、検索項目・値は各区画で最大10行を表示し、超過分は区画ごとに仮想スクロールします。全区画を包む共通スクロールは置きません。

入力欄からShift+Tabを押すと、選択中の保存済み検索式の最初の行内ボタンへ移ります。対象がなければ表示中の最初の操作可能な行を使います。お気に入りは削除、履歴は追加、削除の順です。Tab / Shift+Tabでボタンと入力欄の間を移動しても候補を閉じません。ボタンはEnter / Spaceで操作でき、操作後は入力欄へフォーカスを戻します。Escで入力欄へ戻った場合は候補の再表示を抑制します。

候補は入力欄の下辺から所定の間隔を空けて配置し、開く前に入力欄の実際の幅を取得します。ポップアップの外側の内容幅を丸め誤差の範囲で入力欄へ揃え、固定幅にしません。長い検索式は一行の省略表示にしますが、保存値や適用文字列を切り詰めません。

行にマウスを重ねたときは、行内ボタンも含めた一行として強調します。アイコンの意味と読みやすさは明暗両テーマで維持します。保存済み検索式の本文に付けるツールチップは現在の検索式全文、適用操作のアクセシビリティ名は現在の表示文字列です。行の再利用後も古い内容を残しません。検索項目・値には検索式全文のツールチップを付けず、各ボタン固有の名前・説明を優先します。

利用者向けの文字列はリソースと各言語の辞書に定義し、日本語・英語の操作説明も同じ挙動を説明します。色の正確な値、固定座標、実装上の描画オブジェクトではなく、操作と表示の意味を検証します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 表示先ごとの候補、入力と適用、古い候補の拒否 | [`KeywordSearchAssistanceOwner`](../../../BeMusicSeeker/ViewModels/KeywordSearchAssistance.cs) | [`KeywordSearchPresentationTests`](../../../BeMusicSeeker.Tests/KeywordSearchPresentationTests.cs)、[`GridKeywordSearchQueryTests`](../../../BeMusicSeeker.Tests/GridKeywordSearchQueryTests.cs)、[`ChartListFilterViewModelTests`](../../../BeMusicSeeker.Tests/ChartListFilterViewModelTests.cs)、[`PlaylistWorkspacePresentationStateTests`](../../../BeMusicSeeker.Tests/PlaylistWorkspacePresentationStateTests.cs) |
| 保存の成功順序、失敗時の不変性、履歴とお気に入り | [`KeywordSearchSavedQueryOwner`](../../../BeMusicSeeker/ViewModels/KeywordSearchSavedQuery.cs) | [`KeywordSearchSavedQueryStoreTests`](../../../BeMusicSeeker.Tests/KeywordSearchSavedQueryStoreTests.cs)、[`ApplicationCompositionTests`](../../../BeMusicSeeker.Tests/ApplicationCompositionTests.cs) |
| 明示入力のIME判定、区画のスクロール、行内ボタン、行の再利用 | [`KeywordSearchEditor`](../../../BeMusicSeeker/Views/KeywordSearchEditor.xaml.cs) | [`MainWindowChartPresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindowChartPresentationWpfTests.cs)。OSの実フォーカス・IME・キー操作は[入力操作の明示受入](../development/testing.md#入力操作の明示受入)。 |
| 表示文字列の言語間整合 | 表示リソースと各言語の辞書 | [`LocalizationResourceParityTests`](../../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs) |

## 関連資料

[一覧表示](table-view.md)、[外観](appearance.md)、[プレイ履歴](../playlist/play-history.md)、[WPFの検証方法](../development/testing.md)を参照します。
