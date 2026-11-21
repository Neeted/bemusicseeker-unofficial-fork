# 分裂配置の確認と整理

## 目的と適用範囲

同じ楽曲が複数のフォルダへ分かれて置かれた状態（分裂配置）の探索、グループ表示、フォルダ統合を定めます。譜面の主ハッシュが一致する重複は[重複譜面の確認と整理](duplicate-files.md)が扱い、本書は、主ハッシュが一致しなくても音源の配置名を多く共有するフォルダを対象にします。

探索の結果は統合の候補であり、同じ楽曲であることも、統合が安全であることも保証しません。統合の可否は、重複と同じ[安全性確認](duplicate-files.md#安全性確認)で利用者が確かめます。保留入力、導入先の一時状態、プレイリストだけの情報は対象に含めません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 対象フォルダ | 所持譜面を直下に一件以上持つフォルダ。所持譜面は重複検索と同じ所持集合の行（パスとMD5を持つ行）です。 |
| 音源キー | [リソース索引のキー](../core/data-and-indexes.md#キー)のうち音声のもの。拡張子を除いた譜面相対のパスを、大文字小文字を無視してハッシュ化した値です。 |
| プレビュー名 | フォルダ直下の `preview`・`_preview`・`__preview` に当たる音源キー。 |
| 汎用名 | 20個を超える対象フォルダが持つ音源キー。 |
| 判定用の音源集合 | 対象フォルダが自身で所有する音源キーの集合から、プレビュー名と汎用名を除いた集合。 |
| 候補フォルダ | 判定用の音源集合が10個以上の対象フォルダ。 |
| グループ | 分裂配置の関係で連結した、二つ以上の候補フォルダの集合。 |

## 仕様

### 判定用の音源集合

対象フォルダごとに、次の順で判定用の音源集合を作ります。

1. リソース索引の項目から、そのフォルダが自身で所有する音声のキー（[`SelfOwned`](../core/data-and-indexes.md#祖先から見える集合と自身の所有分)）を取ります。子の譜面フォルダが所有する音源は含めません。索引に項目がないフォルダは空の集合です。
2. プレビュー名を除きます。除くのは、フォルダ直下にあり、拡張子を除いた名前が `preview`・`_preview`・`__preview` のいずれかに大文字小文字を無視して一致するキーだけです。`preview2` のように一部だけが一致する名前と、サブフォルダの中の `preview` は除きません。
3. 汎用名を除きます。手順2の後の集合について、各キーを持つ対象フォルダを数え、20個を超えるキーをすべての集合から除きます。`01` のような連番名だけを共有する無関係なフォルダを、候補から外すための規則です。
4. 残ったキーが10個以上のフォルダを候補フォルダにします。

音源の実ファイルは読みません。キーは32ビットのハッシュで照合するため、異なる名前が同じ値になる衝突はあり得ます。衝突による誤った一致は許容し、補正しません。

### 分裂配置の関係

二つの候補フォルダは、次の両方を満たすときに分裂配置の関係にあります。

- 判定用の音源集合が小さい方（同数ならどちらでも同じ）のキーの80%以上が、他方の集合にあります。共有数を `s`、小さい方の個数を `n` として、`s × 5 ≥ n × 4` で判定します。
- 両方のフォルダの直下に、同じ主ハッシュの所持譜面がありません。主ハッシュを共有する組は重複検索が扱うため、関係にしません。主ハッシュは重複検索と同じく、大文字小文字を無視して比べます。

10個の条件は両方のフォルダに求めます。フォルダが親子関係にある組も同じ規則で判定し、探索では除きません（安全性確認が統合不可にします）。

### グループ

分裂配置の関係でフォルダを連結し、二つ以上のフォルダを持つ連結成分をグループにします。AとB、BとCがそれぞれ関係にあれば、AとCが関係になくても、A・B・Cは同じグループです。主ハッシュを共有するために関係にならなかった二つのフォルダも、別のフォルダを介して同じグループになり得ます。

結果は重複検索と同じ `DuplicateGroup` で表します。

| 項目 | 内容 |
| --- | --- |
| フォルダ | グループのフォルダを、パスの昇順（大文字小文字を無視する序数比較）に並べます。フォルダの同一性も大文字小文字を無視し、表記は、捕捉した所持集合の行の順で最初に現れた表記です。 |
| 譜面 | 各フォルダの直下にある所持譜面のすべてを、フォルダの順、フォルダ内は捕捉した所持集合の行の順（重複検索が使う捕捉と同じ順）に並べます。 |
| 警告 | 譜面の警告は捕捉時の現在値のままです。探索は警告を付与・解除せず、カタログを変更しません。 |
| 見出し | 先頭の譜面の題名です。 |
| 並び | グループを見出しの序数順に並べ、同じ見出しは先頭のフォルダのパスの昇順（大文字小文字を無視する序数比較）にします。 |

同じ所持集合とリソース索引からは、並列度によらず同じ結果を返します。

### 探索の実行

モデルの入口は `BMSLibrary.SearchSplitPlacementGroups` です。呼出し側が共通のライブラリ変更受付を保持している間に呼びます。

- 有効な結果があれば、探索せずに戻ります。
- 所持集合の行とリソース索引のスナップショットを、探索の開始時に一回だけ捕捉します。捕捉の後は、ライブラリのロックを保持せずに計算します。
- 取消の要求には、準備と照合の途中で応じ、`OperationCanceledException` を送出します。取消と失敗では、途中までの結果を公開せず、保持している状態を変えません。
- 進捗は、準備の段階と、照合を終えた候補フォルダの数・候補フォルダの総数を報告します。報告は探索を実行するスレッド（呼出しスレッドとは限りません）から同期に呼び、受け手は待たずに戻る責任を持ちます。完了数の単調な増加と、最終値の報告は保証しません。受け手が送出した例外は探索の失敗として呼出し側へ伝え、取消と同じく状態を変えません。
- 新しい結果を格納したときは、ロックの外で、呼出しスレッドから結果の変更を一回通知します。0件の結果は空の一覧で、`null` ではありません。

### 結果の保持と失効

`BMSLibrary` は現在の結果を一つだけ保持し（`SplitPlacementGroups`）、保存しません。結果がない状態は `null` です。

- 重複検索の結果が失効する契機（所持集合・フォルダ構成の変更、所持集合の全置換、ライブラリの解除）で、分裂配置の結果も失効し、変更を通知します。
- 結果は、探索に使ったリソース索引の世代を持ちます。探索の要求時に現在の世代と異なれば、有効な結果とは扱わず、探索し直します。画面は、結果があり世代が現在と一致するかを `HasCurrentSplitPlacementGroups` で読みます。音源だけが変わった場合に、表示中のグループをその場で更新することは保証しません。
- 失効した結果は、次の探索でグループのインスタンスを引き継ぐための基底として保持します。新しい結果のグループのうち、フォルダの集合（大文字小文字を無視）と、譜面の所持項目の識別・パス・順序が基底のグループと一致するものは、基底の `DuplicateGroup` インスタンスをそのまま使います。一致しないグループは新しいインスタンスです。引き継いだグループは捕捉時の見出しと譜面値を保ち、題名などの値だけの変更は反映しません。並びは各グループが保持する見出しで決めます。譜面のパスは完全一致（大文字小文字を区別）で比べます。

### 性能

[代表規模](../core/performance-and-scale.md#代表規模)で、フォルダの全対を総当たりで比べません。

- 既存の逆引き（キーからフォルダのパスの列への対応）は使わず、探索の中だけで、キーと対象フォルダの番号の組を整列した索引を作ります。索引は探索の終了時に破棄します。
- 汎用名を除いた後は、どのキーも20個以下のフォルダにしか属しません。候補フォルダごとに、自分のキーを持つフォルダを索引から数え上げて共有数を求め、照合の回数を「音源の登録数 × 20」以下に抑えます。
- 照合は候補フォルダの単位で並列に行います。
- 主ハッシュの共有は、音源の条件を満たした組だけについて調べます。

計測する完了範囲は、探索の要求から結果の公開までです。`SearchSplitPlacementGroups` は、対象フォルダ数、候補フォルダ数、音源の登録数、汎用名の数、関係の組数、グループ数、引き継いだグループ数と、捕捉・準備・照合・グループの構築の所要時間を診断へ記録します。

### ツリーと一覧

メンテナンスのツリーの「重複ファイルチェック」の次に「分裂配置チェック」のルートを置きます。子は、重複のツリーと同じく、[統合候補一覧](duplicate-files.md#統合候補一覧)のノード（結果が一件以上あるときだけ）、グループ、フォルダです。

| ツリーの選択 | 一覧 |
| --- | --- |
| 分裂配置のルート | 全グループの譜面 |
| グループ | そのグループの譜面 |
| フォルダ | そのパス配下の譜面 |
| 統合候補一覧 | 専用の表 |

一覧は `SplitPlacementFilterSelected` による仮想的な部分集合で、選択の値は重複と同じ `DuplicateViewContext` で捕捉します。列設定は重複の一覧と共用します（`DuplicateCustomTableColumnSettings`）。捕捉したグループやフォルダが現在の結果にない場合と、結果がない場合は空の一覧とし、別のグループや全件へ広げません。

ルートまたはその子を選んだときに有効な結果（`BMSLibrary.HasCurrentSplitPlacementGroups`）がなければ、探索を始めます。入口は `SplitPlacementSearchWorkflowOwner.SelectAsync` です。

- 有効な結果があれば、受付を取らず、進捗も表示せずに一覧を表示します。
- 共通のライブラリ変更受付を待たずに取得します（[操作の競合ポリシー](../core/operation-concurrency-policy.md)）。競合時は既存のBusy案内を一件出して探索を始めず、表示中の一覧を変えず、予約しません。受付が閉じている場合は案内せずに未開始とします。
- 探索中は、取消ボタンつきのモーダルの進捗を表示し、準備の段階と「照合を終えた候補フォルダの数／総数」を示します。探索中にほかの操作を受け付けません。受付は探索の終端まで保持し、一覧の表示の前に解放します。受付を保持したままUIの完了を同期的に待ちません。
- 成功したら結果をツリーと一覧へ反映します。結果が0件なら、ルートの一覧は空です。
- 取消では案内を出さず、結果がない状態のまま空の一覧を表示します。失敗は一回だけ報告し、空の一覧を表示します。取消を要求しても探索が結果を格納して終わった場合は、成功として扱います。
- 音源だけが変わって世代が古くなった結果が残っている場合の取消と失敗では、モデルがその結果を保持するため、ツリーと一覧は古い結果を表示したままです。

結果が失効すると、分裂配置の一覧を表示中でもツリーの子と一覧は空になります。自動では探索し直さず、次に利用者がルートを選んだときに探索します。グループやフォルダを選んだまま子が消えると、ツリーは親のルートを自動で選び直しますが、この選び直しは利用者の選択として扱わず、探索もBusy案内も行いません。ルートが選択済みのまま有効な結果がない場合は、利用者がルートをクリックするか、ルートでEnterキーを押すと探索を始めます。ツリーは、変更の通知を受けた時点の現在の結果を読んで同期し、遅れて届いた失効の通知で新しい結果を消しません。

重複のツリーにある単体の統合操作（Ctrl+G、統合先メニュー）、同一フォルダ内の整理、整理後の自動選択は、分裂配置のツリーと一覧には出しません。フォルダ項目の「エクスプローラーで開く」は重複と同じく使えます。フォルダの統合は統合候補一覧からの一括整理だけで行います。

### 統合候補一覧と一括整理

分裂配置の統合候補一覧は、重複の[統合候補一覧](duplicate-files.md#統合候補一覧)と同じ表・操作・[安全性確認](duplicate-files.md#安全性確認)を使います。判定、段階、理由、読み取りを省略する簡易確認は変えません。「別のグループを巻き込む」の判定は、分裂配置の一覧の全グループ（非表示を含む）を基準にし、重複検索のグループは含めません。行の活性化（ダブルクリック、Enterキー）は、分裂配置のツリーの対応するグループを展開してフォルダノードを選びます。表は重複と同じものを使い、表示する一覧だけを切り替えます。状態（SELECT、KEEP、確認結果、並べ替え、検索語、除外語、優先統合先、簡易確認の指定）は分裂配置の結果のグループに対応付け、重複の統合候補一覧とは独立に持ちます。結果がない間は行を空にし、状態は捨てません。再探索でインスタンスを引き継いだグループが状態を保つためです。

一括整理は、重複の[統合候補一覧からの一括整理](duplicate-files.md#統合候補一覧からの一括整理)と同じ入口・確認・進捗・終端を使い、次の点だけが異なります。

- 対象（表示中でSELECTがオンのグループ）に「未確認」のグループが一つでもあれば、先に安全性確認を行うよう案内を一件出し、整理を始めません。受付の取得、確認の表示、再生停止、モデルの呼出しを行わず、SELECT・KEEP・確認結果を変えません。
- 「要注意」のグループは、重複と同じく確認ダイアログの承認で対象にします。
- モデルの整理が結果を返して終わった後に有効な結果（`HasCurrentSplitPlacementGroups`）がなければ、同じ受付と画面更新の抑制の内側で探索し直します。音源だけが変わって世代が古くなった場合も含みます。取消で途中で止まった場合と、モデルが操作全体を受理せず未適用で返した場合（Busy、管理出力の保護を取れない場合など）も同じ判定を行い、結果が有効なままなら探索しません。モデルの呼出しが例外で終わった場合は探索しません。この探索は取消の対象にしません。整理で解消したグループは一覧から消え、インスタンスを引き継いだグループはSELECT・KEEP・確認結果を保ち、新しいインスタンスのグループは初期値（未確認・SELECTオフ）になります。探索し直しの失敗は、整理の結果の案内とは別に一回だけ報告します。
- 重複検索の結果は、重複の一覧を表示していないため、整理の後に準備し直しません（ほかのライブラリ変更と同じ扱いです）。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 判定用の音源集合、分裂配置の関係、グループ | [探索部品](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/SplitPlacementSearch.cs)、[グループの組立て](../../../BeMusicSeeker/Models/Library/BMSLibrary.SplitPlacement.cs)、[自身の所有分の読取り口](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/DirectoryResourceLookupCache.cs) | [探索部品のテスト](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/SplitPlacementSearchTests.cs): プレビュー名・汎用名・10個・80%・主ハッシュ・連結の境界と、全組の比較による参照実装との一致。[入口のテスト](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibrarySplitPlacementTests.cs): 自身の所有分、フォルダの同一性と譜面の順、見出しと並び、実走査のプレビュー名 |
| 探索の実行、結果の保持と失効、性能の診断 | [入口と失効・引継ぎ](../../../BeMusicSeeker/Models/Library/BMSLibrary.SplitPlacement.cs)、[失効の接続](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)（`InvalidateDuplicateChartGroupsCache`） | [入口のテスト](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibrarySplitPlacementTests.cs): 通知（呼出しスレッド・ロックなし・一回）、有効な結果の再利用、取消・失敗、照合中のロック、全置換・削除・統合での失効、索引の世代、インスタンスの引継ぎ。診断ログの文字列は固定しない |
| ツリーと一覧：探索の入口、受付・進捗・取消・失敗、ツリーの子、一覧の部分集合、失効、重複との独立、フォルダ項目のメニュー | [探索の手順](../../../BeMusicSeeker/ViewModels/Maintenance/SplitPlacementSearchWorkflowOwner.cs)、[有効な結果の判定](../../../BeMusicSeeker/Models/Library/BMSLibrary.SplitPlacement.cs)（`HasCurrentSplitPlacementGroups`）、[ツリーの子と同期](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateTreeChildCollection.cs)、[分裂配置ツリーの保持](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceTreeViewModel.cs)、[一覧の部分集合](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)（`SplitPlacementFilterSelected`）、[ツリーの接続](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml)、[選択の入口](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [探索の手順のテスト](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/SplitPlacementSearchWorkflowOwnerTests.cs)：有効な結果（0件を含む）での受付・進捗なしの遷移、世代の古い結果の再探索、Busy・閉鎖、探索中の受付の保持とUIスレッド外、進捗画面が先に閉じても探索の終端まで保持、取消・取消を要求しても完了した場合・失敗、遷移の文脈。[入口のテスト](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibrarySplitPlacementTests.cs)：`HasCurrentSplitPlacementGroups`。[ツリーのテスト](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/MaintenanceTreeViewModelTests.cs)：子（結果が一件以上あるときだけ先頭に置く統合候補一覧のノードと、結果の順のグループ）、ノードとグループの同じインスタンスの維持・Resetなし・位置の補正、失効で即空、遅れた失効の通知、UIスレッドだけでの変更、重複との独立。[一覧の構築のテスト](../../../tests/BeMusicSeeker.Core.Tests/ChartList/RegularChartViewBuildAndOrderingTests.cs)：ルート・グループ・フォルダ・未解決・結果なしの譜面（BMSONと兄弟譜面を含み、結果にない譜面を足さない）。[画面受付の網羅](../../../tests/BeMusicSeeker.Core.Tests/ChartList/ChartListVirtualViewTests.cs)・[BMSON行の除外](../../../tests/BeMusicSeeker.Core.Tests/ChartList/LibraryChartRowSortEngineTests.cs)・[列設定](../../../tests/BeMusicSeeker.Core.Tests/ChartList/MainChartColumnSettingsBoundaryTests.cs)：新しい種別の位置づけと `DUPLICATE` の保存値。[実際の接続](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.SplitPlacement.cs)：選択からツリー・一覧への反映、失効、遅れた失効の通知、重複の解析を始めないこと。[画面の代表](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.SplitPlacement.cs)：ルートの見出し、進捗の所有者、フォルダ項目のメニューとショートカット、Busy案内、子が消えたときの自動の選び直しで探索も案内もしないこと（受付が空いている場合と他者が保持中の場合）、選択済みのルートのクリックとEnterキーによる探索（有効な結果があれば何もしない）。表示文言の辞書の整合は既存の `LocalizationResourceParityTests` |
| 統合候補一覧と一括整理 | [ノードの同期](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceTreeViewModel.cs)（`SplitPlacementCandidateListNodeItem`）、[一覧主体と確認の主体の構成](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs)（重複とは別のインスタンス）、[表示中の一覧主体の参照口](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)（`DisplayedCandidateList`）、[共用の表の束縛とノード・行の活性化](../../../BeMusicSeeker/Views/MainWindow/MainWindow.DuplicateMergeCandidateList.cs)（[`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml)）、[一覧の構築と採用](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)（画面種別から組を選ぶ）、[結果なしの入口](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMergeCandidateListOwner.cs)（`ShowNoResult`）、[一括整理](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.BatchOrganize.cs)（`OrganizeSelectedSplitPlacementGroupsAsync`）と[再探索の接続](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs)（`followUp`・`IDuplicateMaintenanceStore.SearchSplitPlacementGroups`）、[未確認の案内の文言](../../../BeMusicSeeker/Properties/Resources.resx) | [一括整理のテスト](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerSplitPlacementTests.cs)：未確認を含む整理（三段階の混在・受付が他者に保持中でも案内一件だけで、受付・確認・モデル・探索に触れない）、対象0件・Busy・閉鎖、要注意と承認・分裂配置の一覧だけが対象・非表示の未確認が妨げないこと、再探索（受付の保持中・案内より前・取消を要求しても取り消されない・インスタンスの引継ぎ・重複結果を明示準備しない）、再探索の失敗の報告一回と結果の案内一回、重複の一覧からの整理の従来の挙動。[一覧主体のテスト](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/DuplicateMergeCandidateListOwnerTests.cs)：結果なしの入口と、その後の採用での引継ぎ・初期値・破棄。[ツリー](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/MaintenanceTreeViewModelTests.cs)・[探索の手順](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/SplitPlacementSearchWorkflowOwnerTests.cs)のテスト：ノードの有無と同一性、ノードの選択の文脈。[一覧の構築と採用](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartNavigationTests.SplitPlacement.cs)：結果なしでの状態の保持と次の結果での引継ぎ・初期値・破棄（0件を含む）、古い構築を採用しないこと。[画面の代表](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.SplitPlacement.cs)：共用の表の束縛と二つの一覧の独立、実際の検査器による確認の行き先と基準、行の活性化、実際の整理ボタンからの整理と一覧の作り直し（実ファイル・DB）。表示文言の辞書の整合は既存の `LocalizationResourceParityTests` |

## 関連資料

- [重複譜面の確認と整理](duplicate-files.md): グループの型、統合候補一覧、安全性確認、一括整理の正本です。
- [データと索引](../core/data-and-indexes.md#リソース索引と所有関係): 音源キーと、自身の所有分の定義です。
- [データ規模と性能](../core/performance-and-scale.md): 代表規模と、計測する完了範囲の原則です。
