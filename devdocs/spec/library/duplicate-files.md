# 重複譜面の確認と整理

## 目的と適用範囲

所持しているBMS・BMSONの重複検索、グループ表示、削除、フォルダ統合を定めます。保留入力、導入先の一時状態、プレイリストだけの情報は重複検索の所持主体へ含めません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

「グループ」は同じハッシュを共有するフォルダを連結した集合です。重複ハッシュのない兄弟譜面を含むことと、その譜面へ重複警告を付けることは別です。

## 仕様

### 識別と検索結果

重複はBMSの `hash` とBMSONの `md5` を共通の主ハッシュとして判定します。SHA-256へ切り替えて一致させません。パスまたはMD5のない保存行は所持集合に入らず、検索や警告解除の対象にもなりません。同じ正本パスに複数行がある状態は正常系としません。

`DuplicateChartGroups` は現在の検索結果です。必要時に `SearchDuplicateChartGroups` が結果を作り直し、有効なキャッシュがあれば再検索しません。初回と、所持集合の全置換後、結果を外部から設定した後は、所持集合全体を解析します。それ以外の再検索は、前回の検索結果と、その検索が使った所持集合の捕捉（基底）からの差分解析で、統合・削除・移動・導入・再調査など失効の原因によらず同じ経路を使います。結果の失効では基底を捨てません。`OwnedDuplicateChartRowSnapshot` は所持項目の識別、捕捉時の共通値・パス・ディレクトリ・MD5と重複ハッシュの索引を保持します。捕捉した値は後の現在値適用で変わらず、操作前の対象解決と確認後の固定対象検査は[共通譜面モデル](chart-model.md#所持譜面の識別条件)に従います。

### グループと警告

同じMD5を持つ複数パスを検出し、そのディレクトリをUnion-Findで接続します。AとBがハッシュX、BとCがハッシュYを共有すれば、A・B・Cは同じグループです。

グループには接続したフォルダの兄弟譜面も含め、整理時に同梱内容を確認できるようにします。重複警告を付けるのは重複ハッシュの譜面だけです。見出しは先頭の題名、子ノードはフォルダの一覧で、見出し順に並べます。

BMSの警告は同じ所持項目の識別の共通現在値へ反映し、BMSONは重複表示用の `ChartFile` に投影します。解除は前回実際に警告を付けたBMSの所持項目の識別の集合だけを対象とし、所持集合の全置換後だけ未追跡の警告を次回検索で全体解除します。警告だけの反映は集合版を進めません。グループへ入れる譜面のうち重複ハッシュに所属しないものからは、確定前の値に古い重複警告が残っていても重複警告を外します。

#### 再検索の差分解析

差分解析は、基底と現在でハッシュ別の所属（所持項目の識別とパスの組、行順）が変わったハッシュだけを起点にします。重複の有無を問わず全ハッシュが対象で、新設・消滅と、重複ハッシュを持たない兄弟譜面の追加・削除・パス変更を含みます。識別・ハッシュ・パスが変わらない値だけの更新（題名など）は変化に数えません。パスは序数（大文字小文字を区別）で比べ、フォルダの同一性は大文字小文字を無視します。

- 影響フォルダは、変化したハッシュに基底または現在で所属する譜面のフォルダです。影響フォルダを一つでも含む前回のグループが影響グループで、再判定の範囲は影響グループの全フォルダと影響フォルダ、および現在の重複ハッシュで接続するフォルダの閉包です。
- 結果は、同じ現在の所持集合の全体解析と、グループごとの所属フォルダ（表記・順序）、グループ内の譜面（所持項目の識別・パス・順序）、各譜面の重複警告の有無、グループの並びが一致します。フォルダの表記は全体解析と同じ「最初に現れた表記」で、重複ハッシュのバケットを先頭行の正規順に並べて決めます。
- 再判定の範囲に入らない前回のグループは同じ `DuplicateGroup` インスタンスを新しい結果へ含め、最後に判定した時点の見出しと譜面値を保ちます。維持したグループが保つのは、判定時の見出し、譜面の共通値、フォルダの順序と表記で、次に再判定されたときに更新されます。このため題名など識別・パス・ハッシュ以外の値だけが変わった場合や、識別とパスが変わらないまま所持集合の正規順だけが変わった場合（BMSONの順序確定など）に、維持したグループの見出し・フォルダの順序と表記が全体解析と異なることを許容します。所属フォルダの集合（大文字小文字を無視）、所属譜面の識別・パスの集合、重複警告の有無は一致します。並びは各グループが保持する見出しの序数比較で非減少です。再判定したグループは新しいインスタンスです。
- 譜面値の具体化は、再判定の範囲のフォルダの譜面だけです。フォルダ直下の現在行は既存の実パス索引から引き、所持集合全体を走査・具体化せず、全重複ハッシュの連結も作り直しません。変化の検出のためにハッシュ別バケットの辞書を一巡し、参照が異なるバケットだけ内容を比べます。
- 現在値の重複警告を付与・解除する対象は、変化したハッシュに所属する（した）BMSだけで、すでに警告がある譜面と警告のない解除対象の現在値は書き換えません。重複でなくなったBMSは警告を解除して追跡の集合から除き、重複でない譜面は追跡しません。基底に識別が無い行（MD5のない行）の変化は検出対象外です。

### 一覧と操作

| ツリーの選択 | 一覧 |
| --- | --- |
| 重複ルート | 全グループの譜面 |
| グループ | そのグループの譜面 |
| フォルダ | そのパス配下の譜面 |
| 統合候補一覧 | 専用の表（[統合候補一覧](#統合候補一覧)） |

グループは現在の検索結果内で一意な所属フォルダを値として捕捉し、手動選択と整理後の自動選択で共用します。見出しは表示用であり、同名・空名の別グループを同一視しません。捕捉した所属が現在の結果にない場合は空一覧とし、別グループや全件へ広げません。全件表示は明示的な重複ルート選択だけです。フォルダ配下の選択はグループ全体の選択と区別します。

一覧は `DuplicateFilterSelected` による仮想的な部分集合で、列設定は `DuplicateCustomTableColumnSettings` を使います。フォルダをExplorerで開く操作は、存在しない場合は何もしません。

検索結果の `ChartFile` は操作用の捕捉値として保持します。一覧の構築・再選択では、その所持項目の識別の集合の共通現在値を同じカタログ版で一回捕捉し、検索結果が持つ重複固有警告だけを重ねます。退役した所持項目の識別を別項目へ読み替えません。表示中も、確定したカタログ差分の実対象だけを交換し、表示済み・未表示の入力、検索適否、並び順、健康度を揃えます。BMS・BMSONの重複警告と、固有の兄弟譜面の非警告を維持します。

再選択で既存の並べ替え順を使う前に、捕捉済みの全入力で列・副キーTitle・同値時の元順序を線形検査します。値が変わっても整列済みなら同じ順序を再利用し、崩れた場合だけ再整列します。カタログ確定後・通知配送前の再選択にも同じ判定を使い、新しい値へ古い添字順を組み合わせません。通知の配送順に依存する重複専用のキャッシュ失効処理は設けず、新しい世代・表示行の全具体化も追加しません。

共通現在値の更新だけでは全重複解析や全行の具体化を行いません。グループ所属・フォルダ一覧・`Header` は検索結果のまま維持し、グループを再検索したときに、影響したグループだけを[差分解析](#再検索の差分解析)で置換します。共通値の差分反映で、ツリー所属や自動選択キーまで更新する保証は持ちません。捕捉済みの操作スナップショットは変更しません。

統合先メニューには同じグループから、入力元と同一または親子関係にあるフォルダを除いて表示します。同一・包含の判定は既存のパス比較を双方向に使い、大小文字・末尾の区切り文字の違いを同一として扱います。兄弟フォルダと接頭辞だけが同じ兄弟は候補へ残し、候補の重複排除は大小文字を無視します。候補の算出では存在確認や動的な受付を行わず、実行時の物理側の拒否条件を維持します。

`ShowDuplicateFileCheckConfirmMsg` が有効なら確認してから実行します。フォルダ統合の確認文には、統合元フォルダの配下（全階層・隠しフォルダを含む）にあるフォルダがある場合に、その件数と、配下のフォルダも相対配置を保って統合先へ移す旨を添えます。配下にフォルダがなければ従来の確認文のままです。件数は背景で数え（ファイルは数えず、再解析ポイントはそれ自体を1件と数えてその先をたどらず、長いパスは `LongPathFileSystem` に従う）、数えられなかった場合は警告を記録して、配下にフォルダがある場合にそれらも移す旨だけを添えます。確認を表示しない設定では数えず、追記もしません。統合の挙動自体は変えません。再生停止、画面更新抑制、重複表示優先の範囲を設定し、[変更セッションの統合契約](mutations.md#フォルダ統合と確定後の保守)へ渡します。DB失敗時の全体補償や、独自の通知経路は持ちません。

停止後のモデル準備では、元フォルダが確定済み利用者BMSルート本体または空の登録子を含む祖先なら統合を拒否します。元先の実体・DB・登録を保持し、登録の解除・付替えを行いません。登録を含まない通常作品は、登録配下や名前の先頭が同じ兄弟でも通常の統合条件で処理できます。

同じ準備で、統合にリンク（シンボリックリンクとジャンクション）が含まれる場合も、物理変更と変更セッションの開始より前に拒否します。リンクのファイルを作品の一部として移動・削除する危険を避けるためで、従来受理していた統合を拒否する互換性変更です。OneDrive のプレースホルダーなど、リンク以外の再解析ポイントは拒否の対象にしません。次のいずれかがリンクなら拒否します。

- 統合元フォルダ本体と、その配下の全階層のファイル・ディレクトリ（隠し・システム属性を含む）
- 統合先フォルダ本体
- 統合元の配下のディレクトリと同じ相対パスにある、統合先の既存ディレクトリ
- 統合元の配下のファイルと同じ相対パスにある、統合先の既存ファイル

統合先にある、統合元と対応しないリンクと、統合元・統合先の祖先のリンクは拒否しません。検査はリンク先へ入らず、リンク自身の属性と再解析タグで判定します。リンク先が消えたリンクもリンクとして拒否し、統合元本体がリンクなら「入力元の欠落による終了」より先に拒否します。リンクでなく存在しない統合元は、従来の欠落による終了のままです。拒否では、統合元・統合先・リンク先のファイル、DB、カタログ、BMSルートの登録を変えず、変更セッションの失敗の事実を持たない未適用として返し、案内を一件だけ出します。汎用の統合失敗の案内と変更セッションの報告は重ねません。列挙や属性取得の失敗（アクセス拒否を含む）でリンクの有無を確認できない場合も、同じ保全で拒否し、確認できないことを伝える別の案内を一件だけ出します。拒否の後は受付を解放し、次の統合を受け付けます。変更セッション開始前の既存の拒否（Busy、カタログ未収束、登録ルートを含む統合元）は、リンクの拒否より先に働きます。検査は[`FolderLinkInspector`](../../../BeMusicSeeker/Models/Utils/FileOperations/FolderLinkInspector.cs)がモデル側の独立した部品として持ち、統合先メニュー・Ctrl+G・モデル入口のすべてに同じ経路で適用します。

入力元を除く所持集合に同じMD5が残る譜面は移動対象から除きます。譜面名だけの衝突は採番し、リソースの衝突は既存の上書き設定に従います。残存物の削除には[確認済みコピーの条件](file-db-consistency.md#入力元の後片付け)を適用します。

### 統合候補一覧

重複ツリーの子の先頭に「統合候補一覧」のノードを置きます。ノードは重複検索結果が一件以上あるときだけ現れ、未解析・0件・準備が走っていない失効・ライブラリの解除では現れません。結果が失効して準備が走っている間は、グループ項目と同じ規則で残し、結果が一件以上ある間の同期ではノードを除去・置換しません。ツリーの子は、ノードの項目（`DuplicateMergeCandidateListNode`、0〜1件）とグループ項目を連結した一覧（`MaintenanceTreeViewModel.DuplicateTreeChildren`）で、元の集合の個別の変更を位置を補正した個別の変更として中継し、Resetへ置き換えません。元の集合のResetはResetとして中継します。`DuplicateTreeItems` は変えません。

ノードの選択は、画面受付を他の重複表示と同じ入口（`DuplicateViewContext.ForCandidateList`）に乗せます。未解析なら共通受付を非待機で取得して競合時は既存のBusy案内で未開始とし、実行中の準備へ合流し、準備済みなら新しい受付を取らず、後から選んだ画面を優先し、旧ライブラリの候補を採用せず、失敗は一度だけ通知し、取消は通知しません。譜面一覧の入力は明示的に空で、一覧の構築（TITLE・ARTISTの解決を含む）はUIスレッド外の画面構築で行い、採用は空の譜面一覧の採用と合わせて、その画面受付と入力の重複結果が現在のものである場合だけ行います。構築はノードの選択時に行い、事前計算しません。

一覧は専用の表で、一行は一フォルダ、最小単位は重複グループです。一フォルダだけのグループも表示します。グループ内の行はPATHの昇順（大文字小文字を無視）、グループの既定の並びは重複検索結果の順で、背景はグループごとに交互になります。表示中は、譜面一覧の表、メインの件数表示、メインの検索欄、保留譜面の操作バー、プレイログの集計バーを表示せず、隠れた譜面一覧は行0件・選択なしです。

| 列 | 内容 |
| --- | --- |
| SELECT | グループ結合のチェックボックス。初期値はオフ。統合不可は操作できない。 |
| STATUS | グループ結合の文字。「未確認」、または[安全性確認](#安全性確認)の結果（段階と、成立した理由のすべて）。ツールチップに理由と該当を出す。 |
| KEEP | 残すフォルダのラジオボタン。グループで常に一つ。 |
| PATH、FOLDER | フォルダのパスと名前。 |
| TITLE、ARTIST | そのフォルダ（子孫を含む所持範囲）の導入先候補プロフィールの実在最頻ペアの表示値。有効なペアがなければ両方空。 |

列の幅と順序は起動中だけ保持し、設定へ保存しません。初期のKEEPは、グループの譜面（兄弟譜面を含む）のうちそのフォルダ直下にある所持譜面数が最多のフォルダで、同数ならPATH昇順の先頭です。登録範囲（手動統合が移動元として拒否するのと同じ判定。構築ごとに登録を一度捕捉）のフォルダが一つならKEEPをそこへ固定し、二つ以上ならPATH昇順の先頭へ固定して「統合不可：登録ルート」とし、SELECTをオフかつ無効にします。一フォルダのグループはKEEP固定です。固定のKEEPと統合不可のSELECTは操作しても変わりません。

状態（SELECT、KEEP、確認結果）は `DuplicateGroup` のインスタンスに対応付けて持ちます。重複結果の再検索で同じインスタンスが維持されたグループは状態を保ち、新しいインスタンスは初期値、消えたグループの状態は採用時に破棄します。ライブラリの解除・交換ですべて破棄し、永続化しません。別の画面へ移って戻っても、状態・並べ替え・検索語・除外語を保ち、再検索で結果が変わっても並べ替え・検索語・除外語を新しい結果へ当て直します。

- 並べ替え: 列見出しで、グループの先頭行の値を使ってグループ単位に並べます。SELECTはオフが先、STATUSは表示文言の比較で、KEEPは対象外です。同値は既定の結果順で、グループ内の行順は変えません。
- 検索: 専用の検索欄で既存のキーワード検索の構文を使います。項目は path、folder、title、artist、status で、条件は行単位で評価し、一致した行を一つでも持つグループの全行を残します。
- 除外: 検索欄の右の専用の除外欄で、検索欄と同じ構文（入力支援・履歴・保存は持たない。アクセシブル名とプレースホルダーで除外用と分かる）を行単位で評価し、条件を満たす行を一つでも持つグループを全行まとめて一覧から除きます。空なら何も除かず、表示するのは「検索で残るグループのうち、除外で除かれないグループ」です。除外語は検索語と同じ寿命（別の画面との行き来、結果の変更、ライブラリの交換の後も保つ）で、表示言語の切替の直後には再評価せず、次の作り直しまで保ちます。除外で非表示になったグループのSELECT・KEEP・確認結果は保ち、除外を解除すると同じインスタンスで現れます。除外欄は検索欄と同じく一覧の表示中だけ表示します。STATUSの語（`status:`）の検索と除外は、要注意・統合不可のすべての理由名に一致します。
- 件数表示: 「表示中 n グループ／選択 m グループ」。n は検索と除外を当てた後の表示中のグループ数、m はそのうちSELECTがオンのグループ数です。行集合の序数（交互の背景）も、除外後の表示順に振り直します。

| 操作 | 結果 |
| --- | --- |
| SELECT・KEEPのセルのクリック | そのグループのオン・オフ、またはそのフォルダのKEEP。結合列では先頭でない行でも同じ。有効なSELECT・KEEPのダブルクリックは遷移しない。選択は、SELECT（結合列）のクリックがそのグループの全行、KEEPのクリックがその行だけ（[結合列の選択](../ui/table-view.md#結合列の選択)）。 |
| それ以外のセルのダブルクリック、行でのEnterキー | 重複ツリーの対応するグループを展開し、そのフォルダノードを選択して既存のフォルダ表示へ移る。画面外で未生成のグループでも同じ。現在の結果で解決できなければ何もしない。 |
| Space | 選択行が属するグループごとに一度。統合不可を除いてオンとオフが混在していればすべてオン。 |
| 右クリック | 「チェックを入れる」「チェックを外す」（グループごとに一度、統合不可は変えない）、「このグループを確認」（[安全性確認](#安全性確認)）、「エクスプローラーで開く」（存在する選択フォルダごとに一度）。 |
| 「表示中をすべて選択」「表示中をすべて解除」 | 検索と除外を当てた後の表示中のグループだけに作用する。統合不可は選択しない。 |
| 「優先統合先…」 | ディレクトリを追加・削除するウィンドウ。OKで全グループ（非表示を含む）へ適用し、優先先と同じかその配下にあるフォルダがグループ内でちょうど一つならKEEPにする。該当なし・二つ以上・固定のグループは変えない。判定はパス成分の境界で行い、大文字小文字と末尾の区切りを無視する。登録内容は起動中だけ保持し、キャンセルは何も変えない。 |

#### 安全性確認

グループごとに、統合しても作品として意味が崩れないかを、読み取りだけで確認します。ファイルとDBは変更せず、結果（段階と理由）を一覧の状態へ書き込みます。ファイルを動かす一括整理は含みません。

| 入口 | 動作 |
| --- | --- |
| ツールバー「選択グループを確認」 | 表示中でSELECTがオンのグループを対象にする。対象が0件なら、受付・進捗・案内なしで何もしない。 |
| 右クリック「このグループを確認」 | 選択行が属するグループを、グループごとに一度、SELECTの状態によらず対象にする。解析不能で統合不可になったグループの再確認もここから行う。 |
| ツールバー「サイズと更新日時が同じファイルは読まずに同一とみなす」 | 既定はオンで、アプリの起動中だけ保持する（起動のたびにオンから始まり、ライブラリを交換しても保つ）。確認の開始時の値を使い、チェックに触れずに確認を始めればオンで検査する。利用者がチェックを入れる操作のたびに、内容が異なるファイルを同一と判定することがある旨を一件案内し、起動時の既定のオンと外す操作では案内しない。 |

確認は、共通のライブラリ変更受付を非待機で取得します（[操作の競合ポリシー](../core/operation-concurrency-policy.md)）。競合時は既存のBusy案内を一件だけ出して未開始とし、検査・進捗・状態を変えません。受付が閉じている（終了の要求中など）ための不成立は案内せずに未開始とし、検査・進捗・状態を変えません。検査の開始から結果の書込みまで受付を保持し、書込みの後、結果の案内の前に解放します。確認中はモーダルの進捗（既存の進捗画面。所有者はメインウィンドウ）に「[何番目/総数] 代表フォルダ名」を示し、他のアプリ内操作を受け付けません。代表フォルダ名はグループのKEEPのフォルダ名です。取消は、処理中のグループを最後まで処理して書き込んでから止まり、着手しなかったグループの確認結果とSELECTは変えません。入出力以外の予期しない例外は、完了済みのグループの結果を書き込み、受付を解放した後に、既存の操作失敗の通知を一度出します。

検査の対象は、グループの各フォルダの本体と配下の全階層（隠し・システム属性を含む）です。リンク（シンボリックリンクとジャンクション）の先へは入らず、フォルダごとの列挙は一回で、走査と読取りはUIスレッド外でグループごとに逐次行います。モデルのロックとDBトランザクションを保持したままファイルを読みません。一フォルダだけのグループは、フォルダ本体のリンクと解析不能だけを検査し、配下を走査しません（それ以外は安全です）。確認時にグループのフォルダが存在しなければ解析不能です。

確認結果は、段階（安全／要注意／統合不可）と、同時に成立し得る理由の一覧で、理由ごとに該当するフォルダと相対パス（または譜面名と参照の原文）を保ちます。統合不可の理由が一つでもあれば統合不可、なければ要注意の理由が一つでもあれば要注意、どちらもなければ安全です。理由は次の順に並びます。

| 段階 | 理由 | 条件 |
| --- | --- | --- |
| 統合不可 | リンク | いずれかのフォルダの本体または配下にリンクがある（KEEP側も含む）。一フォルダのグループは、本体がリンクの場合だけ。 |
| 統合不可 | 登録ルート | 登録範囲のフォルダが二つ以上ある。一覧の構築時に付き、確認の後も保つ（確認の結果で上書きせず、SELECTは無効のまま）。 |
| 統合不可 | 親子関係 | グループ内のフォルダ同士が同一または親子。名前の先頭が同じだけの兄弟は該当しない。 |
| 統合不可 | 型の衝突 | 同じ相対パスが、あるフォルダではファイル、別のフォルダではディレクトリ（大文字小文字を無視）。 |
| 統合不可 | 別グループを巻き込む | 二フォルダ以上のグループのいずれかのフォルダが、現在の重複結果の別のグループ（確認対象外・検索で非表示・一フォルダのグループを含む）のフォルダと同一または親子。 |
| 統合不可 | 解析不能 | 権限・列挙・読取りの失敗（共有違反・削除保留のディレクトリ、比較が必要なファイルの読取りの失敗、読めない所持譜面）。該当するパスを持つ。 |
| 要注意 | 親ディレクトリ参照 | 移動元になるフォルダ（KEEP以外）の直下・配下の所持譜面が、使用中のリソース参照に親ディレクトリ参照（`ChartResourceReference.RestrictsAutomaticDestination` と同じ判定）を持つ。 |
| 要注意 | 同名音源の拡張子違い | 同じ相対ディレクトリ・同じ拡張子なしの名前の音源が、グループ内の別のフォルダに異なる拡張子である。同じフォルダ内、別ディレクトリ、同じ拡張子は該当しない。 |
| 要注意 | 別内容ファイル衝突 | 同じ相対パスの資源が、グループ内の二つのフォルダで別内容と判定される。 |
| 要注意 | 同一内容ファイルなし | 資源を持つフォルダが二つ以上あり、「同じ相対パスで同一内容と判定された資源を共有する」関係で一つに連結しない（全フォルダに共通する一ファイルは要求しない）。資源を持たないフォルダは外し、保護対象だけの一致は数えない。内容を比較できなかった組は、連結しているものとして扱う。 |
| 要注意 | 配下に別の譜面フォルダ | 移動元になるフォルダの配下の子孫フォルダに、所持譜面がある。 |

譜面ファイルは拡張子 `.bms`・`.bme`・`.bml`・`.pms`・`.bmson`、保護対象は `.bmx`・`.pmx`・`.txt`、資源はそれらを除くファイル、音源は `.wav`・`.ogg`・`.mp3`・`.flac` です。拡張子と相対パスの比較は大文字小文字を区別しません。所持譜面はライブラリの所持集合にある譜面だけで、所持していない譜面ファイルは該当しません。同じ相対パスの保護対象が二つ以上のフォルダにある場合は、段階を変えずに「保護対象名の衝突」を付記します。

同じ相対パスの二つの資源は、次の順で内容を比較します。

1. サイズが異なれば別内容（読まない）。
2. 簡易確認がオンで、サイズと更新日時（最終書込み時刻）が完全に一致すれば、読まずに同一内容。
3. サイズが全量読取りの上限（192 KiB）以下なら全量、超えるなら先頭・中央（ファイルの中心をまたぐ）・末尾の各64 KiBの区間を読み、XXH3（128ビット）の値が等しければ同一内容、異なれば別内容。

一回の確認の中で、同じファイルを相手ごとに読み直さず、ハッシュを確認の外へ保存しません。相手のいないファイルは読みません。大きいファイルの未読領域にだけ差がある場合は同一内容と判定されます（全バイトを比べる経路は持ちません）。確認結果は使った比較方法（全量／部分／サイズと更新日時）の組の数を保ちます。上限と区間長は `DuplicateMergeSafetyPolicy` で一か所に定めます。

KEEPに依存する理由（親ディレクトリ参照、配下に別の譜面フォルダ）は、確認時にフォルダ別の事実として保持し、KEEPが変わっても再走査せず、段階と理由を導き直します（SELECTは変えません）。それ以外の理由はKEEPによりません。所持譜面は、既存の索引から対象フォルダの範囲だけを取得し、リソース参照が未取得の譜面は、ロックの外で譜面ファイルを解析して得ます。参照を得られない譜面は解析不能です。

結果は次のとおり反映します。

- 確認を終えたグループのSELECTを「安全ならオン、それ以外はオフ」に置き換えます（統合不可はオフで無効）。解析不能で統合不可になったグループは、再確認で安全になればオンで有効に戻ります。
- 複数グループの書込みは一括の窓口（`DuplicateMergeCandidateListOwner.WriteVerifications`）で行い、検索・除外・並べ替えがSTATUS・SELECTに依存するときだけ、行集合の差し替えを全体で一回にして新しい値で当て直します。SELECT・KEEPの利用者操作では並べ直しません。
- 確認中に状態表から消えたグループの結果は捨て、結果の案内のどの件数にも数えません。
- 確認の終端で、結果を一度だけ案内します。「{n}グループを確認しました。安全 {a}／要注意 {b}／統合不可 {c}。安全な{a}グループにチェックを入れました。」で、取消で着手しなかったグループがあれば「未確認 {d}」を加えます。安全が0件なら「チェックを入れました」の文を、確認を終えたグループが0件なら案内を出しません。
- STATUSは、未確認が「未確認」、安全が「安全」（付記があれば「安全（保護対象名の衝突）」）、要注意・統合不可が「要注意：<理由>, <理由>…」「統合不可：<理由>, <理由>…」で、成立した理由をすべて、上の表の順（統合不可の理由、次に要注意の理由）に、表示言語によらない「, 」（譜面一覧のWARNING列と同じ区切り）で並べます。理由が一つなら一つだけです。統合不可と要注意の理由が混在する結果は、段階は統合不可のまま、両方の理由を並べます。保護対象名の衝突は、要注意・統合不可でもツールチップにだけ付記します。STATUSの文言による検索と並べ替えは、この文言を対象にします。
- STATUSのツールチップは、理由ごとに説明と該当（フォルダのPATH昇順、次に相対パスまたは譜面名の昇順。どちらも大文字小文字を無視）を出し、一つの理由につき10件を超える分は「ほか{n}件」と省略します。別内容ファイル衝突は「統合すると、上書きの設定に従って一方だけが残ります」、保護対象名の衝突はファイル名と「上書きの設定によっては、内容が異なっていても一方だけが残ります」を添えます。安全のツールチップは、比較した同名の資源の組の数と比較方法（混在すれば方法ごとの件数）を出します。

一括整理（[統合候補一覧からの一括整理](#統合候補一覧からの一括整理)）は、`DuplicateMergeCandidateListOwner.CaptureSelectedGroups`（表示中でSELECTがオンのグループを、表示順に、グループ・PATH昇順のフォルダ・KEEP・固定の有無・登録範囲のフォルダ・捕捉時点の確認結果を持つ変更不能な対象として捕捉）を使います。確認結果の書込みは `WriteVerifications`（UIスレッドで確認結果を書き込み、現在の状態表にないグループは捨てる）、確認の手順は `DuplicateMergeVerificationWorkflowOwner` が担います。

### 実行中の進捗行

フォルダ統合と同一フォルダ内の重複削除は、背景の変更処理の実行中だけ、画面下部の進捗一覧に行を一つ表示します。行は件数のない不定進捗で、取消などの操作を持ちません。詳細には、統合では統合元フォルダ、重複削除では対象フォルダを示します。表示の体裁は[ステータスバーの進捗](../runtime/statusbar-progress.md#独立した操作行)に従います。

行は、共通受付を取得し、必要な確認がすべて承認された後、対象別受付の取得・再生停止・物理変更を始める前に表示します。確認を表示しない設定では、統合は共通受付の取得後すぐ、重複削除は事前準備とフォルダ単位の確認の後に表示します。背景の変更処理が成功・対象別受付の使用中・失敗のいずれで終わっても、結果の報告と呼出し元への返却より前に行を消します。既存宛先の復元不能では、アプリ終了への接続より前に消します。

共通受付の拒否、確認の拒否・取消・表示失敗、重複削除の事前準備の失敗、削除対象なしでは行を表示しません。進捗通知の受け手の失敗は警告として記録し、操作の結果・受付の解放・報告を変えません。統合候補一覧からの一括整理は、モーダルの進捗画面を使うため、この行を表示しません。

### Ctrl+Gと同一フォルダ内の整理

元グループのフォルダが一つなら同一フォルダ内の重複削除を行います。複数フォルダで有効な統合先候補がゼロなら、統合もメニュー展開も行いません。有効候補があるときは元グループの件数に従い、二つなら選択元からもう一方へ統合し、三つ以上なら統合先メニューを展開します。三つ以上から有効候補が一つに絞れても、直接統合へ変更しません。

統合とハッシュ整理が成功したら、途中で別画面や別の重複グループへ移っていても、操作前に捕捉した次群または存続群を選択します。次の対象がなければ自動選択せず、捕捉した所属が消失した場合は空表示と選択解除を行います。同名の別群・現在位置の別群・全件へ読み替えません。処理失敗時は成功後選択を行いません。

標準構成では、自動選択に必要な最新グループを現在画面に依存せず親の共通受付内で準備します。準備失敗は元操作の失敗へ合成し、先行した確定事実・元の失敗と受付の回収を保ちます。表示所有者が未接続なら追加の準備を始めず、未接続だけを失敗条件にしません。

同一フォルダ内ではMD5ごとに一件を残します。更新日時が古いもの、同日時ならファイル名が短いものを優先します。設定に応じて確認し、`RemoveLibraryCharts` からごみ箱へ送ります。削除数は計画件数ではなく、削除APIの成功で確認した対象数です。個別の物理削除失敗後の独立対象は処理できますが、カタログ・必須反映の失敗では次グループへの成功時選択を行いません。

### 重複グループの一括整理（モデルの入口）

複数の重複グループを一回の操作で整理する入口です。`BMSLibrary.OrganizeDuplicateGroups` と保存窓口 `IDuplicateMaintenanceStore.OrganizeDuplicateGroups` が、要求と結果の値までを扱います。確認・進捗や結果の表示・表示文言は[統合候補一覧からの一括整理](#統合候補一覧からの一括整理)が定め、この節は定めません。

要求は、順序付きのグループの列、取消の合図（`CancellationToken`）、進捗の受け手（任意）です。グループは KEEP のフォルダ一つと、移動元のフォルダの列（PATH 昇順、0 件以上）を持ち、移動元が 0 件のグループを一フォルダのグループと呼びます。

#### 一つの操作

一回の一括整理は、一つの共通受付と一つの変更セッションで行います。カタログ・DB・索引・LR2同期・通知への反映と必須保守は、全グループの物理処理の後に一回だけ行い、確定・索引反映・必須保守の回数はグループ数・移動元数に比例しません。Busy・カタログ未収束・管理出力の保護を取れない場合は、物理変更の前に操作全体を未適用で返し、全グループを「未処理」にします。案内は既存の拒否の案内だけで、グループごとの理由を案内に積みません（共通受付の使用中はモデルが案内を出さず、受付を取る入口が表示します）。管理出力の保護は、要求の全フォルダの和集合を、最初の物理変更の前に一回だけ取ります。

#### グループの処理

- 二フォルダ以上のグループは、移動元を与えられた順に KEEP へ統合します。ファイルの移動・譜面名の採番・リソースの上書き・入力元の後片付けは、単体の統合と同じ共通のパッケージ処理と設定を使い、一括用の規則を持ちません。
- 移動元ごとに、実際に KEEP へ移る主ハッシュ（今回の所持判定に該当しないもの）に限って、移動元直下の同じ主ハッシュの譜面を、移動の前に一件へ整理して余分をごみ箱へ送ります。KEEP に既にある主ハッシュは整理せず、既存の統合の入力元の後片付けに任せます。ごみ箱へ送れた譜面は統合の入力とカタログ事実の両方から除き、送れなかった譜面は一緒に移します。型の衝突などで事前に拒否する移動元では、重複を整理しません。
- 全移動元の後に、KEEP 直下に元からある同じ主ハッシュの譜面を一件にします。一フォルダのグループは、そのフォルダ直下の同じ主ハッシュの譜面を一件にします。
- 重複整理は、同一フォルダ内の整理と同じ一つの選定規則（`DuplicateChartCleanupSelector`。更新日時が古いものを残し、同日時ならファイル名が短いものを残す。同点は列挙順で、順序を契約にしない）を使い、主ハッシュごとに必ず一件を残します。フォルダを削除せず、確認を求めず、フォルダ全体の削除を行いません。削除の確定事実は、行パス変更通知の方針を抑止で操作全体の変更セッションへ加えます。個別の削除の失敗は停止の理由にせず、その譜面を残して数えます。

#### 最後のコピーの保護

「既に所持している」の判定と、入力元の残存物を削除してよい裏付けには、次だけを使います。

1. 要求に含まれる全移動元（拒否・未処理・失敗になるグループの移動元を含む）の配下を除いた、操作開始時の確定済みの所持。
2. 今回の操作で実際に配置へ成功した譜面。

未着手・失敗・拒否・取消で残る移動元の譜面と、予約しただけの宛先を裏付けにしません。KEEP に譜面 X、移動元 A に X と Y、移動元 B に Y があるとき、Y は KEEP にちょうど一件残り、A と B の Y が両方失われません。B が別のグループの移動元で、そのグループが拒否・取消・未処理・失敗になる場合も同じです。同じ譜面が複数の移動元にだけある場合は、与えられた順で最初の移動元のコピーが KEEP に残ります。手動で順に統合した場合との差として許容します。別のグループの移動元の譜面が、この操作で先に別の KEEP へ配置済みなら、その譜面は移しません。操作前に所持していた譜面のコピーは、確定の失敗・途中の物理失敗・取消でも、どの主ハッシュについても一件も残らない結果になりません。

#### 書込み前の拒否

次のグループは、そのグループの物理変更の前に理由付きで「スキップ」にします。ファイル・DB・登録を変えず、変更セッションに失敗の事実を残さず、独立した次のグループへ進みます。

| 理由（`DuplicateBatchOrganizeSkipReason`） | 条件 |
| --- | --- |
| `LinkFound`・`LinkCheckFailed` | 移動元ごとに、手動統合と同じ検査（`FolderLinkInspector.InspectMerge`）でリンクが見つかる、または確認できない。一フォルダのグループは、フォルダ本体だけを `FolderLinkInspector.InspectFolder` で検査し、配下のリンクは配置を動かさないため対象にしない。二フォルダ以上のグループも、KEEP 直下の重複整理を移動元の有無や結果によらず行うため、KEEP 本体を同じ方法で必ず検査する（移動元がすべて欠落していても検査する）。 |
| `RegisteredRoot` | 移動元が、登録 BMS ルート本体、または空の登録子ルートを含む祖先。KEEP が登録ルートの場合は統合できる。 |
| `NestedFolders` | 同じグループのフォルダ同士が同一または親子。 |
| `OverlapsOtherGroup` | 二フォルダ以上のグループのいずれかのフォルダが、要求内の別のグループのいずれかのフォルダと同一または親子。別のグループの移動元の中にある一フォルダのグループは、そのグループ自身は処理する。 |
| `FolderMissing`・`InvalidPath` | 一フォルダのグループのフォルダが存在しない。フォルダのパスが空、または正規化できない。 |

#### 部分失敗と取消

型の衝突・移動元の欠落・KEEP の欠落・所持譜面なしの移動元は、その移動元を理由付き（`DuplicateBatchOrganizeSourceReason`）で残して、同じグループの次の移動元へ進みます。予期しない物理失敗（例外・受取りの失敗を含む）が起きたら、その移動元を物理失敗として残し、そのグループの残りの移動元と後続の全グループを未処理として止め、それまでに成功した分だけを確定します。既存宛先を復元できなかった事実は、既存の統合と同じ形で操作の結果に残ります。

取消はグループの開始前にだけ確認します。処理中のグループは最後まで進み、残りのグループは未処理になります。取消は失敗として扱わず、確定・入力元の後片付け・必須保守は取消の対象にしません。

確定（永続化）に失敗した場合は入力元の後片付けを行わず、物理的に成功していたグループ（統合した移動元またはごみ箱へ送った譜面があるもの）を失敗にし、統合した移動元は確定していない扱い（`NotCommitted`）で残した側へ移します。スキップしたグループは変わりません。ごみ箱へ送った数は削除の成功を確認した実数で、確定に失敗しても変わりません。

#### 状態と結果

| 状態 | 条件 |
| --- | --- |
| 完了 | 二フォルダ以上のグループは、移動元をすべて統合でき、重複整理の削除にも失敗がない。一フォルダのグループは削除に失敗がない（削除対象が 0 件でも完了）。 |
| 一部完了 | 統合できた移動元が一つ以上あり、残った移動元がある。または統合はすべてできたが、重複整理の個別の削除に失敗がある。一フォルダのグループで個別の削除に失敗がある場合も同じ。 |
| スキップ | 書込み前の拒否。 |
| 失敗 | 二フォルダ以上のグループで、移動元を一つも統合できなかった。ごみ箱へ送った数は別に報告する。確定に失敗した操作では、物理的に成功していたグループも失敗。 |
| 未処理 | 取消、または予期しない物理失敗による停止で、開始しなかった。 |

進捗は、取消の確認の後、グループの開始ごとに 1 始まりの番号・総数・KEEP を同期で受け手へ渡し、全グループの物理処理の後・確定の前に、反映段階への移行（`DuplicateBatchOrganizeProgressKind.Applying`）を一回だけ渡します。反映段階への移行は、操作を受理した場合だけ、取消や停止の後も通知し、受理しなかった場合は通知しません。モデルのロックを保持したまま呼ばず、受け手の例外で結果を変えません。最新値への集約は呼出し側が行います。結果は、操作全体の受取り（既存の統合と同じ、変更セッションの終端と必須保守の結果）、要求と同じ順のグループ結果（KEEP・状態・スキップ理由・統合した移動元・残した移動元とその理由・ごみ箱へ送った数・送れなかった数）、取消されたか、受付を取って処理を始めたかです。グループごとの KEEP・移動元・結果は、受付の解放後にログへ記録します。

統合が一件以上成功したら、成功した全 KEEP を対象に、既存の統合と同じ必須保守を一回だけ行います。保守の失敗・取消は確定を取り消さず、完全成功として報告しません。単体のフォルダ統合と同一フォルダ内の重複整理の利用者に見える挙動は変えません。

### 統合候補一覧からの一括整理

統合候補一覧のツールバー「選択グループを整理」は、表示中でSELECTがオンのグループを、上の[モデルの入口](#重複グループの一括整理モデルの入口)で一回の操作として整理します。入口は `DuplicateMaintenanceWorkflowOwner.OrganizeSelectedGroupsAsync` で、確認・再生停止・進捗・結果の案内・失敗の報告を扱い、`MainWindow` はクリックの接続と想定外の例外の通知だけを担います。安全性確認を再実行せず、未確認・要注意のグループも確認ダイアログの承認で対象にし、確認結果とSELECTを書き換えません（統合不可は選択できないため含まれません）。

#### 対象と受付

要求のグループの順は、並べ替えと検索を当てた後の表示順です。各グループは、KEEPと、KEEP以外のフォルダをPATH昇順（大文字小文字を無視）に並べた移動元を持ち、一フォルダのグループの移動元は空です。対象が0件なら、受付・確認・Busy案内・再生停止・進捗・モデルの呼出しのどれも行いません（受付を外部が保持していても案内しません）。

共通のライブラリ変更受付は待たずに取得します（[操作の競合ポリシー](../core/operation-concurrency-policy.md)）。競合時はBusy案内を一件だけ出して未開始とし、確認・再生停止・進捗・モデルの呼出しをせず、SELECT・KEEP・確認結果を変えません。予約せず、解放後に自動実行しません。受付が閉じている（終了の要求中など）ための不成立は案内せずに未開始とします。

#### 確認

受付の取得後、確認を必ず一回表示します（OKとキャンセル、既定はキャンセル）。統合の確認を表示しない設定（`ShowDuplicateFileCheckConfirmMsg`）でも表示します。キャンセル・閉じる・表示の失敗では、再生停止・進捗・画面更新の抑制・モデルの呼出しへ進まず、受付を解放して状態を変えません。表示の失敗は一回だけ報告します。確認の表示中は受付を保持し、モデルを呼ばず、ライブラリの書込みロックとDBトランザクションを持ちません。

確認文は、通常の本文と、警告色で表示する警告用の本文（`UiMessageRequest.WarningMessageBoxText`）に分け、次の行を含みます（文言は表示リソースで、各本文の行の順は上から）。

| 本文 | 行 | 条件 |
| --- | --- | --- |
| 通常 | 「{n}グループを整理します。」 | 常に |
| 通常 | 「{m}グループは、移動元の{k}フォルダをKEEPのフォルダへ統合します。移動元のフォルダは削除され、元に戻せません。」 | 二フォルダ以上のグループがあるときだけ（mはそのグループ数、kは移動元の合計） |
| 通常 | 「同じフォルダにある重複した譜面は、一つを残してごみ箱へ送ります。」 | 常に |
| 警告用 | 「未確認 {u}グループ／要注意 {c}グループを含みます。」 | 未確認または要注意が一件以上あるとき。0件の側は省き、このときダイアログのアイコンは警告（どちらも0なら質問） |
| 警告用 | 「内容が異なる同名のファイルは、上書きの設定に従って一方だけが残ります。」 | 確認結果に別内容ファイル衝突（要注意の理由）または保護対象名の衝突（付記）を持つグループがあるとき |

警告用の本文は、該当する行を上の順に改行で並べ、該当がなければ出しません。確認の回数・ボタン・既定・アイコンの条件は変わりません。

#### 実行

承認後は、モーダルの進捗（既存の進捗画面。所有者はメインウィンドウ、取消ボタンつき）を開き、その操作の内側で、再生の停止、画面更新の抑制、モデルの呼出し（UIの文脈の外）の順に進みます。再生停止の失敗・取消ではモデルを呼ばず、抑制の解除と受付の解放を行います。画面下部の進捗行（[実行中の進捗行](#実行中の進捗行)）は、一括整理では表示しません。

進捗は「[何番目/総数] KEEPのフォルダ名」を示します。物理処理のループの後・確定の前に、モデルが反映段階への移行を一回だけ通知し（受理した操作は、取消や停止の後も通知します。受理しなかった場合は通知しません）、画面は「ライブラリへ反映中」を示して取消ボタンを無効にします。この間に取消を要求しても、結果に取消の行を付けません。進捗の受け手は待たずに戻り、最新の値だけを保持し、画面への投稿は一定の間隔（約50ミリ秒）と終端で間引きます（途中のグループ開始の値は欠けてよく、番号は後退しません）。「ライブラリへ反映中」は以後のグループ開始の値に上書きされず、必ず最後に表示されます。受付を保持したままUIの完了を同期的に待ちません。

取消はモデルの取消の合図へ届き、処理中のグループを終えて止まります（確定・後片付け・必須保守は取消の対象にしません）。進捗画面は、取消の扱いを操作へ任せる設定（`ProgressDialogSettings.OperationOwnsCancellation`）で開きます。この設定では、取消を要求した後も進捗の表示を更新し、操作の例外と結果を取消へ置き換えずに返します。既存の利用先（保留導入・音声変換・安全性確認）は既定の動作のままです。進捗の仕組みが取消を返しても、結果の案内と報告を省きません。

受付は、モデルの終端・重複結果の準備（[更新の集約と後続保守](#更新の集約と後続保守)の内部継続）・画面更新の抑制の解除まで保持し、結果の案内の前に解放します。受理済みの変更で重複結果が失効した場合（一部完了・取消で一部だけ実行・確定失敗の前に物理変更があった場合を含みます）は、同じ受付の中で新しい重複結果を準備します。失効していなければ準備しません。

#### 終端

- 操作全体が受理されなかった場合（Busy・カタログ未収束・管理出力の保護）は、モデルが積んだ案内だけを一回出し、結果の案内も報告も出しません。
- 既存宛先を復元できなかった場合は、受付と活動の解放・進捗を閉じた後に、単体の統合と同じ終了手順を一回呼び、報告も結果の案内も出しません。
- 確定・反映・保守の失敗は、単体の統合と同じ報告（`FileDbMutationReport`）を一回出し、その後に結果を一回出します。再生停止の失敗・モデルの例外・確認の表示失敗など、モデルの結果がない経路は、報告を一回だけ出し、結果の案内を出しません。重複結果の準備の失敗は報告へ合成し、その後に結果を出します。移動元ごとの事前拒否（型の衝突など）は結果の理由で案内し、報告は出しません。
- 結果は最後に一回だけ案内します。「{n}グループを整理しました。完了 {a}／一部完了 {b}／スキップ {c}／失敗 {d}／未処理 {e}。」（0件の区分も出す）に、ごみ箱へ送った譜面の合計が一件以上なら「重複した譜面 {t}件をごみ箱へ送りました。」（確定に失敗したグループの分も数える）、取消されていれば「取り消されました。」（未処理の件数は見出しの「未処理」で示す）を続けます。完了以外のグループは、要求順に最大20グループまで「KEEPのパス：状態（理由）」と、残った移動元ごとの「移動元のパス：理由」を並べ、超える分は「ほか{n}グループはログを確認してください。」とします。全グループが完了でも結果は案内します。
- 状態の「（理由）」は、スキップは拒否の理由、確定の失敗による失敗は「確定の失敗」、そうでなく個別の削除に失敗があれば「削除の失敗」で、それ以外は付けません。両方が重なる場合は、確定されていないことを示すため「確定の失敗」を優先します。理由の文言は、モデルの分類値（`DuplicateBatchOrganizeSkipReason`・`DuplicateBatchOrganizeSourceReason`）のすべてに表示リソースを対応させます。

#### 操作の後の一覧

重複結果が失効して準備された後は、統合候補一覧を表示したまま作り直し、別のグループや別の画面へ自動で移りません。整理で解消したグループは一覧から消え、影響しなかったグループは同じ状態のインスタンスのままSELECT・KEEP・確認結果を保ちます。再判定されたグループは初期値（未確認・SELECTオフ）になります。受付を保持している間の統合候補一覧のノードの選択は、Busyで拒否されず、実行中の準備へ合流します。一覧の作り直しと結果の案内の前後は固定しません。

新しい結果にないグループの状態と行は、画面の表示中かによらず、重複データの完成通知を表示へ渡す前に取り除きます（`DuplicateMergeCandidateListOwner.RemoveUnresolvedGroups`）。結果が0件になった場合は、一覧の行が0件になり、古い行を残しません。ノードが消えた後のツリーの選択の移動は、既存の動きに従います。

### 初回解析中の画面移動

新しい重複解析は、画面選択を受理する前に共通のライブラリ変更受付を非待機で取得します。競合時は既存のBusy案内で未開始とし、有効な表示と検索条件を保ちます。解放後の予約や自動再試行は行いません。準備済み結果の表示と、進行中の同じ解析結果を参照する表示には新しい受付を要求しません。

受理した要求は、現在の選択をUIで確認してサマリーを解除し、検索文脈を更新してから解析を始めます。解析中も閲覧、検索、並べ替えと許可された別画面への移動を受理し、後発の選択を優先します。古い要求は一覧、左の選択、検索条件を変更しません。

画面受付ごとの短命な参照を、UIでの最初の副作用から一覧構築と採用まで検査します。適用済みの画面対象にもその参照を対応付け、依存更新や検索・絞込み・並べ替えでは適用済み参照を検査します。後発選択を受理してUIへ適用する前の旧表示を、新しい選択として扱いません。プレイリストと履歴の対象は既存の所有者が解決します。

データ準備は同じライブラリの実行中Taskへ合流し、キャッシュ構築、警告のカタログ確定、必要な通知配送まで同じ共通受付を保持します。行の構築や描画の完了だけを理由に、解析の受付を延長しません。新しいグループの変更通知は、ライブラリのロックをすべて解放した後、解析から戻る前に準備側のスレッドで、警告の一覧通知より先に配送します。受け手はこの通知の中で一覧の構築を失効させるため、後から配送すると、その後に受理した表示を取り消します。警告の一覧通知より先にするのは、同じ解析の警告差分を受け取る側が、グループ変更による失効とツリーの更新を反映済みの状態を観測するためです。通知先の失敗は準備の失敗にしません。完成通知はグループの変更通知の後に送り、準備元ライブラリと完成結果だけを運び、UIはその時点で有効な重複選択と検索・絞込み・並び順から表示を始めます。他画面へ移動済みならデータだけを更新し、再訪時に最新結果を表示します。未準備、解析済み0件、絞込み結果0件、所属消失による空一覧を区別します。捕捉した結果が再失効した場合は既存の失効番号と参照で不採用にします。

元の移動要求と一覧表示の完了時機を一致させる保証は設けません。データ準備の失敗はその操作結果へ返し、表示開始の同期例外と表示Taskの失敗は一覧の所有者が既存の操作失敗通知へ一度渡します。取消は失敗通知にしません。開始済みの準備・表示・通知は既存のTask集合へ登録し、停止時に回収します。重複ルートは、必要なグループが準備済みで現在も同じ選択のときに展開します。一覧描画と展開の厳密な順序は要求しません。

ライブラリの接続・交換では現在の利用者選択を保ち、旧ライブラリで捕捉した一覧候補と完成通知を採用しません。旧データ準備は必要な終端まで回収し、新ライブラリの準備へ待ち直しません。起動時に通常ルートを選択してからライブラリを接続する場合も、その通常一覧を表示します。通常一覧・部分集合・導入一覧の構築、UI採用、必要配送、表示同期と停止回収は共通の一覧処理を使います。

### 更新の集約と後続保守

受理済みのライブラリ変更で重複結果が失効したとき、現在の受理済み選択が重複表示なら、同じ存続中の権限による内部継続として必要なデータ準備を完了します。`BMSLibrary.CompleteAcceptedDuplicateDataAsync` を一つの直接接続として使い、各操作が準備の必要性や解析を独自に判断しません。準備は同期コミットの後、更新抑制の解除を通知する前に開始します。解除で流れる保留中の画面反映が失効したグループで重複ツリーを更新すると、選択中のグループが消えてツリーが親の重複ルートを選びます。この選択を、受付を保持したままの Busy ではなく、登録済みの準備への合流として受理するためです。開始時の権限の拒否や接続先の同期例外は失敗した Task として返し、各操作は抑制の解除と後片付けを続けます。待機は、短いモデルロックと更新抑制を外した後、親の共通受付を解放する前に行います。フォルダ名変更では必須の変更配送とは別に待ち、配送の失敗でも準備の終端まで受付を保持します。同期コミットからUIや別所有者のTaskを同期的に待ちません。

一般の変更で現在選択が重複以外なら失効だけを残し、次の明示表示で最新化します。明示した統合・ハッシュ整理は成功後の自動選択に必要なグループを、現在画面によらず同じ親の共通受付内で準備します。通常の警告無視や成績更新から重複解析を増やしません。内部継続の失敗でも先行した変更の確定事実と失敗結果を保持します。直接接続はライブラリの交換と一覧所有者の停止に合わせて解除します。

結果のプロパティと無効化の `DuplicateChartGroupsInvalidationVersion` は分離します。`PrepareDuplicateDataAsync` は、同じライブラリで開始済みのデータ準備Taskだけを参照し、新しい解析を暗黙に開始しません。単一の実行中準備は、共通Lを持つ実解析と必要通知だけを表します。準備済み結果の表示は既存の表示Taskとして所有し、解析の準備へ混ぜません。再利用の正本は完成キャッシュと失効版とし、完了済みTaskを別のキャッシュとして保持しません。ライブラリ交換後は旧ライブラリの準備へ合流せず、その完成通知も採用しません。交換前に開始したTaskは、既存の集合で完了まで回収します。

構築済みのプレイリスト候補索引は、所持項目の確定差分で維持します。無関係な現在値更新から先行読込みを開始せず、初回の必要な準備とライブラリ交換時の取消は索引の所有者が管理します。通常一覧、フォルダ、導入、重複の更新抑制は、解除時に保留した通知を流します。

統合後のリソース保守は `DeferOnUpdates` を使います。更新中は索引の差分反映・全体構築を行わず、次の `GetResourceHealthIndexSnapshotForView` が現在の所持対象から一度構築し、続く読取りは同じ結果を再利用します。統合と保守の成否は同じ操作結果に残し、確定後の必須反映失敗を通常成功にしません。

### 自動選択

二フォルダの統合と同一フォルダ内の削除では、操作前に現在の順序から次のグループの所属フォルダを捕捉します。三フォルダ以上の統合では移動先フォルダを捕捉し、元フォルダの消失後も存続グループを解決します。次のグループがなければ自動選択しません。見出しや処理後の隣接位置へ読み替えません。

左ツリーの重複項目は `MaintenanceTreeViewModel` が保持する一つのコレクション（`DuplicateTreeItems`）に束縛し、インスタンスは接続・解除・同期を通じて変えません。同期では、新しい結果にも含まれる同じ `DuplicateGroup` の項目を除去・置換せず、同じツリー項目のまま展開状態を保ちます。結果から消えた項目だけを除き、新しいインスタンスを結果の順序の位置へ入れ、同期後の項目の順序は結果と各位置で一致します。Reset は使いません。同期はUIスレッドだけで行い、ワーカースレッドから届く変更通知はUIスケジューラーへ配送します。更新抑制（`UiRefreshChannel.DuplicateTree`）の保留と解除時の反映の枠組みは従来どおりで、反映の中身がこの同期です。

結果が失効中（null）のときは、接続中ライブラリの重複データ準備（`PrepareDuplicateDataAsync` が返す開始済みのTask）が未完了なら直前の項目を維持し、準備が新しい結果を作って完了したらその結果へ同期します。準備が結果を作らずに終わった場合（失敗・正常終了とも）、準備が進行中でない失効、ライブラリの解除では項目を空にします。二フォルダの統合で対象グループが解消しても、無関係な展開済みグループは同じツリー項目のまま展開状態を保ち、三フォルダ以上の統合では存続グループ（新しいインスタンス）が選択・展開されます。

保守結果は自動選択に必要なグループの準備と、その変更通知の配送後に返ります。`ApplyDuplicateMaintenanceSelectionAsync` は、UIスレッド上でその時点の結果へ項目を同期してから、捕捉した所属を一度だけ解決して、仮想化された項目を必要に応じて生成・選択します。遅れて届く変更通知の到着待ちや再試行は行いません。対象が現在結果から消失した場合は左の重複選択と旧行選択を解除して空一覧にし、同名の別グループやルートへ切り替えません。この表示解決のために重複全解析を追加しません。識別は当該検索結果・保守操作内だけで使い、恒久IDや選択永続化を持ちません。

重複ツリーの選択表示は、手動選択・自動選択とも、実際に選択している一つの項目だけに付けます。仮想化の再利用に対する扱いは、左ツリー共通の[ツリーと検索欄](../ui/appearance.md#ツリーと検索欄)に従います。

### 起動と性能

重複解析は明示的な要求で行い、起動の必須経路へ入れません。起動中のツリー・一覧表示は表示準備まで遅延します。

分析は所持集合が持つ重複MD5の索引から開始し、全行のハッシュ辞書を毎回作りません。兄弟を含めるためのディレクトリ索引は接続対象だけを扱います。削除、パス変更、追加更新、MD5変更と同じ捕捉結果の置換に索引を同期します。再検索は前回結果からの[差分解析](#再検索の差分解析)で、少数の変更のために全グループの解析、全譜面の警告の付け直し、ツリー項目の作り直しを繰り返しません。

警告対象は捕捉した重複区分の照合用ハッシュへ所属する譜面で判断します。増分スナップショットから別々に列挙した行の参照同一性を要求せず、接続先の固有な兄弟譜面には重複警告を付けません。

`SearchDuplicateChartGroups` の経路（全体／差分）・変化したハッシュ数・再判定のフォルダ数とグループ数・重複ハッシュ数・対象行数・接続フォルダ数、`ui_suppress`、`duplicate_group_autoselect` を診断に使います。確定結果と異常報告の回数は共通の変更仕様に従います。

単体ファイルを表す導入済みパッケージは、統合前に捕捉した所持項目の識別と旧完全一致パスから、衝突回避後の実際の移転先へ参照を更新します。フォルダパッケージのルートと区別し、次のExplorer要求にも確定先を渡します。参照の退役・題名公開は[ライブラリ変更](mutations.md#退役所属と確定したパッケージ参照)に従います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 連続統合後の索引の最新性 | `LibraryMutationOwner.MergeChartDirectory` | `BmsLibraryDuplicateServiceTests.MergeChartDirectory_TwoWarmOperationsKeepIndexesCurrentWithoutFullRebuild` は実検索・再利用と2回の実統合で、フォルダ集合4→3→2、グループ譜面6→5→4、重複警告4→3→2、所持・DB総数22→21→20を確認する。統合後も残る1グループ、移動先の固有ハッシュ参照、移動元消失、既存重複実体のバイト列と背景16行を保つ。旧スナップショットと温めた索引を全再構築しない保証を同じケースで維持する。 |
| 登録本体・空の登録子を含む元の準備拒否と通常作品の統合 | [`LibraryMutationOwner.MergeChartDirectory`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Merge.cs)、`BmsLibraryDuplicateMaintenanceStore` | [`BmsLibraryDuplicateServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) / [`BmsLibraryDuplicateServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `MergeChartDirectory_RejectsRegisteredSourceScope` と `MergeChartDirectory_AllowsOrdinaryChildAndSimilarSibling` が元先のファイル・DB・登録・新ライブラリ読込みを確認する。[`DuplicateMaintenanceWorkflowOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) / [`DuplicateMaintenanceWorkflowOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) の `RunFolderMergeAsync_RegisteredScopeRefusalReleasesAdmissionForNextMerge` は本番の保存窓口を通る停止後拒否と次要求の受付を確認する。改名の保護条件と保証の分担は[変更仕様](mutations.md#実装とテストの対応)を参照する。 |
| 統合へのリンクの混入の拒否（元本体・配下・先本体・対応する先の項目、リンク先の消失、確認できない場合、拒否後の受付解放と隠し・入れ子の通常統合） | [`FolderLinkInspector`](../../../BeMusicSeeker/Models/Utils/FileOperations/FolderLinkInspector.cs)、[`LibraryMutationOwner.MergeChartDirectory`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Merge.cs) | 日常検証はリンクの検出を実行しない。[`DuplicateMaintenanceWorkflowOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) の `RunFolderMergeAsync_UncheckableFolderIsRefusedWithOneNoticeAndNextMergeSucceeds` は、列挙が実OSの共有違反・削除保留のアクセス拒否になる統合元を、本番の保存窓口・実SQLiteで未適用・ファイル・DB・登録の不変・案内一件・汎用案内と変更セッション報告なし・受付解放後の案内・ロック解放後の同じ統合の成功まで確認する。[`BmsLibraryDuplicateServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `MergeChartDirectory_HiddenAndNestedDirectoriesWithoutLinksMergeWithoutNotice` は、リンクのない隠しフォルダ・二階層の入れ子・統合先の対応する通常ディレクトリの統合が従来どおり成功し案内が出ないことを確認する。リンクの検出は実リンクが必要なため専用suiteで明示実行する。`Suite.Junction` の [`FolderMergeJunctionRefusalTests`](../../../tests/BeMusicSeeker.Compatibility.Tests/Maintenance/FolderMergeJunctionRefusalTests.cs) は、深い隠しフォルダ内・リンク先が消えた・統合元本体・統合先の二階層目の対応ディレクトリのジャンクションの拒否（案内一件の完全一致、ファイル・リンク・リンク先・DB・登録の不変、別の組の成功）、既存の拒否（登録ルート・カタログ未収束）が先に働くこと、`RunFolderMergeAsync` での案内一件・報告なし・受付と活動の解放・次の統合の成功を確認する。`Suite.DirectorySymbolicLink` の [`FolderMergeSymbolicLinkRefusalTests`](../../../tests/BeMusicSeeker.Compatibility.Tests/Maintenance/FolderMergeSymbolicLinkRefusalTests.cs) は、深い位置のファイル・ディレクトリのシンボリックリンクと、統合元のファイルと同じ相対パスにある統合先のファイルのシンボリックリンクを同じ観点で確認する。統合元本体が壊れたリンクの場合はテストで固定しない。 |
| 統合の確認文への配下のフォルダの件数と移動する旨の追記 | [`DuplicateMaintenanceWorkflowOwner.ConfirmFolderMergeAsync`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs)、[`NestedFolderCounter`](../../../BeMusicSeeker/ViewModels/Maintenance/NestedFolderCounter.cs) | [`DuplicateMaintenanceWorkflowOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) の `RunFolderMergeAsync_ConfirmationMentionsNestedFolders` は配下なしで従来の文、3件で件数付きの追記、数えられない場合の件数なしの追記と統合の続行を、`RunFolderMergeAsync_ConfirmationDisabled_SkipsCount` は確認を表示しない設定で数えないことを、`NestedFolderCounter_CountsAllLevelsIncludingHiddenFoldersButNotFiles` は実ファイルシステムで全階層と隠しフォルダを数えファイルを数えないことと存在しない起点の失敗を確認する。再解析ポイントをたどらない規則は自動テストでは確認していない。 |
| MD5識別、接続グループ、兄弟譜面、警告 | [`BmsLibraryDuplicateService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/BmsLibraryDuplicateService.cs)、[`OwnedDuplicateChartRowSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/OwnedChartCollectionState.cs) | [`BmsLibraryDuplicateServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) / [`BmsLibraryDuplicateServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) |
| 同版の共通現在値捕捉、確定差分、固有警告・未表示行・検索・順序・健康度・再選択 | [`DuplicateGroup.ProjectCurrentChart`](../../../BeMusicSeeker/Models/Library/DuplicateGroup.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、[`ChartSubsetReferenceRows`](../../../BeMusicSeeker/ViewModels/ChartList/ChartSubsetReferenceRows.cs)、[`ChartListOrder.IsSortedFor`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListOrder.cs) | [`RegularChartNormalLibraryRefreshTests.Duplicates`](../../../tests/BeMusicSeeker.Core.Tests/ChartList/RegularChartNormalLibraryRefreshTests.Duplicates.cs) の `DuplicateCurrentValues_RealEncodingAndRescanPatchOnlyAffectedRows` は、実重複検索から一件の文字コード変更・BMS/BMSONの再調査・通知配送・再選択を通し、配送前の再選択を含め検索加入/離脱・Title/健康度順・旧スナップショット不変と対象だけの仕事量を確認する。`ChartListVirtualViewTests.CapturedOrder_CurrentValuesRespectDirectionTitleAndStableTies` は昇降順の副キー・元順序・空入力と整列済み値変更を確認する。解析条件と温めた索引は `BmsLibraryDuplicateServiceTests` が分担する。 |
| 解析の共通受付、Busyと読取り、受理済み変更の内部継続 | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、[`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | `RegularChartNavigationTests.DuplicateAdmission_BusyPreservesViewAndCachedDisplayDoesNotReenter` はBusy時のサマリー・検索保全、予約なし、準備済み表示を確認する。`DuplicateAdmission_RowsBuildDoesNotExtendDataAdmission` は行構築と受付解放の独立性を確認する。`BmsLibraryDuplicateServiceTests.DuplicateAdmission_RealAnalysisRejectsLibraryMoveUntilRequiredNotificationsComplete` は実解析中の移動拒否を確認する。`DuplicateDependencyInvalidation_PreparesOutsideUiAndKeepsLaterSummary` は実移動のファイル・SQLite・通知と、表示中の内部継続、非表示時の失効と明示再訪を分担する。実統合と実ツリーのBusyは `MainWindowPackageMaintenanceWpfTests` が確認する。`DuplicateSelections_ActualMergeSelectsCapturedNextGroupThroughCompiledTree` と `DuplicateSelections_ActualHashCleanupSelectsCapturedNextGroupAfterLaterView` は、実統合の捕捉次群選択と、実ハッシュ整理中の別画面から捕捉次群への成功後選択を分担し、実Tree・Binding・ファイル・DB整理後の受付解放を確認する。`DuplicateMaintenanceWorkflowOwnerTests.ExplicitMaintenance_RequiredPreparationFailurePreservesFactsAndReleasesAdmission` は準備失敗と元失敗の合成・先行確定・親受付の回収を分担する。[`AcceptedChangeDuplicatePreparationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/AcceptedChangeDuplicatePreparationTests.cs) の `SuppressionRelease_DuplicateSelectionJoinsRegisteredPreparation` は統合・一括整理（統合候補一覧の文脈で重複選択する）・譜面移動・保留・自動改名・フォルダ名変更の抑制解除通知の中で重複選択が Busy にならず登録済みの準備へ合流し、準備の終端までLを保持することを、`PreparationFailure_ComposesIntoResultAndReleasesAdmission` は統合以外の準備失敗の結果への合成と後片付けを、`CompleteAcceptedDuplicateDataAsync_SynchronousFailureReturnsFaultedTask` は同期例外の Task 化を確認する。`RegularChartFolderRenameTests.FolderRename_RequiredDeliveryFailureStillAwaitsDuplicatePreparation`（Core）は必須配送の失敗でも準備を待つことを確認する。 |
| グループ変更通知の配送時機 | [`BMSLibrary.SearchDuplicateChartGroups`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、`BMSLibrary.DuplicateChartGroups` | [`BmsLibraryDuplicateServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `SearchDuplicateChartGroups_DeliversGroupsChangeOnCallerAfterAllLocksBeforeReturn` は、重複あり・0群・通知先の失敗で、群の通知が検索の戻り前に呼出しスレッドへ、全ロックの解放後に（警告の一覧通知がある場合はそれより先に）届き、通知時点で戻り後と同じ群を読めること、キャッシュ命中で通知しないことを確認する。`DuplicateChartGroupsSetter_DeliversChangeOnCallerBeforeReturnAndSkipsSameValue` はsetterの同期通知と同値の非通知を確認する。通知後に返る保守結果の空表示は `MainWindowPackageMaintenanceWpfTests.DuplicateSelections_DisappearedTargetClearsTreeAndRowsWithoutHeaderFallback`、通知後の完成通知による表示は `RegularChartNavigationTests.ColdDuplicate_OwnNotificationsKeepAcceptedRootAndReadyRows` が分担する。 |
| 初回解析中の後発画面移動、完成通知、解析前の文脈更新 | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、`MainWindowViewModel`、`MainWindow.dupulicateFileCheckFolderSelect` | [`RegularChartNavigationTests.ColdDuplicate_LaterNavigationKeepsSemanticTargetThroughCompletion`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.cs) と `DuplicateNavigation_QueuedPresentationCannotClearLaterSummary` が後発画面と遅いUI配送を確認する。`ColdDuplicate_OwnNotificationsKeepAcceptedRootAndReadyRows` は初期・保守・完成0件と、グループ配送後の解析中に選ぶフォルダの行集合、データ確定と必要警告を確認する。`MainWindowPackageMaintenanceWpfTests.ColdDuplicateTree_LaterPlaylistRootKeepsSelectionAndVisibleSummary` は実ツリー選択、サマリーのBinding、解析前の検索文脈と準備済みルート展開を確認する。 |
| データ失敗・表示失敗・取消・通知と停止回収 | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、`MainWindowViewModel` | [`RegularChartCommitAndLifecycleTests.DuplicatePresentation_FailureNotifiesOnceAndStopWaitsForNotification`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartCommitAndLifecycleTests.cs) は同期開始例外・非同期失敗・取消と通知待機中の停止を確認する。`DuplicateData_DeliveryFailureRemainsOnNavigationOperation` はデータ配送失敗を操作結果へ返す。[`RegularChartNavigationTests.ColdDuplicate_OwnNotificationsKeepAcceptedRootAndReadyRows`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.cs) の表示失敗条件は実一覧の失敗と準備取消、実VMからの通知一度を確認する。`StopAsync_WaitsForAcceptedDuplicateProducerBeforeItsOwnerNotification` は開始済み解析の回収を分担する。 |
| ライブラリ交換時の選択維持と旧処理回収 | [`RegularChartListOwner.AttachNormalLibraryRefreshSource`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、`MainWindowViewModel` | [`RegularChartNavigationTests.SourceAuthorityReplacement_KeepsCurrentSelectionAndRejectsOldSourceWork`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.cs) が、旧一覧構築中の交換と旧候補の不採用、独立解析中の新規接続のBusy拒否と有効状態の保持を分担する。`MainWindowPackageMaintenanceWpfTests.ResourceHealthMaintenanceNotificationRefreshesRealizedNormalRowBeforeDisplay` は起動時の通常ルート、接続後の実一覧とBinding、後続保守の実反映を確認する。 |
| 後発選択の受理後・UI適用前に届く旧通知 | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、`RegularChartListRefreshTypes`、`MainWindowViewModel`、`PlaylistWorkspaceViewModel.TreeSelection` | [`RegularChartNavigationTests.ColdDuplicate_AutoRefreshCannotAdoptOldTargetWhileAcceptedInstallUiIsPending`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.cs) は特定の導入項目を受理してUI配送を保持し、旧解析と通知を完了しても旧一覧を採用せず、配送後は特定項目だけを表示することを確認する。 |
| 重複結果の準備中の検索・絞込み・並べ替え | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、`MainWindowViewModel` | [`RegularChartNavigationTests.DuplicateInvalidation_NormalFiltersAwaitReadyInputAndAdoptLatestFilter`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.cs) は実設定変更から同じデータ準備へ合流し、仮の空一覧や古い条件を採用せず、完成時の条件と順序、完成後の正当な空結果を確認する。 |
| 同名・空名、明示ルート、フォルダ配下、未解決、行選択の識別 | [`DuplicateViewContext`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateViewContext.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`RegularChartViewBuildAndOrderingTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartList/RegularChartViewBuildAndOrderingTests.cs)、[`RegularChartViewBuildAndOrderingTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartViewBuildAndOrderingTests.cs) の `RegularEntry_SelectsDuplicateSubsetFromLibraryOwner` と `RegularEntry_DuplicateFolderContextSelectsOnlyMatchingCharts` が実管理主体からのパス集合、選択範囲、子孫と名前の先頭が共通な兄弟、捕捉後の元の一覧の変更を確認する。 |
| 操作前の次対象・三以上の存続宛先、実左Treeまでの接続と消失時解除、選択表示の維持 | [`MaintenanceTreeViewModel`](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceTreeViewModel.cs)、`DuplicateMaintenanceWorkflowOwner`、`MainWindow.ApplyDuplicateMaintenanceSelectionAsync`、`MainWindow.treeSelectionCleanUpVirtualizedItem` | [`MaintenanceTreeViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/MaintenanceTreeViewModelTests.cs) と [`DuplicateMaintenanceWorkflowOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) / [`DuplicateMaintenanceWorkflowOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) が捕捉値と現在結果の所属を確認する。[`MainWindowPackageMaintenanceWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `DuplicateSelections_ActualMergeSelectsCapturedNextGroupThroughCompiledTree` は実検索・標準構成・本番の保存窓口・SQLite/ファイルから左ツリーの自動選択までを通し、`DuplicateSelections_DisappearedTargetClearsTreeAndRowsWithoutHeaderFallback` は、群の差し替えと同期の変更通知の後に結果を返す形で、対象消失時の左無選択・空一覧・旧行選択解除を分担する。実統合と実ハッシュ整理の二件は、自動選択した群の入れ物だけが選択表示を持つことも確認し、実統合は無関係な展開済み群と次群の入れ物・インスタンスが同じまま残ることも確認する。`DuplicateSelections_AutoSelectionRealizesOffscreenCapturedGroupAndKeepsDisplayAcrossScroll` の入力は対象群を同じフォルダの新しいインスタンスへ差し替えた結果で、新しいインスタンスの選択・展開を確認する。`DuplicateSelections_ManualSelectionDisplayStaysOnSelectedItemAcrossVirtualizedScroll` は、ツリー外にフォーカスがある状態でグループと多数フォルダの群のフォルダを選び、仮想化で近傍の入れ物が解放される位置までスクロールして戻しても、選択表示が選択項目だけに残り、左の選択・一覧要求・行集合が変わらないことを確認する。`DuplicateSelections_AutoSelectionRealizesOffscreenCapturedGroupAndKeepsDisplayAcrossScroll` は、完了時に画面外で未生成の捕捉群を生成して選択し、その後のスクロールでも選択表示が移らないことを確認する。 |
| 再検索の差分解析（変化ハッシュ、影響範囲、グループ・警告の全体解析との一致、インスタンス維持、仕事量、全体解析へ戻る条件） | [`BmsLibraryDuplicateService.AnalyzeIncremental`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/BmsLibraryDuplicateService.cs)、[`OwnedDuplicateChartRowSnapshot.FindChangedHashes`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/OwnedChartCollectionState.cs)、`OwnedChartCollectionState.CreateDuplicateRowsInDirectory`、[`BMSLibrary.SearchDuplicateChartGroups`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`BmsLibraryDuplicateServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `IncrementalAnalysis_MatchesFullAnalysisAndKeepsUnaffectedGroups` は分裂・移動／追加による結合・解消・存続・新規発生・変化なし・MD5変更・BMSON加入・兄弟だけの追加・パスの大文字小文字・維持グループの題名だけの変更・古い兄弟警告で、全体解析との一致、影響外の同一インスタンス、警告集合の収束を確認する。`IncrementalAnalysis_WorkIsBoundedByRecheckRange` は大量の無関係グループで具体化・取得が再判定の範囲に限られることを、`SearchDuplicateChartGroups_ReSearchAfterLibraryChangesMatchesFullAnalysisAndRewritesOnlyChangedWarnings` は実統合・削除・BMSON置換の後の差分再検索、追跡した警告の解除、BMS全置換後の全体解析への復帰を確認する。 |
| 重複ツリー項目の維持と失効中の保持 | [`MaintenanceTreeViewModel`](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceTreeViewModel.cs) の `DuplicateTreeItems`、`MainWindow.xaml` の `treeViewItemSearchDuplicated` | [`MaintenanceTreeViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/MaintenanceTreeViewModelTests.cs) の `DuplicateTreeItems_SyncKeepsSameInstancesAndFollowsResultOrder` は同じインスタンスの維持と結果順・Reset なしを、`DuplicateTreeItems_InvalidationKeepsItemsOnlyWhilePreparationRuns` は準備中の維持・結果ありの完了・結果なしの終了・準備なしの失効・解除を確認する。[`RegularChartNavigationTests.DuplicateTreeItems_AcceptedMovePreparationKeepsItemsOnUiAndClearsWhenNoResult`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.cs) は実移動・実準備でUIスレッドだけの変更と結果なしでの空表示を確認する。 |
| 統合先候補の同一・親子除外、兄弟保持、大小文字の重複排除とCtrl+Gの元件数分岐 | [`DuplicateMaintenanceWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs) の `CaptureDuplicateFolderMergeDestinations`、`CaptureDuplicateFolderKeyboardAction` | [`DuplicateMaintenanceWorkflowOwnerTests.DuplicateFolderInteractionQueries_PreserveDestinationAndKeyboardPolicy`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) は管理主体の実装で候補の件数付き集合一致、末尾区切り文字・大小文字・親子・接頭辞だけが同じ兄弟、二件／三件以上の候補ゼロ、有効一件時の元件数分岐と単一フォルダ整理を確認する。存在確認は呼出し失敗の代替にし、物理資源を使わない。実メニューとショートカットからの対象受渡し・単一フォルダ整理への接続は [`MainWindowPackageMaintenanceWpfTests.CompiledDuplicateContextAndShortcutRoutesUseOneTypedTerminal`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) が分担する。 |
| 統合、後続保守、受付解放、失敗と通知 | [`DuplicateMaintenanceWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs) | [`DuplicateMaintenanceWorkflowOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) / [`DuplicateMaintenanceWorkflowOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs)、[`BmsLibraryDuplicateServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) / [`BmsLibraryDuplicateServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs)。同一・子・祖先の宛先を物理変更前に拒否して元入力を保つ保証は [`BmsLibraryPackageInstallServiceTests.MovePackageFilesWithReceipt_RejectsOverlappingDirectoryDestinationBeforeMutation`](../../../tests/BeMusicSeeker.Core.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) が分担する。 |
| 重複グループの一括整理（一つの受付と変更セッション、単体の統合との一致、重複整理の選定、最後のコピーの保護、書込み前の拒否、移動元ごとの拒否、予期しない物理失敗、取消と進捗、確定失敗、反映回数、操作全体の拒否） | [`LibraryMutationOwner.OrganizeDuplicateGroups`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.BatchOrganize.cs)、[`DuplicateChartCleanupSelector`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/DuplicateChartCleanupSelector.cs)、[`SourceExcludingInstalledChartLookup`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/SourceExcludingInstalledChartLookup.cs)、[`DuplicateBatchOrganize`（要求・結果）](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/DuplicateBatchOrganize.cs)、`FolderLinkInspector.InspectFolder`、`BmsLibraryDuplicateMaintenanceStore.OrganizeDuplicateGroups` | [`DuplicateBatchOrganizeTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateBatchOrganizeTests.cs) が実ファイル・SQLite・実ライブラリで確認する。`OrganizeDuplicateGroups_MatchesSequentialSingleMerges` は、スマート上書きと保護対象名の別名保持の全組合せで、採番・資源の衝突・二階層の配下・保護対象名を含む入力の結果（相対パス・バイト列・DB 行）が単体の統合を順に適用した結果と一致し、反映が一回であることを、`OrganizeDuplicateGroups_KeepDirectDuplicates_KeepsOldestThenShortestName`・`OrganizeDuplicateGroups_SourceDuplicates_AreCleanedOnlyForHashesThatMove`・`OrganizeDuplicateGroups_SingleFolderGroup_TrashesOnlyDirectDuplicates` は選定規則・BMSON・移る主ハッシュに限る移動元の整理・ごみ箱の記録・確認と案内が 0 件であることを、`OrganizeDuplicateGroups_NeverLosesTheLastCopy` は同じグループ・別グループの成功／スキップ／取消／物理失敗・型の衝突での最後のコピーの保護を、`OrganizeDuplicateGroups_RefusesBeforeAnyChangeAndContinues` は書込み前の拒否の各理由と変更なし・案内なし・次のグループの完了を、`OrganizeDuplicateGroups_SourceRefusals_AreRetainedWithReasonsAndTheGroupContinues` と `OrganizeDuplicateGroups_MissingDestination_FailsTheGroupAndKeepsEverySource` は移動元ごとの拒否と宛先の欠落を、`OrganizeDuplicateGroups_UnexpectedPhysicalFailure_CommitsPrecedingWorkAndLeavesTheRestUnprocessed` と `OrganizeDuplicateGroups_UnrestoredExistingDestination_IsReportedInTheSessionReceipt` は予期しない物理失敗と既存宛先の復元不能を、`OrganizeDuplicateGroups_CanceledBeforeStart_ProcessesNothing`・`OrganizeDuplicateGroups_CancelOnSecondGroupStart_FinishesTheStartedGroupAndMaintainsIt`・`OrganizeDuplicateGroups_ProgressReceiverFailure_DoesNotChangeTheResult` は取消・進捗の順序・受け手でのロック非保持・受け手の例外を、`OrganizeDuplicateGroups_CommitFailure_FailsPhysicallySuccessfulGroupsAndKeepsEveryFact` は確定失敗の状態と実数の保持を、`OrganizeDuplicateGroups_ApplyAndPublicationCountsDoNotGrowWithGroupCount` は反映・公開の回数がグループ数に比例せず全 KEEP が保守済みであることを、`OrganizeDuplicateGroups_BusyAdmission_RejectsTheWholeOperationWithOneNotice`・`OrganizeDuplicateGroups_CatalogNotConverged_RejectsTheWholeOperationWithOneNotice`・`OrganizeDuplicateGroups_ManagedOutputBusy_RejectsTheWholeOperationWithOneNotice` は操作全体の拒否を確認する。列挙が共有違反になる移動元のスキップは [`BmsLibraryDuplicateServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `OrganizeDuplicateGroups_UncheckableSourceTree_SkipsTheGroupWithoutNoticesAndContinues`、実ジャンクションは `Suite.Junction` の [`FolderMergeJunctionRefusalTests`](../../../tests/BeMusicSeeker.Compatibility.Tests/Maintenance/FolderMergeJunctionRefusalTests.cs) の `OrganizeDuplicateGroups_JunctionIsSkippedOrIgnoredPerGroupKindAndOtherGroupSucceeds`（移動元の深い位置のジャンクション、一フォルダのグループのフォルダ本体のジャンクションはスキップ、一フォルダのグループの配下のジャンクションは処理してリンクとリンク先を変えない）が確認する。選定規則の古さ優先の行は [`DuplicateMaintenanceWorkflowOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) の `RunHashCleanupAsync_ChoosesShortestNameAndReturnsRemovalCount` が分担する。画面側は[統合候補一覧からの一括整理の対応](#実装とテストの対応)の行が分担する。反映段階への移行の通知は、`AssertApplyingNotifiedOnceLast` が、通常・取消前・二つ目のグループでの取消・受け手の例外の各ケースで、一回だけ最後に通知されることと、操作全体の拒否では通知しないことを確認する。 |
| 実行中の進捗行の開始・終了と通知しない経路 | [`DuplicateMaintenanceWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs) の `ProgressChanged`、[`OperationProgressHubViewModel.AttachDuplicateMaintenanceProgressSource`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs)、`ApplicationComposition` | [`DuplicateMaintenanceWorkflowOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) の `ProgressFacts_StartAfterApprovalAndEndBeforeReportOnEveryTerminal` は、統合と重複削除の成功・対象別受付の使用中・失敗と確認を表示しない設定で、全確認の回答後かつ背景処理の開始前の開始、終端後かつ報告前の終了、対象フォルダを確認する。`ProgressFacts_NotPublishedWhenOperationEndsBeforeBackgroundMutation` は受付の拒否・確認の取消と表示失敗・事前準備の失敗・対象なしで通知しないことを、`ProgressSubscriberFailure_KeepsResultReleaseAndReport` は受け手の例外で結果・解放・報告が変わらず終了通知が欠けないことを確認する。`RunFolderMergeAsync_UnrestoredDestinationExitsAfterOwnedTerminal` は復元不能で終了への接続より前に閉じることを確認する。標準構成の接続は [`MainWindowPackageMaintenanceWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `DuplicateSelections_ActualMergeSelectsCapturedNextGroupThroughCompiledTree` と `DuplicateSelections_ActualHashCleanupSelectsCapturedNextGroupAfterLaterView` が、実行中の一行と終端後の消去で確認する。行の内容と言語追従は[ステータスバーの対応表](../runtime/statusbar-progress.md#実装とテストの対応)を参照する。 |
| 削除の確認件数とカタログ反映 | [`LibraryMutationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.cs) | [`OwnedChartCollectionLibraryMutationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Catalog/OwnedChartCollectionLibraryMutationTests.cs)、[`BmsLibraryFolderRenameRefreshTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryFolderRenameRefreshTests.cs) |
| 終端の分類と多言語表示 | [`FileDbMutationReport`](../../../BeMusicSeeker/ViewModels/ChartOperations/FileDbMutationReport.cs) | [`FileDbMutationReportTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/FileDbMutationReportTests.cs)、[`LocalizationResourceParityTests`](../../../tests/BeMusicSeeker.Core.Tests/Localization/LocalizationResourceParityTests.cs) |
| 統合候補一覧のノード（有無、位置、同一性、Resetなし） | [`MaintenanceTreeViewModel`](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceTreeViewModel.cs)、[`DuplicateTreeChildCollection`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateTreeChildCollection.cs)、`MainWindow.xaml` の `duplicateTreeItemContainerStyleSelector` | [`MaintenanceTreeViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/MaintenanceTreeViewModelTests.cs) の `CandidateListNode_ExistsOnlyWhileResultsExist` と `CandidateListNode_KeepsInstanceAndRelaysGroupChangesWithoutReset` |
| 統合候補一覧の構築・行の順・初期KEEP・並べ替え・検索・件数・操作・優先統合先・状態の寿命・後続向けの窓口・解決回数 | [`DuplicateMergeCandidateListOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMergeCandidateListOwner.cs)、[`DuplicateMergeCandidateModels`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMergeCandidateModels.cs) | [`DuplicateMergeCandidateListOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMergeCandidateListOwnerTests.cs) が、メモリ上のグループと解決・登録範囲の代替で各項目を確認する。`Capture_FollowsSortedDisplayOrderAndCarriesTheVerificationOfTheMoment` は並べ替えを当てた表示順での捕捉と、捕捉時点の確認結果の保持を、`RemoveUnresolvedGroups_DropsResolvedGroupsWhileHiddenAndKeepsTheRest` は一覧が隠れている間も解消したグループの状態と行を取り除くこと・結果が空なら行が0件になること・残るグループの状態の同一性を確認する。`Exclude_RemovesGroupsHavingAnyMatchingRowAndKeepsHiddenState` は除外語を検索欄と同じ構文で行単位に評価すること（行単位のAND・否定・項目指定・正規表現・空と空白・検索との組合せ）、件数・序数・表示中をすべて選択／解除・捕捉が非表示のグループへ及ばないこと、除外を解除した後に同じインスタンスで状態が残ることをデータ駆動で、`StateLifetime_` と `Operations_DoNotResolveOrCaptureAgain` は除外語の寿命と解決・捕捉を呼ばないことを確認する |
| TITLE・ARTISTの一括解決、登録範囲の判定、構築前にプロフィールを作らない | [`BMSLibrary.DuplicateMergeCandidates`](../../../BeMusicSeeker/Models/Library/BMSLibrary.DuplicateMergeCandidates.cs)、`LibraryMutationOwner.CaptureRegisteredBmsRootPredicate` | [`DuplicateMergeCandidateListLibraryTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMergeCandidateListLibraryTests.cs) が実検索・実登録で確認する |
| ノードの選択、専用の表への切替、状態の保持、セル・Space・メニュー・ツールバー・ダブルクリック遷移・優先統合先のウィンドウ・表示言語 | `MainWindow.DuplicateMergeCandidateList.cs`、`MainWindow.xaml`、[`DuplicatePreferredDestinationDialog`](../../../BeMusicSeeker/Views/Dialogs/DuplicatePreferredDestinationDialog.xaml.cs)、`CustomTableColumnFactory.CreateDuplicateCandidateColumns` | [`MainWindowDuplicateCandidateListWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowDuplicateCandidateListWpfTests.cs) の `CandidateList_` で始まるケース。`CandidateList_NodeSwitchesToDedicatedTableAndKeepsStateAcrossScreens` は、専用の検索欄の表示と入力からの束縛による絞込み・再訪での復元、表示中にメインの件数表示・保留譜面の操作バー・プレイログの集計バーを出さないこと、表の操作で変えた列幅が行き来で保たれ保存される側の列設定を変えないことを確認する。`CandidateList_LeavingForPendingOrPlayHistoryShowsThatScreensOwnBars` は、保留とプレイログの画面へ移るとその画面のバーと件数表示が元の条件で出て、一覧へ戻ると隠れることを確認する |
| 安全性確認の検査（段階の集約、親子関係・型の衝突・別グループ・登録ルート・解析不能、親ディレクトリ参照・配下の別の譜面フォルダ・同名音源・別内容・同一内容の連結・保護対象名の付記、読まない経路、簡易確認と全量・部分の境界、KEEPの導き直し） | [`DuplicateMergeSafetyInspector`](../../../BeMusicSeeker/Models/Library/DuplicateMerge/DuplicateMergeSafetyInspector.cs)、[`DuplicateMergeFolderScan`](../../../BeMusicSeeker/Models/Library/DuplicateMerge/DuplicateMergeFolderScan.cs)、[`DuplicateMergeContentComparer`](../../../BeMusicSeeker/Models/Library/DuplicateMerge/DuplicateMergeContentComparer.cs)、[`DuplicateMergeFolderIndex`](../../../BeMusicSeeker/Models/Library/DuplicateMerge/DuplicateMergeFolderIndex.cs)、[`DuplicateMergeGroupInspection`](../../../BeMusicSeeker/Models/Library/DuplicateMerge/DuplicateMergeGroupInspection.cs)、[`DuplicateMergeSafetyPolicy`](../../../BeMusicSeeker/Models/Library/DuplicateMerge/DuplicateMergeSafetyPolicy.cs) | [`DuplicateMergeSafetyInspectorTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMergeSafetyInspectorTests.cs) が、テスト用の作業領域の実ファイルと実ディレクトリを一グループの検査の入口へ渡し、所持譜面の情報だけをメモリ上で代替して、段階・理由・該当パス・比較方法を確認する。サイズは本番の定数から計算し、読まない経路は排他ロックしたファイルが解析不能になるかで区別する |
| 所持譜面の情報の取得口（対象フォルダの範囲、実際のBMS解析による親ディレクトリ参照、所持していない譜面・範囲外の除外、解析不能） | [`BMSLibrary.DuplicateMergeCandidates`](../../../BeMusicSeeker/Models/Library/BMSLibrary.DuplicateMergeCandidates.cs)（`CaptureOwnedCharts`） | [`DuplicateMergeCandidateListLibraryTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMergeCandidateListLibraryTests.cs) の `CaptureOwnedCharts_FeedsInspectorWithRealParsingAndOnlyTheFolderScope` が、実ライブラリと実際のBMS解析で確認する |
| 確認結果の一括書込み（SELECTの置換え、行集合の差し替え一回、検索・除外・並べ替えの当て直し）、KEEP変更によるSTATUSの導き直し、STATUS（全理由の連結）・ツールチップの書式、STATUSの語の検索・除外、簡易確認の既定と寿命 | `DuplicateMergeCandidateListOwner`、[`DuplicateMergeVerificationPresentation`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMergeVerificationPresentation.cs) | `DuplicateMergeCandidateListOwnerTests` の `WriteVerifications_`、`SetKeep_Rederives`、`StatusText_`、`StatusSearchAndExclude_`、`StatusTooltip_`、`SafeTooltip_`、`QuickCompare_`、`Capture_` で始まるケース。`StatusText_ListsAllReasonsInResultOrder` は三理由の連結・統合不可と要注意の混在・理由が一つの組を、`StatusSearchAndExclude_MatchEveryReasonName` は二番目の理由名での検索の残りと除外の消えを、`WriteVerifications_WithStatusExcludeReplacesRowSetOnceAndHidesMatchingGroups` は除外にSTATUSの語があるときの一括書込みで行集合の差し替えが一回で対象が消えることを、`QuickCompare_DefaultsOnAndKeepsDisabledValueAcrossLibraryExchange` は既定の有効と無効の保持を確認する |
| 確認の手順（受付の保持と解放の順序、Busy、受付の閉鎖、対象0件、進捗の報告、取消、確認中に消えたグループ、予期しない例外、UIスレッド外の走査、全グループのフォルダの受渡し、登録ルートの保持、簡易確認の既定の有効と案内（入れる操作のたびに一件、外す操作と起動時は案内なし）と値の受渡し） | [`DuplicateMergeVerificationWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMergeVerificationWorkflowOwner.cs) | [`DuplicateMergeVerificationWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMergeVerificationWorkflowOwnerTests.cs) が、本番の受付と実際の管理主体で確認する。時機の制御が要るケースは、グループ単位で待合せできる検査器の代替を使い、全グループのフォルダの受渡しは実検査器と実ファイルで確認する |
| 確認のボタン・メニュー・チェック・進捗の要求・ツールチップ・標準構成の受付への接続 | `MainWindow.DuplicateMergeCandidateList.cs`、`MainWindow.xaml`、`CustomTableColumnFactory.CreateDuplicateCandidateColumns`、`ApplicationComposition` | `MainWindowDuplicateCandidateListWpfTests` の `CandidateList_VerifyButtonMenuAndQuickCompareCheckActThroughRealFiles` が、実ファイルの衝突から実ボタン・メニュー・チェックの操作で、STATUS・ツールチップ・SELECT・件数表示・進捗の所有者・案内一件・Busyを確認する。チェックは開始時にオンで案内がなく、外す操作で案内せず、入れる操作で案内を一件出すことも同じケースが確認する。`CandidateList_PresentationFollowsDisplayLanguageRoundTrip` が表示言語の往復（除外欄のアクセシブル名とプレースホルダーを含む）と、ツールバーの五つのボタンとチェックボックスが共通スタイルの見た目を保つこと（スタイルの継承元・テンプレートの一致・配色とテンプレートの上書きがないこと）を確認する。ツールバーの高さ・余白・除外欄の位置は自動テストで固定しない |
| 統合候補一覧からの一括整理の手順（対象とKEEP・移動元・表示順、Busy・受付の閉鎖、確認とその文面（通常の本文と警告用の本文の分け方を含む）、再生停止・抑制・モデルの順序、進捗と反映段階、取消、未受理・失敗・復元不能の終端、結果の案内） | [`DuplicateMaintenanceWorkflowOwner.OrganizeSelectedGroupsAsync`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.BatchOrganize.cs)、[`DuplicateBatchOrganizePresentation`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateBatchOrganizePresentation.cs)、`DuplicateMergeCandidateListOwner.CaptureSelectedGroups` | [`DuplicateMaintenanceWorkflowOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) の `OrganizeSelectedGroups_` で始まるケースが、実際の一覧の管理主体・本番の受付・記録用の保存窓口・実際の背景実行の進捗で確認する。0件（外部が受付を保持中を含む）・表示中でSELECTがオンのグループだけ・未確認と要注意の取込みと状態の不変・表示順と大文字小文字を無視した移動元・Busyと受付の閉鎖・確認の承認／取消／閉じる／表示失敗と設定オフでも出ること・確認中の保持・確認文の組合せ（通常の本文は［グループ数、統合、ごみ箱］、警告用の本文は［未確認・要注意、衝突］で、警告の行が通常の本文へ残らないこと）・停止と抑制とモデルの順序とUIの文脈の外・停止の失敗・進捗の文言と反映段階での取消不可・取消の合図の到達と進捗が取消を返す場合・取消中の例外・未受理・確定／反映／後片付けの失敗の報告と結果の順序・モデルの例外・復元不能・結果の見出しと詳細（20件と21件を含む）・ごみ箱の合計（確定失敗の分を含む）・全分類値の文言を確認する。[`DuplicateMaintenanceWorkflowOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) の `OrganizeSelectedGroups_` で始まるケースが、実受付・実SQLiteで、準備の完了までの受付の保持と結果が解放後に一回であること・確認中に書込みロックを持たずモデルを呼ばないこと・モデルがUIの文脈の外で呼ばれ塞がれたUIを待たずに進捗の受け手が戻り表示が最後の値で終わることを確認し、`ExplicitMaintenance_RequiredPreparationFailurePreservesFactsAndReleasesAdmission` の一括の行が準備失敗と元失敗の合成を確認する。確認ダイアログが警告用の本文を警告色で出し、空・nullでは出さないことは、[`DialogPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs) の `ThemedMessageBox_ShowsNonBlankWarningTextInWarningBrushAndOmitsEmptyOrNull` が確認する |
| 進捗画面の取消の扱いを操作へ任せる設定（取消後の表示更新、取消ボタンの無効化、例外を保つこと）と既定の動作の維持 | [`ProgressDialog`](../../../BeMusicSeeker/Parago/Windows/ProgressDialog.cs)、`ProgressDialogContext`、`ProgressDialogSettings.OperationOwnsCancellation`、`UiProgressContext` | [`UiDialogCoordinatorWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/UiDialogCoordinatorWpfTests.cs) の `RunWithProgressAsyncOperationOwnsCancellationKeepsReportsDisablesCancelAndPreservesFailure` が実の進捗画面で、取消の要求後の表示更新・取消ボタンの無効化・操作の例外が失敗として返ることを、`RunWithProgressAsyncDefaultCancellationStillDiscardsReportsAndSwallowsFailure` が既定では従来どおり更新を捨てて取消として返すことを確認する |
| 統合候補一覧からの一括整理の実画面での接続（ボタン、進捗の所有者、受付の保持、下部の進捗行なし、再構築での状態の保持、解消したグループの行の消失、結果0件、Busy案内なし） | `MainWindow.DuplicateMergeCandidateList.cs`、`MainWindow.xaml`、`ApplicationComposition`、`RegularChartListOwner`（解消したグループの除去） | `MainWindowDuplicateCandidateListWpfTests` の `CandidateList_OrganizeButtonMergesTheSelectedGroupsAndRebuildsTheListKeepingTheRest` が、実画面・実ワークフロー・実際の保存窓口・実ファイル・SQLiteで、実行の開始（変更の活動の開始。再生停止とモデルの呼出しの前）で受付を保持し下部の進捗行がなく進捗画面の所有者がメインウィンドウであること、終了後に解消したグループが行から消え影響しないグループが同じインスタンスでSELECT・KEEP・確認結果を保つこと、ノードの選択と表示中の状態が保たれ譜面一覧を出さないこと、ファイル・DB・結果一回・Busy案内なし・受付の解放を確認する。受付の保持中の重複選択がBusyにならず準備へ合流する保証は、このケースでは直接確かめず、`AcceptedChangeDuplicatePreparationTests` の一括整理の行が担う。`CandidateList_OrganizingEveryGroupLeavesNoStaleRows` は結果が0件のとき一覧の古い行を残さず、通知が結果の案内だけであることを確認する |
| 実ジャンクション・シンボリックリンクの検出とリンク先へ入らないこと | `DuplicateMergeFolderScan` | [`DuplicateMergeSafetyJunctionTests`](../../../tests/BeMusicSeeker.Compatibility.Tests/Maintenance/DuplicateMergeSafetyJunctionTests.cs)（`Suite.Junction`）と [`FolderMergeSymbolicLinkRefusalTests`](../../../tests/BeMusicSeeker.Compatibility.Tests/Maintenance/FolderMergeSymbolicLinkRefusalTests.cs)（`Suite.DirectorySymbolicLink`）が、実リンクで確認する |
| 統合候補一覧の画面受付（未解析のBusy、実行中の準備への合流、準備済みの受付なし、後発画面の優先、再構築での状態の保持、ライブラリ交換での旧構築の不採用） | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)（`DuplicateCandidateList`、`PrepareRegularView`）、`DuplicateMergeCandidateListOwner.Adopt` | [`RegularChartNavigationTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.CandidateList.cs) の `CandidateList_ColdBusyKeepsViewAndNotifiesOnce`、`CandidateList_JoinsRunningPreparationOrUsesPreparedResultWithoutAdmission`、`CandidateList_LaterSelectedScreenWins`、`CandidateList_RebuildKeepsUnaffectedGroupStateAndResetsAffectedGroup`、`CandidateList_LibraryReplacementRejectsOldBuild`。構築の保留は構築の中の到達の待合せで行う。再構築のケースは、結果の差替え後に一覧を選び直す経路で確認し、表示中に受理済みの変更が入る内部継続は、このテストでは確認せず、`MainWindowDuplicateCandidateListWpfTests` の `CandidateList_OrganizeButtonMergesTheSelectedGroupsAndRebuildsTheListKeepingTheRest` が、実ボタンの一括整理から、再構築での状態の保持、解消したグループの行の消失、ノードの選択の維持まで確認する（受付中のツリー選択の合流は `AcceptedChangeDuplicatePreparationTests` の一括整理の行が担う） |
| 統合候補一覧の構築の失敗・取消・停止 | `RegularChartListOwner.PresentDuplicateData` | [`RegularChartCommitAndLifecycleTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartCommitAndLifecycleTests.CandidateList.cs) の `CandidateListBuild_FailureNotifiesOnceCancelIsSilentAndStopCollectsBuild` が、解決の例外で通知が一度、停止による取消で通知なし、構築中の停止で構築Taskの回収を確認する |

## 関連資料

[BMS・BMSONの共通モデル](chart-model.md)、[警告](warnings.md)、[一覧表示](../ui/table-view.md)、[利用者向け説明](../../../docs/manual.ja.md)を参照します。
