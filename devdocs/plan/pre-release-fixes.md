# 3.2.0.0公開前の修正

## 目的と範囲

3.2.0.0の公開前調査で見つかった課題を、任意改善を含めてすべて修正する。課題ごとの成立条件・原因・影響は[課題の詳細](#課題の詳細)に置き、本書の実装単位の契約がそこに書いた案より優先する。性能計測は計画の是非の判断材料に使わない。未リリース分（`dev` だけ）が永続化したデータの移行は扱わず、v3.1.0.0が書いたデータからの互換だけを考える。

進め方は[エージェント運用](../spec/development/agent-workflow.md)に従う。各単位は、計画案 → 必要な設計相談 → 必要な独立テスト設計と承認 → `plan-clarifier` の引継ぎ点検 → 実装 → 整形・ビルド・対象moduleの局所MTP実行 → 凍結 → 新しい `preflight-static-review` → 最終レビュー → `verify` → ブランチ内コミット → `dev` への `--ff-only` 統合の順に進める。予備レビューは指摘のたびに新しい担当で行い、3回目の予備レビューや最終レビューの3回目の再レビューが必要になった単位は、再計画して以後の修正を `issue-resolver` へ渡す。これらの工程は効率化のために省かない。

## 利用者が確定した判断

| 課題 | 判断 |
| --- | --- |
| 更新後初回の全件再解析 | 受付の保持は現行のまま。拒否の案内と進捗の見せ方だけを整える。 |
| 初回設定・設定不備で開始できない理由 | 入力・修正が必要な項目を案内する（[設定の不備の案内](#設定の不備の案内)）。 |
| 資源のない複数曲分の差分フォルダ | 意図した変更ではなく、`11c33f362` が仕様・テストとともに持ち込んだ退行。混在zipの対応とv1.2.1.0の同一パッケージの要件を保って規則を設計し直す。補助画像もまとめる根拠に含め、Stella 7475の実物（`.tmp/007475.zip`）を問題なく扱えること（[保留パッケージの分け方](#保留パッケージの分け方)）。 |
| フォルダ統合の配下の移動 | 配下ごと移す現行の挙動を維持する。確認画面に配下を移す旨を添える程度の警告だけを加える。 |
| 検索候補を選んでいないEnter・Tab | Enterは入力語のまま確定して履歴へ、Tabは通常のフォーカス移動にする。 |
| 右クリック項目・履歴プリセットの即時保存 | 「既定値へ戻す」と削除に確認を加える。 |
| 区切り文字を含む旧出力名 | 実際の利用者はいない前提で、起動を止めないことと、アプリ内で直せることだけを保証する最小の対応にする。 |
| URL補完の反映後のプロパティ保存 | 保存できるように直す。 |
| Everythingが索引していない範囲 | 現行の扱いを維持し、保証外であることを設計判断と仕様に明記して、今後の監査で再指摘されないようにする。 |
| 実機確認 | 行わない。 |
| 全再初期化でスコアを読み直さない | 意図した仕様。仕様に明記する。 |
| SQLiteの構文 | LR2の `song.db` に限らず、本アプリが扱うSQLite全般の共通方針として定める。 |

## 実装単位

区分は[重大変更の判定](../spec/development/agent-workflow.md#重大変更の判定)による。重大は `critical-static-review`、通常は `repo-static-review` で最終レビューする。許可パスは目安であり、単位責任者が調査後に確定して引継ぎへ書く。新しい表示文言は `Resources.resx`、`Resources.cs`、`lang/` の6言語を同じ単位で揃える。リソース辞書は複数単位が追記するため、`dev` への統合時の更新で両方の追加を残して競合を解消する。利用者に見える挙動の変更は[利用者向け文書の編集方針](../README.md#利用者向け文書の編集方針)に従ってマニュアル・リリースノートへ反映する。

### SQLiteの互換範囲

- 区分: 重大（永続化・互換性）。対象: [LR2のsong.dbへ作る式索引](#lr2のsongdbへ作る式索引)。
- 契約: 本アプリが作成・変更するすべてのSQLiteファイル（スタンドアロンのアプリDB、LR2連携の `song.db`、スコアDB、将来追加するもの）に残すスキーマ定義（表・索引・トリガー・ビュー、列の既定値・CHECK）は、SQLite 3.6.7が解析できる構文と関数だけを使う。部分索引、式索引、生成列、`WITHOUT ROWID`、`STRICT`、UPSERT、CTE、ウィンドウ関数、`RETURNING`、3.6.7より後に追加された関数（`char`、`unicode`、`instr`、`printf`、`iif`、JSON関数など）を含めない。アプリ自身が実行する問い合わせも同じ範囲を原則とし、超える場合は理由を仕様の例外表に書く。DBの種類で規則を分けない。
- 正本: [データと索引](../spec/core/data-and-indexes.md)へ「SQLiteの互換範囲」を新設し、[LR2履歴のスキーマ](../spec/playlist/lr2-history-schema.md)の個別規定はそこへの参照に置き換える。[LR2楽曲DB](../spec/integration/lr2-song-db.md)からも参照する。
- 修正: `playlist_entry_idx_candidate` を素の列の索引へ戻し、空白・NULLの正規化と大文字小文字の比較は問い合わせ側で行う。既存の式索引は、定義不一致時に作り直す既存の処理で置き換わるため、移行処理は追加しない。全DDLと、問い合わせで新しい関数を使う既存箇所（`Lr2FolderExistingRowLookup`、`LR2SongDBExtended`、`PlaylistPersistenceRepository`）を監査し、書き換えるか例外表へ理由を記載する。
- 検証: 各DB種別のスキーマ準備後の `sqlite_master` の定義が許可範囲だけで構成されることを、共通の検査で確認する。判定基準は `test-contract-designer` が仕様から定める。
- 許可パスの目安: `BeMusicSeeker/Models/BmsLibraryInternal/Playlist/`、`Models/BmsLibraryInternal/Lr2/`、`Models/LR2/`、関連テスト、上記仕様。

### 受付使用中の拒否通知

- 区分: 重大（受付・結果型・通知の責務）。対象: [ライブラリ操作の受付が使用中のときの拒否通知](#ライブラリ操作の受付が使用中のときの拒否通知)、[更新後初回の譜面情報の全件再解析](#更新後初回の譜面情報の全件再解析)の拒否表示。
- 契約: 利用者が明示した要求が共通の受付（L）または管理出力の受付（P）の競合で拒否された場合は、副作用なしで `Warn_LibraryOperationBusy` を警告として一回だけ出す。汎用の「予期しないエラー」は実際の処理失敗だけに使う。確認ダイアログを先に出す入口は、確認の前に受付の可否を検査する（予約はしない）。背景処理、受理済みの継続、自然終了による次曲の開始は案内しない。メニューやボタンを使用中に無効化する方式は採らない。
- 範囲: 右クリックの「ごみ箱へ移動」「移動」「無効な拡張子に変更」「文字コード」、Pの交差による拒否（所持・保留）、フォルダの自動リネーム（選択・一括）、インストール履歴・保留履歴の消去、重複の統合とハッシュ整理、全譜面の再検査、ゼロノートの再確認、設定の「LR2の派生データを再同期」、独立導入先の選択、試聴の明示的な開始（再生ボタン、行の起動、前へ・次へ）。単位責任者はLとPの取得箇所を列挙し、明示要求の入口に漏れがないことを確認する。
- 正本: [操作の競合ポリシー](../spec/core/operation-concurrency-policy.md#競合拒否と見送り)に明示要求の案内規則を一か所で定め、[ライブラリ変更](../spec/library/mutations.md)、[設定](../spec/runtime/settings.md)、試聴の仕様はそこを参照する。
- 許可パスの目安: `BeMusicSeeker/ViewModels/ChartOperations/`、`ViewModels/Install/`、`ViewModels/Maintenance/`、`ViewModels/Playback/`、`ViewModels/Settings/` の該当箇所、`Views/MainWindow/MainWindow.cs` の通知の接続、関連テスト、上記仕様。

### 進捗表示

- 区分: 通常。対象: [譜面情報の補完の件数表示](#譜面情報の補完の件数表示)、[ステータスバー仕様の段階数と子行の所属](#ステータスバー仕様の段階数と子行の所属)。
- 契約: 譜面情報の補完の行は、v3.1.0.0と同じく件数と対象パスを出す。起動由来の補完は「起動に伴う追加処理」の子、それ以外は独立行とし、汎用の実行行と二重にしない。[ステータスバーの進捗](../spec/runtime/statusbar-progress.md)の段階数・表示例・子行の所属は[進捗](../spec/runtime/progress.md)の規則に揃え、固定数を書かない。全件再解析の間に利用者が待つ理由と残り量は、この行で分かるようにする。
- 許可パスの目安: `BeMusicSeeker/ViewModels/MainWindow/`（進捗の購読と行の生成）、関連テスト、上記仕様。

### 設定の不備の案内

- 区分: 重大（設定の保存受理条件と起動時検査）。対象: [初回設定と修復で開始できない理由](#初回設定と修復で開始できない理由)、[前提が成立していない有効化チェック](#前提が成立していない有効化チェック)。
- 不備の一覧: 起動時検査の結果を、文字列の連結ではなく「ページ・対象の欄・理由」を持つ項目の一覧として一か所で作る。起動時検査、開始ボタンの可否、設定画面の案内、ページ一覧の印、起動時の失敗通知はすべてこの一覧を使い、判定を重複させない。
- 設定画面の案内: 不備がある間は、初回設定でも通常の設定画面でも「対応が必要な設定」の欄を常に表示する。各行は「ページ名: 理由」で、選ぶとそのページへ移り、対象の欄までスクロールしてフォーカスを移す。ページ一覧の該当ページに印を付け、対象の欄の直下にも理由を出す。開始ボタンは不備がある間は無効のままでよく、理由は案内欄で示す。LR2の `config.xml` の親子関係の検索ルートなど、ボタンが無効になる理由も同じ一覧に載せる。
- 起動時の失敗: 汎用の「設定を確認してください」だけで終わらせず、不備の一覧を添えて設定画面を案内欄つきで開く。既存の起動失敗後の修復の流れと揃える。
- 前提を要する有効化チェック: 組が成立した入力だけを保存する。LR2設定ファイルのバックアップは、チェック前から出力先を選べるようにし、出力先なしでチェックしたときはフォルダ選択を開き、取消なら保存しない。beatorajaのスコアDB、外部画像、beatorajaのBMT出力も、未成立の理由を対象の欄に表示する。既に保存された不整合の組（v3.1.0.0以前のデータなど）は、上記の案内欄に出て利用者が直せる。
- 正本: [設定](../spec/runtime/settings.md)へ不備の一覧・案内欄・組の保存条件を書く。新しい文言は6言語で揃える。
- 許可パスの目安: `BeMusicSeeker/ViewModels/Settings/`、`Views/Settings/`、`ViewModels/MainWindow/MainWindowViewModel.cs` の起動時検査の接続、関連テスト、上記仕様。

### 設定画面の操作

- 区分: 通常。設定の不備の案内と同じファイルを扱うため、同じワークツリーでその統合後に直列で進める。対象: [追加出力先の複数追加の途中で拒否されたとき](#追加出力先の複数追加の途中で拒否されたとき)、[追加出力先の追加・改名後の選択](#追加出力先の追加改名後の選択)、[右クリック項目と履歴プリセットの即時保存](#右クリック項目と履歴プリセットの即時保存)。
- 契約: 複数追加では拒否したものだけを除き、有効なものは保存し、拒否理由は一回にまとめて通知する。確定後の再同期は、現在の選択（改名後のパスを含む）が残っていれば維持する。BMSディレクトリ一覧が同じ形なら同じ契約に揃える。右クリック項目の「既定値へ戻す」、項目の削除、履歴プリセットの削除は確認を出し、取り消せば保存も一覧の変化も起こさない。
- 許可パスの目安: `BeMusicSeeker/ViewModels/Settings/`、`Views/Settings/Pages/`、関連テスト、[設定](../spec/runtime/settings.md)。

### LR2の局所同期

- 区分: 重大（受付・確定後の失敗・song.dbの書込み範囲）。対象: [通常出力先を走査ルートに含むLR2連携で確定後にBusy失敗になる](#通常出力先を走査ルートに含むlr2連携で確定後にbusy失敗になる)、[譜面フォルダの削除・移動後の中間親フォルダのfolder.date](#譜面フォルダの削除移動後の中間親フォルダのfolderdate)、[導入後のNEW SONGフォルダの行](#導入後のnew-songフォルダの行)。
- 契約: 局所同期でPの交差判定に渡す集合は、実際に行を書き換える範囲だけにする。変更と無関係な登録ルートは含めない。LとPの両方が書く範囲が変更に入る場合だけ、物理変更の前に同じ規則で判定してL＋Pを確定させ、確定後にPを新たに取る経路をなくす。時刻を取り直す対象には、削除・移動元の存続する直接の親を加え、P判定の集合と同じ定義から作る。局所同期の確定後、追加した行が新曲の期間内で組込みの新曲フォルダの行がなければ、その行を作る（全譜面は走査しない）。
- 正本: [LR2楽曲DB](../spec/integration/lr2-song-db.md)、[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md)。
- 許可パスの目安: `BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs`、`Models/BmsLibraryInternal/Lr2/` の局所同期と範囲生成、関連テスト、上記仕様。表の出力処理（`PlaylistCustomFolderOutputMaintenanceOwner`）はプレイリストの単位が扱う。

### 保留パッケージの分け方

- 区分: 重大（導入範囲・保留登録・テストの判定変更）。対象: [資源のない差分を複数曲分まとめたフォルダ](#資源のない差分を複数曲分まとめたフォルダ)。経緯は `11c33f362` を起点に、単位責任者が `git show` で確認する。
- 要件: (1) 資源を同梱しない別々の曲の差分を入れたフォルダ（フォルダ指定・全項目選択・書庫）は、曲ごとに推定・導入できる。(2) v1.2.1.0で対処したStella 7475のように、同じフォルダの差分が譜面ごとに別のキー音を使い共有資源名が少なくても、同梱したpreview・補助画像などを漏らさず一つのパッケージとして導入できる。(3) 差分単体と、フォルダ別の追加音源付き差分が混在する書庫を、子作品の発見漏れなく扱う。(4) 一部の譜面だけを選んだ追加は自動でまとめない。(5) 未発見の子作品を導入時に巻き込まない。(6) 自己資源を持つ入れ子の導入拒否、重複保留の登録整理、直接指定の隣接資源による親フォルダ化は維持する。

#### 考え方

パッケージの単位は、導入される物理範囲と、導入先が一括で適用される範囲を同時に決める。単体はその譜面ファイルだけを導入し、ディレクトリ単位は配下すべてを一つの導入先へ移す。推定は単位内の全譜面の参照の和集合で候補を選び、手動で指定した導入先は単位の全項目へ適用される。このため、分け方の誤りは向きによって重さが違う。

| 誤り | 結果 | 扱い |
| --- | --- | --- |
| 同梱資源があるのに単体へ分ける | 単体は譜面ファイルしか導入しないため、同梱資源が導入されずに残る（導入漏れ）。 | 避ける。v1.2.1.0でStella 7475に対処した課題そのものである。 |
| 同梱資源がない譜面をまとめ損ねて単体にする | 譜面ファイルを導入する点は同じで、導入漏れは起きない。失うのはフォルダ単位の導入とツリー表示、未参照の付属物（readme等）の同伴だけ。 | 許容する。 |
| 無関係な譜面をまとめる | 和集合の推定で候補が出にくくなり、手動の導入先が全譜面へ適用されて別曲が同じフォルダへ入る（誤導入）。 | 避ける。 |

ここから次の方針を取る。同梱資源は、まとめる必須の根拠とする。同梱資源がなければ、同じ作品である強い根拠がある場合だけまとめ、迷う場合は単体にする。子作品を持つディレクトリは、親に同梱資源がなければまとめない。まとめると子作品も親の導入先へ動き、誤導入になるためである。親に同梱資源がある場合は親子一体の作品とみなし、親子の両方が自己資源を持つ場合は既存の入れ子の導入拒否で止める。

同じ作品の根拠には、導入先推定と同じ正規化による `(タイトル, アーティスト)` のキーの完全一致を使う。この正規化は難易度・差分の表記を吸収し、MIX名や異なるリミックス作者は区別する（[導入先推定のメタデータ照合](../decisions/install-estimation-metadata-matching.md)）。v3.1.0.0の参照名の共通率は、`01.wav` のような汎用のキー音名で別曲を一致させ、まとめ過ぎの側に誤るため採らない。類似度による一致も、同じ作者の別の曲を一致させるため採らない。

v3.1.0.0の推定後の再統合は戻さない。推定の成否でまとまるかが決まり、各譜面の音源充足を要求する現行の自動設定では成立しにくい。重複保留の登録整理と再ドロップで再統合済みの単位が単体へ戻り、手動の導入先を失う。v1.2.1.0で再統合が必要だったのは、当時previewと補助画像を同梱資源として数えず、同梱物を持つ差分集を単体へ割っていたためであり、同梱資源を発見時の根拠に含めれば再統合は要らない。

#### 規則

子ディレクトリは常に再帰して発見する。直下に譜面があるディレクトリDは、次の順に最初に当たった規則で決める。

1. 同梱資源がある → D全体を一つにする（子譜面を含む。入れ子の導入拒否は現行どおり）。同梱資源は、直下の譜面が参照し（未使用の定義を含む）D配下に実在する音声・BGA・動画・補助画像（STAGEFILE・BACKBMP・BANNER）・PREVIEWの参照先と、D直下の `preview`・`_preview` で始まる音声である。どの譜面も参照しないファイル（readme、未参照の画像など）は根拠にしない。
2. 譜面を持つ子ディレクトリがある → 直下の譜面を各単体にし、子は別に発見する。
3. 直下の譜面が1件 → D全体を一つにする。
4. 直下の全譜面の正規化した `(タイトル, アーティスト)` のキーが一つに揃う → D全体を一つにする。タイトルが空の譜面があれば揃わないとする。
5. それ以外 → 直下の譜面を各単体にする。

直接指定したファイルを親フォルダごと扱う判定は、規則1と同じ根拠を使う。部分選択は単体、全項目選択はフォルダ指定と同じに扱う。単位は発見時に確定し、推定の後に組み替えない。重複保留の登録整理と起動時の復元も同じ判定を使う。dev HEADとの違いは規則3〜5だけで、現行の「同梱資源も子譜面もなければ直下の譜面数によらず一式」を置き換える。規則1・2は現行の挙動を保つ。

#### 受容する割り切り

- 同梱資源を持つ平坦なフォルダに別々の曲の差分が混ざる場合（例: 全譜面が共通のバナーを参照する複数曲の差分集）は、一つにまとまる。分けると同梱資源の導入漏れになるため、まとめる側を選ぶ。各譜面の音源充足を求める自動設定は成立しないため自動では誤導入しないが、手動の導入先は全項目に適用される。
- 同梱資源がない同じ曲のリミックス集や、タイトル表記が揃わない差分は単体になる。導入漏れは起きない。

#### 期待結果

| 入力 | 結果 |
| --- | --- |
| 同梱資源のない別々の曲の差分を平坦に入れたフォルダ（フォルダ指定・全項目選択・書庫） | 規則5で各単体。曲ごとに推定・導入する。 |
| 同梱資源のない同じ曲の難易度違いの差分（`Title [Another]`、`Title [Hyper]`） | 規則4で一つ。 |
| Stella 7475（下記） | 規則1で一つ。 |
| 直下に同梱資源のない差分、`songX/` に追加音源付き差分がある書庫 | 規則2で直下は各単体、`songX` は規則1で一つ。 |
| 通常の作品フォルダ（譜面と共有音源） | 規則1で一つ。 |
| 1曲の差分だけのフォルダ | 規則3で一つ。部分指定なら単体。 |
| フォルダ内の一部の譜面だけの選択 | 各単体。選んだ譜面に同梱資源があれば親フォルダで規則を適用する。 |
| 親に譜面と同梱資源、子にも譜面 | 規則1で一つ。子も自己資源を持てば導入を拒否する。 |
| 同梱資源のない別曲2件とreadmeだけの書庫 | 規則5で各単体。readmeは導入しない。 |
| 全項目選択の後に同じフォルダを再ドロップ | 同じ分類を再現し、導入先を引き継ぐ。 |
| 補助画像を全譜面が参照する複数曲の差分集 | 規則1で一つ（割り切り）。 |

#### Stella 7475の実物

`.tmp/007475.zip`（リポジトリに含めない配布物）は、フォルダ `千年女王の幻燈` に4譜面を持つ。タイトルは `千年女王の幻燈 (Postrock mix)` などリミックス名が異なり、アーティストも `微熱 vs. cybermiso`、`微熱 + MONOLITH` など共作表記が異なる。キー音は同梱せず、各譜面の `#PREVIEW`（`preview_*.ogg`）と `#BACKBMP`（`_backbmp_*.png`）、1譜面の `#STAGEFILE`（`title_Another.png`）、フォルダの `preview.ogg` を同梱する。規則1で一つになり、規則4には依存しない（正規化はMIX名を残すためキーは揃わない）。この単位では次を確認する。

- 実物の書庫を、コミットしない一時の検証として取り込みから発見・推定入力まで通し、一つのパッケージになり、同梱の5音声・5画像がすべて導入範囲に入ることを確認して引継ぎへ記録する。
- 恒久テストは、同じ構成（タイトル・アーティストが揃わない、キー音なし、譜面ごとのPREVIEW・BACKBMP、一部のSTAGEFILE、フォルダのpreview）を合成した入力で固定し、元曲のキー音を持つライブラリへの推定入力が和集合で一つの候補になることも確認する。
- 導入先に同名の `preview.ogg` などがある場合は既存の上書き規則に従い、この単位では変えない。

#### 実装・テスト・正本

- テスト: `11c33f362` で反転・新設・削除したテストは正本として扱わない。`test-contract-designer` へは上記の考え方・規則・期待結果だけを渡し、実装や現在の期待値を渡さずに判定基準を定めさせる。少なくとも、`PackagesSplitsIndependentChartsIntoSingleFilePackages` の子なしの行、`PreservesInputUnitBeforeDestinationEstimation`、`PackageInstallBundledArchiveTests` の説明を見直す。期待結果の表の入力は、取り込みから導入まで一続きで確認する。
- 正本: 考え方・採らない案・割り切りを設計判断 `devdocs/decisions/pending-package-grouping.md` として新設し、[設計判断の一覧](../decisions/README.md)へ載せる。[ドロップ導入](../spec/library/drop-install.md)の「単体分割と子作品の発見」は規則と期待結果へ書き換え、「資源がなく子譜面もなければ一式」の記述を除く。[導入先推定](../spec/library/install-estimation.md)の「検出した物理範囲の保持」の「全項目選択は一式」という言い回しを規則への参照に変える。
- 許可パスの目安: `BeMusicSeeker/Models/BmsLibraryInternal/Install/`、`Models/Install/`、関連テスト、上記仕様。受付使用中の拒否通知の単位も `ViewModels/Install/` を扱うため、同じファイルに触れる場合は統合順を全体ルートと調整する。

### プレイリスト

- 区分: 重大（保存の競合判定・出力配置・起動時の必須処理）。対象: [URL補完の反映後にプロパティを保存できない](#url補完の反映後にプロパティを保存できない)、[自動整列の表でフォルダを削除すると自動が外れる](#自動整列の表でフォルダを削除すると自動が外れる)、[内蔵の難度推定表と推薦表の更新日時が表示言語で進む](#内蔵の難度推定表と推薦表の更新日時が表示言語で進む)、[BMTキャッシュから復元した表の出力フォルダの既定](#bmtキャッシュから復元した表の出力フォルダの既定)、[区切り文字を含む出力名の一つの名前への整形](#区切り文字を含む出力名の一つの名前への整形)、[表の削除・改名の後の出力基点のfolder.date](#表の削除改名の後の出力基点のfolderdate)。
- 契約:
  - プロパティ保存の現行性確認は、所属と解決キーの版で行い、利用者操作でないURL補完の反映を編集の競合とみなさない。補完した値を保存で失わない。
  - 明示順が空の表ではフォルダの削除・名前なしへの改名で明示順を作らず、フォルダ欄を編集していない保存では `FolderOrder` を変更対象にしない。空文字だけの明示順は自動として読む。
  - 内蔵表の生成ヘッダーの識別から表示言語に依存する値を除き、言語に依存しない識別を使う。一度だけ保存済みの識別と食い違う際の日時の前進は許容する。
  - 新規表の出力除外の既定を設定から決める処理を登録側の一か所へ寄せ、取得成功とBMT復元の両方に適用する。
  - 旧出力名は最小の対応とする。整形後の出力先が重なった場合も起動全体を失敗させず、衝突した表を出力対象外にして表名と出力先を一回通知し、他の表とLR2同期を続ける。プロパティ保存・一括編集・表削除の事前検査は編集後または削除後の集合で判定し、衝突を解消する編集を受理する。旧階層の出力は自動で移動・削除しないことを仕様に明記し、移行処理は作らない。
  - 表の削除・改名後の整理と同じ同期で、出力基点までの祖先の時刻を更新する。
- 正本: [保存と出力](../spec/playlist/storage-and-export.md)、[プロパティ](../spec/ui/playlist-properties.md)、[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)、[表URL取込み](../spec/playlist/table-url-import.md)、[ローカルのリコメンド](../spec/playlist/local-recommendations.md)。
- 許可パスの目安: `BeMusicSeeker/Models/Playlist/`、`Models/BmsLibraryInternal/Playlist/`、`ViewModels/Playlist/`、`Models/BmsLibraryInternal/Lr2/ManagedPlaylistOutputPlacement.cs`、`Lr2FolderFileDbSyncService.cs`、関連テスト、上記仕様。LR2の局所同期の単位と `Models/BmsLibraryInternal/Lr2/` を共有するため、同じファイルに触れる場合は後から統合する側が更新して整合を確認する。

### メイン画面の表示状態

- 区分: 通常。受付使用中の拒否通知の単位と `MainWindow.cs` を共有するため、その統合後に着手する。対象: [起動中に拒否した操作で表示だけ変わる](#起動中に拒否した操作で表示だけ変わる)、[起動時の初期選択の設定がその場でツリー選択を変える](#起動時の初期選択の設定がその場でツリー選択を変える)、[導入しなかった操作でもツリー選択が移る](#導入しなかった操作でもツリー選択が移る)、[表単体の再読込みの完了時の選択](#表単体の再読込みの完了時の選択)、[最大化から最小化して終了したときの次回の表示](#最大化から最小化して終了したときの次回の表示)、[メニュー項目の名前の最初の下線](#メニュー項目の名前の最初の下線)、[譜面ビューアで開く操作の無通知](#譜面ビューアで開く操作の無通知)、[フォルダ統合で統合元配下の別作品も移る](#フォルダ統合で統合元配下の別作品も移る)。
- 契約: 抑止中に拒否した入力は部品の表示状態も変えない。起動時の初期選択の設定は次回起動の初期選択だけに使い、変更時点の選択を変えない。導入・除去で対象が保留から外れた場合だけ選択を移す。表単体の再読込みは、選択が対象のまま、または置換で失われて他が選ばれていない場合だけ選び直し、受付の拒否では動かさない。最小化で終了したときは「元に戻すと最大化」の情報を保つ。利用者由来の文字列を見出しにする4か所のメニューは `_` をそのまま表示する。未所持の複数選択のメニューは選択件数で文言と可否を決め、何も起きない操作（内蔵表のページを開く、全件登録済みの譜面ビューアへの登録を含む）は理由を通知するか項目を出さない。フォルダ統合の確認画面は、統合元に配下のフォルダがある場合にその件数と、配下も移す旨を添える（確認を表示しない設定では追加しない）。
- 許可パスの目安: `BeMusicSeeker/Views/MainWindow/`、`Views/CustomTable/`、`Themes/`、`ViewModels/ChartOperations/ScoreViewerRegistration.cs`、`ViewModels/Maintenance/` の統合確認、関連テスト、関連仕様。

### 検索

- 区分: 通常（入力操作の互換性変更を含む）。対象: [検索候補を選んでいないEnterとTab](#検索候補を選んでいないenterとtab)、[正規表現の中の縦線](#正規表現の中の縦線)。
- 契約: 候補を選んでいないEnterは入力語のまま確定して履歴へ入れ、Tabは通常のフォーカス移動にする。明示的に選んだ候補だけを適用する。同じ部品を使うプレイリスト一覧の検索欄も揃える。正規表現の条件の本体は `|` で分割せず一つの式として扱い、保存式の同一性判定と候補の構造判定も同じ構文処理に揃える。
- 正本: [キーワード検索](../spec/ui/keyword-search.md)、[検索構文ガイド](../../docs/keyword-search-syntax-guide.ja.md)と他言語版。
- 許可パスの目安: `BeMusicSeeker/Views/Search/`、`ViewModels/Search/`、関連テスト、上記の仕様と文書。

### 音声変換

- 区分: 通常。対象: [外部エンコーダーがない音声変換](#外部エンコーダーがない音声変換)。
- 契約: 変換の実行前に一回だけ形式の可用性を確認し、利用できなければ警告して「WAVEで続行」か中止を選ばせる。中止なら変換せず、どちらでも共有設定の形式を書き換えない。ファイルごとの無通知の代替は削除する。進捗と完了の表示は実際の出力形式と一致させる。
- 正本: [音声変換](../spec/runtime/audio-conversion.md)。
- 許可パスの目安: `BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs` と関連、関連テスト、上記仕様。

### 割り切りの明記

- 区分: 通常（文書のみ）。対象: [Everythingが索引していない範囲の譜面が削除扱いになる](#everythingが索引していない範囲の譜面が削除扱いになる)と、意図した挙動の明記。
- 契約: Everythingによる走査はEverythingの索引を完全な走査として採用し、索引されない範囲（索引対象外のドライブ、ネットワークドライブ、ジャンクション先など）の譜面は保証外であることを、[Everythingの問い合わせ境界](../decisions/everything-query-boundary.md)と[パス長と入出力](../spec/core/path-length-and-io.md)に書く。利用者への案内（索引対象外の場所は通常走査を使う）は利用者向け文書の方針に従ってマニュアルへ加える。全再初期化でスコアを読み直さないこと、操作バーのボタンを使用中も有効にして押下時に拒否すること、自動改名で変更不要のフォルダだけなら実移動0件で終わることを、それぞれの仕様に意図として書く。
- 許可パスの目安: `devdocs/`、`docs/` の該当文書。

## 並列化と統合

作業ワークツリーはメイン以外に同時最大5本とし、メイン作業ツリーは `dev` の統合専用に保つ。着手前に本書と課題の詳細を `dev` へコミットし、各ワークツリーはその版から分岐する。

| 順 | 同時に進める単位 | 備考 |
| --- | --- | --- |
| 第1波 | 保留パッケージの分け方、LR2の局所同期、受付使用中の拒否通知、設定の不備の案内、SQLiteの互換範囲 | 重大な単位を先に進める。 |
| 第2波 | プレイリスト、進捗表示、設定画面の操作、メイン画面の表示状態、検索、音声変換、割り切りの明記 | 空いたワークツリーから順に割り当てる。設定画面の操作は設定の不備の案内のワークツリーで続ける。メイン画面の表示状態は受付使用中の拒否通知の統合後に始める。 |

依存のない単位は着手順に縛らず、検証・独立レビュー・コミットが揃ったものから全体ルートが一件ずつ統合する。`verify` は全ワークツリーで同時に一件だけ実行し、全体ルートが順番を調整する。共有するファイル（`MainWindow.cs`、`ViewModels/Install/`、`Models/BmsLibraryInternal/Lr2/`、リソース辞書）は後から統合する側が `git rebase dev` で更新し、影響する検証とレビューを行う。

## 完了条件

各単位が最終レビューの合格と `verify` の成功を得て `dev` へ統合されていること。全単位の統合後の `dev` で `verify` が成功し、正式リリース前に `full` を実行すること。完了した単位は契約・採用理由・検証の対応を仕様へ移し、本書と課題の詳細から除く。すべて完了したら本書を削除し、[計画の一覧](README.md)から外す。

## 課題の詳細

調査時点のコードに基づく成立条件・原因・影響と、調査時の修正案を残す。行番号は調査時点のもので、着手時に現在の入口と既存テストで再確認する。修正の契約は[実装単位](#実装単位)を優先し、ここに書いた案や分類と食い違う場合は実装単位に従う。

### 調査時の分類: 公開前修正を推奨

#### LR2のsong.dbへ作る式索引

LR2連携では、アプリ所有の表もLR2本体と共有する `song.db` に作る。修復不要の通常起動でも [StartupLibraryInitializationWorkflowOwner](../../BeMusicSeeker/ViewModels/Startup/StartupLibraryInitializationWorkflowOwner.cs) が `EnsureAppOwnedSchema` を呼び、[PlaylistPersistenceRepository](../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistPersistenceRepository.cs) の `RebuildPlaylistEntryIndexes`（855行付近）が `playlist_entry_idx_candidate` を `ifnull(...)`、`HashComparisonExpression` の `CASE ... trim(..., char(...)) ... END COLLATE NOCASE` を含む式索引として作る。式索引はSQLite 3.9.0以降、`char()` は3.7.16以降の構文であり、[LR2履歴のスキーマ](../spec/playlist/lr2-history-schema.md)が前提とするLR2のSQLite 3.6.7はスキーマを解析できず、`song.db` 全体を読めなくなる見込みである。3.2.0.0を一度起動しただけで成立し、v3.1.0.0へ戻しても索引は残る。スタンドアロンのアプリ専用DBはLR2に影響しない。LR2実機での確認は未実施である。

v3.1.0.0の索引は `(<列> COLLATE NOCASE)` だけで、退行である。修正では、候補索引の定義を式を含まない素の列だけの索引へ戻す。正規化した同値の判定は取得後またはクエリのWHERE句で行い、スキーマへ残さない。`RebuildPlaylistEntryIndexes` は既存定義と文字列が一致しなければ作り直すため、定義を戻せば作成済みの式索引も同じ経路で置き換わり、別の移行処理は要らない。候補探索の性能は約21万譜面規模で現行と比較する。

「LR2が開くDBへSQLite 3.6.7で解析できない構文を残さない」契約を `song.db` のアプリ所有オブジェクト全体へ広げ、[LR2楽曲DB](../spec/integration/lr2-song-db.md)にも明記する。検証は、`EnsureAppOwnedSchema` 後の `sqlite_master` の全索引が列名と `COLLATE` だけで構成され、式・`WHERE` 句・新しい関数を含まないことと、式索引を持つDBを開くと素の列の索引へ置き換わることを、既存の索引再作成のテスト系列で確認する。LR2履歴スキーマの禁止構文検査と同種の検査を `song.db` へ広げられるかも確認する。

#### ライブラリ操作の受付が使用中のときの拒否通知

共通の受付（L）を他の処理が保持している間に利用者が明示的に要求した操作の拒否表示が、入口ごとに分かれている。[ライブラリ変更](../spec/library/mutations.md)と[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md#競合拒否と見送り)は、通常の変更の競合を `Warn_LibraryOperationBusy` で案内すると定める。

- 一覧の右クリックの「ファイル削除 > ごみ箱へ移動」「移動」「ファイル削除 > 無効な拡張子に変更」「文字コード」は、確認も出ずに「予期しないエラーが発生しました。」のエラーダイアログになる。[SelectedChartMutationWorkflowOwner](../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartMutationWorkflowOwner.cs) が受付の拒否を英文の `InvalidOperationException` を持つ `Failed` として返し（318・487・604行付近ほか）、[MainWindow](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の `NotifyMainWindowOperationFailureAsync`（279行付近）が `Msg_error_unexpected` で表示する。v3.1.0.0はログだけの無言で、汎用エラー表示は退行である。
- 管理出力との交差でPを取れない場合も、同じ管理主体が本文 `Warn_LibraryOperationBusy` の例外を失敗に入れ（722行付近）、表示は汎用エラーになる。[PendingPackageWorkflowOwner](../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs) の同種の例外も右クリックのハンドラーで汎用エラーになる。
- フォルダの自動リネーム（選択・一括）、インストール済み・保留の履歴の消去、重複の統合とハッシュ整理、全譜面の再検査、ゼロノートの再確認は、何も表示せずに実行しない。[FolderAutoRenameWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/FolderAutoRenameWorkflowOwner.cs) は拒否を英文例外の失敗にし、受領がないため [FileDbMutationReport](../../BeMusicSeeker/ViewModels/ChartOperations/FileDbMutationReport.cs) が表示しない。[PackageCatalogWorkflowOwner](../../BeMusicSeeker/ViewModels/Install/PackageCatalogWorkflowOwner.cs) と [DuplicateMaintenanceWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs) は失敗のない `Rejected`、[MaintenanceRescanWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceRescanWorkflowOwner.cs) は `NotStarted`、[ZeroNoteMaintenanceWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/ZeroNoteMaintenanceWorkflowOwner.cs) は `false` を返し、呼出し側は失敗以外を通知しない。全譜面の再検査は確認にOKした後に無言で終わる。
- 入口の事前抑止 `ShouldBlockChartPackageMutationInteraction`（[MainWindow](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) 1621行付近）は譜面変更自身の活動だけを見るため、背景処理のL保持ではメニューが開き押せる。

無言の拒否自体はv3.1.0.0からあるが、3.2.0.0では譜面情報・保守情報の補完、導入可能譜面の保守、LR2全体同期が背景でLを保持するため、起動直後の数秒から十数秒や、後述の更新後初回の全件再解析の間に通常操作で頻繁に到達する。同じ右クリックの「再検査」「警告を無視」、解析失敗の登録の除去、音声変換、保留の各操作、フォルダ名のセル編集、再読込みは既に `Warn_LibraryOperationBusy` を出しており、これらと揃える。

修正方針は次のとおり。

- 上記の管理主体の結果へ、[SelectedChartResourceHealthWorkflowOwner](../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartResourceHealthWorkflowOwner.cs) の `RejectedBusy` と同じ形の受付拒否を加える。拒否時は管理主体が `Warn_LibraryOperationBusy` を警告として一回出し、保留系の `ShowPendingOperationAdmissionBusyAsync` と同じ責務配置にする。ゼロノートの再確認はダイアログ窓口の注入が要る。
- Pの交差による拒否も例外ではなく同じ受付拒否の結果へ揃える。`NotifyMainWindowOperationFailureAsync` と汎用エラーは実際の処理失敗だけに使う。
- 確認ダイアログを先に出す入口（全譜面の再検査、解析失敗の除去、自動リネームの一括）は、確認の前に受付の可否を事前検査し、全再初期化の既存の形に揃える。事前検査は予約ではなく、実受付の拒否は上記の案内で扱う。
- メニュー・ボタンをLの使用中に無効化する案は採らない。背景処理がLを非同期に取るため競合は消えず、L不要の項目まで巻き込み、操作バーを有効のまま押下時に拒否する現行仕様とも分かれる。
- [操作の競合ポリシー](../spec/core/operation-concurrency-policy.md#競合拒否と見送り)へ、利用者の明示要求の競合拒否は `Warn_LibraryOperationBusy` で一回案内し汎用の失敗として扱わないこと、背景処理・必須の継続・全体LR2同期の見送りは案内しないことを一か所で定め、[ライブラリ変更](../spec/library/mutations.md)の記述をそこへ寄せる。
- 設定画面の「LR2の派生データを再同期」の見送りと、独立導入先の選択の拒否も利用者の明示操作が無言で終わるが、仕様は通知を定めていない。案内を加えるかは利用者に確認し、加える場合は同じ文言に揃える。
- 何がLを保持しているかの表示は受付へ保持者の識別を持たせる必要があり、この修正には含めない。

検証は、各管理主体のCoreテストで共通の受付を先取りした状態から要求し、副作用がないこと、記録用ダイアログへ警告アイコンの `Warn_LibraryOperationBusy` が一回だけ渡り `Msg_error_unexpected` が渡らないことを判定する。WPFでは右クリックの4操作の代表と、インストール履歴の「すべて消去」で同じことを確認する。結果値を固定している既存テスト（再検査の `NotStarted`、自動リネームのPのBusyを失敗本文で見るもの）は新しい結果へ期待を移す。

関連する[更新後初回の譜面情報の全件再解析](#更新後初回の譜面情報の全件再解析)と[譜面情報の補完の件数表示](#譜面情報の補完の件数表示)は別項目とする。全件再解析の扱いを決める前でも、他のL保持中の拒否に同じ表示が要るため、この修正は独立に行う。

#### 前提が成立していない有効化チェック

設定の項目保存化により、前提を要する有効化チェックの扱いが項目ごとに分かれている。v3.1.0.0は「保存して閉じる」で全体を検査し、不備の一覧を出して保存しなかった。

| 項目 | 前提 | 前提なしでチェックしたとき | 次回起動 |
| --- | --- | --- | --- |
| LR2設定ファイルのバックアップ | 出力先フォルダ | 保存され、有効化の案内も出る | 起動時検査に失敗し、ライブラリを読み込まない |
| beatorajaのスコアDBを使用 | 確定済みのbeatorajaルートとscore.db | 保存されず、無言でチェックが残る | 影響なし |
| 外部画像 | 画像ファイル | 保存されず、無言でチェックが残る | 影響なし |
| beatorajaのBMT出力 | beatorajaルート | 候補として残り、確定ボタンが理由なく無効になる | 影響なし |

公開前に直すべきなのはバックアップである。LR2連携の利用者がバックアップページでチェックし、出力先を選ばずに閉じるだけで、[SettingsDialogViewModel](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) の `IsLR2BackupEnabled`（2574行付近）が出力先を検査せずに単独で保存する。出力先の参照欄は [BackupSettingsPage](../../BeMusicSeeker/Views/Settings/Pages/BackupSettingsPage.xaml) でチェックの有効状態に従うため、先にチェックしないと選べない。次回起動では [SettingsDialogViewModel.Confirmation](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) の `CheckValidationForInitialization`（740行付近）が `Error_LR2BackupPathNotSet` で失敗し、利用者には汎用の `Msg_init_settings_check` だけが出て、設定画面にも理由が出ない（[初回設定の開始不可の理由](#初回設定と修復で開始できない理由)）。チェックを外せば戻るが、手掛かりがない。v3.1.0.0のUIはこの組を保存できなかったため退行である。beatorajaスコアは [SettingsWindow.Confirmation](../../BeMusicSeeker/Views/Settings/SettingsWindow.Confirmation.cs) の `ScoreSelectionChanged` が `IsScoreSelectionValid` 偽で何も表示せずに戻り、その後のルート選択で未確定入力の破棄確認になる。

修正方針は、前提を要する有効化チェックをすべて「組が成立した入力でだけ保存し、未成立の間はその欄の直下に理由を表示する」に揃えることである。バックアップは外部画像と同じ局所候補（有効化と出力先の組）へ移し、出力先をチェック前から選べるようにするか、チェック時に出力先が無効なら続けてフォルダ選択を開き、取消なら保存しない。beatorajaスコア、外部画像、BMT出力の確定ボタンにも同じ検証表示を付け、既存の `Error_InvalidBeatorajaRootPath`、`Error_InvalidBeatorajaScoreDbPath`、`Error_LR2BackupPathNotSet` などを使う。ルート確定時にスコアの候補を採用するかは別判断とする。外部プレイヤー（uBMplay・BMIIDXView・LR2本体）の使用チェックも同じ起動時検査を持つため、着手時に同じ系統か確認する。[設定](../spec/runtime/settings.md)の局所候補の範囲と保存成功時の案内の節へバックアップを組として追記する。

検証は、設定の項目保存系列で出力先未設定のバックアップ有効化が保存されず検証表示が出ること、beatorajaスコア・外部画像・BMTで理由が表示されることを確認し、起動進捗の系列でバックアップ有効・出力先なしの保存値を設定画面から作れないことを固定する。

#### 通常出力先を走査ルートに含むLR2連携で確定後にBusy失敗になる

LR2連携で通常出力先を設定していると、通常出力先は互換性のため走査ルートに残る（[Lr2SynchronizationPorts](../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SynchronizationPorts.cs) の `CreateExcludedCustomOutputRoots` は追加先とROOT型基点だけを除く）。起動直後の表の自動更新、表の再読込み、プロパティ保存などがPを保持している間に、通常出力先と無関係な導入・削除・移動・改名をすると、ファイルとDBの確定後にLR2の局所同期が失敗する。

[BMSLibrary.Lr2SynchronizationOwner](../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs) の `SyncLr2NormalFoldersForCatalogMutation` は、[Lr2NormalFolderDbSyncService](../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2NormalFolderDbSyncService.cs) の `CreateDirectoryMetadataTargets`（263行付近）が変更と無関係な全ルートを常に含める集合を（927行付近）、そのまま `TryEnterManagedOutputMutation` の交差判定へ渡す（933行付近）。ルートである通常出力先が管理領域と同一と判定され、Pを非待機で取ろうとして失敗し、`Warn_LibraryOperationBusy` の例外になる。物理変更前の交差判定は実際の移動元・先だけで行い非交差ならPを取らないため、確定前と確定後の判定が食い違うことが原因である。例外は確定後に起きるため確定後の必須失敗となり、導入は失敗として返り自動推定へ進まず、`folder` 行は作られず同期状態は `Incomplete` になって、次の全体同期まで残る。実行済みの操作を再試行しても対象はもうない。v3.1.0.0の局所同期はPを取らず、退行である。

修正方針は、局所同期のP判定に渡す集合を実際に行を書き換える範囲へ絞ることである。変更と無関係なルート自身は交差判定から外し、変更対象の祖先として入るルートは残す。通常出力先の基点行のようにLとPの双方が書く行が変更範囲に入る場合だけ、物理変更前のP判定へ同じ規則で含め、副作用の前にL＋Pを確定させる。確定後にPを新たに取る経路はなくす。ルート行の `date` を局所同期で更新する必要があるかも決め、不要なら無関係なルートを更新対象からも外す。同じ交差判定を使う削除・移動・改名・重複統合・自動改名・導入の全入口がこれで揃う。[中間の親フォルダのfolder.date](#譜面フォルダの削除移動後の中間親フォルダのfolderdate)も同じ更新対象の集合を扱うため、同時に着手する場合は集合の定義を揃える。

検証は、導入の管理出力交差のCoreテストへ通常出力先をLR2の走査ルートに含めた構成を加え、P保持中の非交差の導入が必須失敗にならず、`folder` 行が作られ、同期状態が `Incomplete` にならないことを確認する。削除でも同じ構成を一件加える。

### 調査時の分類: 判断保留

#### 更新後初回の譜面情報の全件再解析

譜面情報の解析版が21から22へ上がったため、v3.1.0.0から更新した初回は、メタデータ同梱データに含まれない所持譜面がすべて再解析の対象になる。[CatalogChartInfoOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogChartInfoOwner.cs) は補完の全件処理の間、`EnterAcceptedBackgroundAsync`（666・1288行付近）で取得した共通の受付を保持する。その間、[PlaybackPanelViewModel](../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) の `AcceptTableRowActivation` などが試聴の開始を無言で拒否し、再生中の連続再生も止まる。導入・削除・改名などは拒否される。一覧は旧版の解析値で表示されるため空欄にはならない。v3.1.0.0の補完は受付を取らず、操作性の退行である。全件再解析の所要時間は未計測で、約21万譜面ではHDDで数分から十数分と見込む。別プロファイルの構成は同梱データを取り込まないため全所持譜面が対象になる。

受付の使用自体は[譜面情報](../spec/library/chart-info.md)と[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md)どおりだが、同じ譜面情報の仕様は補完の完了を利用開始の条件にしないとも定めており、版更新時の長時間保持は検討されていない。先に利用者に見える契約を決める。候補は、全件補完を区切りごとに受付を取り直して間で操作を受け付ける、解析入力を変えない試聴を補完中の拒否から外す区分を設ける、現行のまま待たされる理由と進捗を表示する、のいずれかである。前二者は受付規定の変更を伴う。どの案でも[受付使用中の拒否通知](#ライブラリ操作の受付が使用中のときの拒否通知)が前提になり、表示する案では[譜面情報の補完の件数表示](#譜面情報の補完の件数表示)を同じ単位で扱う。

検証は、解析版21の行を持つ実SQLiteから起動相当の補完を始め、その実行中に試聴と導入の受付が採用した契約どおりに受理または案内されることを確認する。所要時間は性能測定の補完系列で約21万件の旧版行からの全件補完を計測する。

#### 初回設定と修復で開始できない理由

初回設定と起動時検査の失敗後の修復で、「この環境で開始」が無効のまま理由がどこにも出ない。[SettingsWindow.xaml](../../BeMusicSeeker/Views/Settings/SettingsWindow.xaml)（96行付近）がボタンを `CanStartEnvironment` に結び、[SettingsDialogViewModel.Confirmation](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) の `CanStartEnvironment`（761〜763行付近）が `CheckValidationForInitialization(out _)` の理由を捨てる。LR2連携の初回利用者は全般ページでLR2を選んだ後も、プレイリストページの通常出力先とROOT出力先を自分で見つける必要がある。LR2の `config.xml` に親子関係の検索ルートがあると「BMSディレクトリを変更」も無効になり、説明文に到達しない。起動時検査の失敗は汎用の `Msg_init_settings_check` だけである。v3.1.0.0は「保存して閉じる」が常に押せ、「<ページ名>: <理由>」の一覧を表示しており、不足項目の一覧を見る手段がなくなった点が退行である。必須項目そのものはv3.1.0.0と変わらない。

[設定](../spec/runtime/settings.md)は開始ボタンを設定操作とライブラリ操作の状態で無効にすると定め、設定不足での無効化は書いていない。案は、開始ボタンの可否をその状態だけにして押下時に検査し、失敗時はページ名を前置した不足項目の一覧を表示して戻る形（v3.1.0.0の体験と一致）と、ボタンは無効のまま下部の案内欄へ不足項目を常時表示する形である。どちらでも、起動時検査の失敗案内へ不足項目を添えるかを同じ契約として決める。[前提が成立していない有効化チェック](#前提が成立していない有効化チェック)で既に不整合な組を保存した利用者も、この表示で自力で戻せる。検証は、初回設定の系列でLR2ルートだけを確定し出力先が未設定のまま開始を要求すると、不足項目とページ名を含む通知が一回出て初期化を始めないことを確認する。

#### 資源のない差分を複数曲分まとめたフォルダ

別々の曲の音源を参照する差分譜面を、音源を含まない一つのフォルダに置いて取り込むと、一つのパッケージになる。[BmsLibraryPackageInstallService](../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) は直下に譜面があり資源も子譜面もなければ直下の全譜面を一つにまとめ、曲の違いを見ない。推定は和集合で充足率を評価するため候補が出にくく、手動導入では全項目が同じ導入先になり、別曲の差分が同じフォルダへ入る。再ドロップしても既存の保留ディレクトリ配下として同じ一件へ戻る。v3.1.0.0は参照資源の共通率で単体へ分けていた。`11c33f362` は、前日に利用者確定として記録した「単一譜面・実在資源・参照共通率による従来のフォルダ化を維持」を同じコミットの中で反転し、共通率の判定と推定後の再統合を削除して、[ドロップ導入](../spec/library/drop-install.md)の「一式」の規定とテストの期待値を新しい挙動に合わせた。利用者合意のない退行であり、現行の仕様・テストは正本として扱わない。混在書庫の本来の要件（直下の単体分割の後も子作品を発見する）は、その前の `d2a343d9d` で共通率を残したまま満たしていた。

修正の規則は[保留パッケージの分け方](#保留パッケージの分け方)で定める。推定後の再統合を戻す案は、重複保留の登録整理と再ドロップで再統合済みの単位が単体へ戻って手動の導入先を失うこと、推定の成否でまとまるかが決まること、汎用のキー音名では共通率が別曲をまとめることから採らない。

#### フォルダ統合で統合元配下の別作品も移る

重複譜面の確認で統合元に選んだフォルダの配下に、重複と無関係な別作品のサブフォルダがあると、それらも相対配置を保って統合先へ移る。[BMSLibrary.LibraryMutationOwner.Merge](../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Merge.cs) の `PrepareMergeDirectory` は統合元配下全体から譜面を集め、物理計画も配下を再帰的に移す。重複グループと画面には統合元直下の譜面しか出ないため、利用者は配下が動くことを操作前に知れない。配下の譜面のうち他所に同じMD5があるものは仕様どおり除去される。挙動は[重複ファイル](../spec/library/duplicate-files.md)とマニュアルの「フォルダ全体」の説明どおりで、データは失われないが元に戻す操作はない。

まず配下ごと移すのが正しいかを決める。正しいなら、確認ダイアログに配下のフォルダも移す旨と件数を出す。直下だけが正しいなら、対象収集・物理計画・後片付け・保留の確定DST追従と[重複ファイル](../spec/library/duplicate-files.md)・[ライブラリ変更](../spec/library/mutations.md)を揃える大きな変更になる。中間案として、配下に別の所持譜面フォルダを持つ統合元を候補から外すか警告する。確認ダイアログは設定で非表示にできる点も考慮する。検証は、統合元直下の1譜面と無関係な子フォルダの譜面を持つ実ファイル・SQLiteの統合で、決めた契約を固定する。

#### 検索候補を選んでいないEnterとTab

検索欄の候補が開いている間は、候補を選んでいなくてもEnterとTabが先頭の候補を適用する。[KeywordSearchEditor](../../BeMusicSeeker/Views/Search/KeywordSearchEditor.xaml.cs) の `ApplySelectedItem`（860〜863行付近）が未選択時に先頭を採用する。`end` などの検索対象名の前方に一致する語を入れてEnterを押すと `end:` に置き換わって0件になり、空欄をクリックしてからTabを押すと先頭の保存済み検索式が適用される。[キーワード検索](../spec/ui/keyword-search.md)は未選択なら最初の候補を適用すると明記しており実装どおりだが、[検索構文ガイド](../../docs/keyword-search-syntax-guide.ja.md)は選択中の候補を入力すると説明している。v3.1.0.0から同じである。

利用者の意図を確認して決める。現状維持なら利用者ガイドを先頭の候補も含む説明へ直す。変えるなら、未選択時のEnterは入力語のまま履歴へ確定、Tabは通常のフォーカス移動とし、明示的に選んだ候補だけを適用する。この場合は開発仕様と、同じ部品を使うプレイリスト一覧の検索欄も揃え、値候補で未選択Enterを補完に使っている利用者への互換性変更として扱う。[アプリ全体のキーボード操作の要求整理](release-follow-up-candidates.md#アプリ全体のキーボード操作の要求整理)と関係するが、この判断を整理の完了待ちにはしない。検証は、検索欄のWPFテストで未選択のEnter・Tabと上下キーで選んだ後の適用を対にする。

#### 右クリック項目と履歴プリセットの即時保存

右クリックメニューページの「既定値へ戻す」は確認なしでWeb・プログラムの両一覧を既定値に置き換えて保存し、項目の「削除」とプレイリストページの履歴プリセットの「削除」も確認なしで保存する。[RightClickSettingsPage](../../BeMusicSeeker/Views/Settings/Pages/RightClickSettingsPage.xaml.cs) から [SettingsDialogViewModel.Confirmation](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) の `CommitRightClickRecord`（1312行付近）を直接呼ぶ。利用者が登録したURLテンプレートや実行ファイルが誤クリック一回で失われ、戻す操作はない。即時保存は[設定](../spec/runtime/settings.md)どおりだが、v3.1.0.0は下書きだけを変えて「キャンセル」で戻せたため、取消経路だけがなくなった。

案は、「既定値へ戻す」は現在の確定値が既定値と異なる場合に両一覧を置き換える旨の確認を出し、削除には確認を付けるかを並べ替えなど他の即時保存操作との釣り合いで決める形である。削除だけ確認する形がv3.1.0.0との体験差を最小にする。決めた内容を[設定](../spec/runtime/settings.md)へ追記する。検証は、確認を取り消すと保存要求がなく一覧も変わらず、承認すると一回だけ保存されることを確認する。

#### 区切り文字を含む出力名の一つの名前への整形

v3.1.0.0では通常型の表の出力先に `Parent\Child` のような階層を保存でき、階層付きで出力していた。3.2.0.0の [BMSTable](../../BeMusicSeeker/Models/Playlist/BMSTable.cs) の `NormalizeOutputDirectoryName` は保存値全体を一つの名前として整形し、`ResolveOutputDirectoryName` と管理出力の配置判定が `ParentChild` を使う。[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)はこの整形と、旧階層の物理ファイルを自動で移動・削除しないことを定めている。これにより二つの問題が生じる。

- 旧階層の `.lr2folder` が残り、通常出力先はLR2の検索ルートに残るため、LR2の選曲画面に更新されない旧フォルダと新しいフォルダが並ぶ。旧ディレクトリを手で消せば解消する。
- 整形後に別表と同じまたは親子の実効パスになる旧保存値（例: `AB` と `A\B`）があると、起動時の検索ルート同期で [ManagedPlaylistOutputPlacement](../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/ManagedPlaylistOutputPlacement.cs) の `Validate` が例外になり、毎回の起動のローカル初期化が失敗してLR2同期・BMT出力・プレイリストの必須準備が止まる。[PlaylistPropertySaveService](../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs) は編集後集合の前に現在保存済みの全表の配置を検査するため、出力先を直す保存も、無関係な表の保存・一括編集・表削除も拒否され、アプリ内で解消できない。到達には旧版で特殊な出力名を保存している必要があり、稀である。

拒否の契約は合意済みのため維持し、次を決める。旧出力は、整形で実効パスが変わる表を起動時に一度だけ通知して残すか、v3.1.0.0の規則で旧実効パスを求め、初回出力の成立後に既存の移行整理へ旧→新の移行として渡すかを選ぶ。後者では旧パスが他表の現在出力を含まないことを同じ配置検査で確かめる。衝突は、起動の検索ルート同期と必須修復で起動全体を失敗させず、衝突した表を出力対象外にして表名と出力先を通知し、他表の出力とLR2同期を続ける。プロパティ保存・一括編集・表削除の事前検査は編集後または削除後の最終集合に一本化し、衝突を解消する編集を受理する（[プロパティ](../spec/ui/playlist-properties.md)の保存直前の実効パス集合の検査と一致する）。[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)の既存保存値と保存済み不正配置の節へ、起動時の扱いと解消手順を書き、マニュアルの出力先の説明も揃える。

検証は、カスタムフォルダ出力のCoreテストで、旧値 `Parent\Child` と旧階層の `.lr2folder` を置いた状態から起動相当の必須修復を通し、採用した方針の通知または整理を判定する。衝突では、起動相当の出力処理が例外で終わらず衝突表を通知すること、衝突表の出力先を変える保存と無関係な表の保存が成功することを加える。

#### URL補完の反映後にプロパティを保存できない

起動完了の直後、URL補完の取得中に表のプロパティを開き、補完が反映された後にOKを押すと、`Playlist properties changed after editing began.` の例外全文が「予期しないエラー」として表示され保存されない。[PlaylistPropertySaveService](../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs) は開始時の編集値と現在の表を比べ、比較に項目版 `EntriesRevision` を含む。[BMSPlaylist.UrlCompletion](../../BeMusicSeeker/Models/Playlist/BMSPlaylist.UrlCompletion.cs) の補完反映はPを取らずに `CommitEntryValues` を呼び、変化があれば項目版を進める。実行時の補完は保存しないため、補完対象のある表では起動のたびに起こりうる。取得はセッションで一回で、取得が終わるまでの数秒から最大60秒に開いた場合に限られ、外部同期の開始後はPのため開けない。取消して開き直せば保存できる。[プロパティ](../spec/ui/playlist-properties.md)は項目の版による同時編集の判定を定めるが、利用者操作でない補完による版の進行を競合とみなすかは定めていない。

項目版を比較から外すと所属に依存する編集の現行性確認が弱まるため避ける。案は、比較を所属と解決キーだけの版（`EntryLookupRevision`）へ替える、URL補完の反映をPの非待機取得に揃えて編集画面の準備・保存と直列にする、例外を翻訳済みの「開き直してください」通知へ替える、のいずれかである。最初の案はプロパティ保存と概要のセル編集の両方を一か所で揃えられる。検証は、編集セッションの作成後に補完を反映しても保存が成功することを確認する。

#### Everythingが索引していない範囲の譜面が削除扱いになる

Everything 1.5の既定で索引されないルート（exFAT・FATの外付けドライブ、ネットワークドライブ、リムーバブル）と、索引されるルートを併せて登録している場合や、ルート配下に別ドライブへのジャンクションがある場合、Everythingの走査結果にそれらの譜面が含まれず、走査のたびに削除として反映される。[EverythingFileScanner](../../BeMusicSeeker/Models/Utils/Scanning/EverythingFileScanner.cs) はルートの存在確認だけで、ブリッジ（[EverythingBridge](../../native/EverythingBridge/EverythingBridge.cpp) の `EBridge_ScanChartAndResources`）の結果を完全として返す。[LibraryFileScanPipelineOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/LibraryFileScanPipelineOwner.cs) は失敗時だけ通常走査へ代替し、[BmsLibraryInitializationService](../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs) の空走査保護は全体0件のときだけ働く。[FileScanParseCommitOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/FileScanParseCommitOwner.cs) が走査にない既存パスを削除とし、favorite・tag・adddateが失われ、`song.db` からも行が消える。通常走査はジャンクション先を辿るため、取得元の切替だけで集合が変わる。v3.1.0.0から同じだが、利用者データの不可逆な消失である。Everythingの索引範囲は外部製品の挙動で、ジャンクションの扱いは未確認である。

[パス長と入出力](../spec/core/path-length-and-io.md)は完全な走査だけを反映すると定めており、索引外のルートを完全とみなす割り切りはない。案は、Everythingの結果を採用する前に各登録ルートの被覆を確認し、確認できないルートやルート配下の再解析点があれば、既存の代替警告とともに通常走査へ代替することである。ブリッジがルート単位の件数を返す形にすれば、0件のルートだけを追加確認でき通常時の性能へ影響しにくい。同じブリッジを使う導入先推定とLR2の `folder` 列挙も同じ被覆判定に揃え、[パス長と入出力](../spec/core/path-length-and-io.md)、README、マニュアルの案内を合わせる。消えた行は戻せないため移行はない。検証は、走査パイプラインのCoreテストで一方のルートを返さない結果を与えて既存行を削除しないことを確認し、ブリッジのテストへルート単位の集計を加え、実際のexFAT・ジャンクションは環境統合の専用環境で確認する。

### 調査時の分類: 任意改善

以下は分類順ではなく領域ごとに並べる。項目は独立しており、同じ形式で追記する。

#### 起動中に拒否した操作で表示だけ変わる

起動時と全再初期化の間は `ShouldBlockStartupUiInteraction`（[MainWindow](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)）で一覧の並べ替えやツリーの選択を拒否するが、部品の表示状態は先に変わったまま残る。

- 列見出しを押すと、[CustomTableView](../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs) の `RequestSort` が要求の送出前に `SetCurrentValue` で並べ替えの列と方向を書き換える（2259行付近）。拒否された要求では `SortParameters` が変わらず、`Mode=OneWay` の Binding が再評価されないため、完了後も矢印だけが実際の並びと食い違う。
- ライブラリツリーと保守ノードの項目を押すと `TreeViewItem.IsSelected` が先に真になり、各 `Selected` ハンドラーは抑止中なら戻るだけで選択を戻さない。完了時の一覧は抑止前の表示で作られ、ツリーの選択表示と食い違う。もう一度押せば `ForceRefreshMainTreeSelection` で切り替わる。

どちらもv3.1.0.0からある。契約を「抑止中に拒否した入力は部品の表示状態も変えない」に揃える。並べ替えは `RequestSort` で `SetCurrentValue` をやめ、矢印をBindingの更新でだけ変える。ツリーは抑止中にマウスとキーボードの選択移動を処理済みにして選択自体を起こさないか、抑止の解除時に選択中の項目へ一度だけ再選択を送る。同じ表部品を使うプレイリスト一覧・履歴・プレイリスト詳細とプレイリストツリーも同じ契約で点検し、[起動](../spec/runtime/startup.md)の起動中の操作制限へ明記する。検証は、共通表のWPFテストで購読側が要求を採用しない場合に矢印がBindingの値のままであること、MainWindowで抑止中の見出しクリックとツリー選択の後に表示と一覧が一致することを確認する。

#### 起動時の初期選択の設定がその場でツリー選択を変える

詳細設定の「起動時にインストール保留を選択」を変えると、設定画面の裏でメイン画面のツリー選択と一覧が「ライブラリ」のルートまたは保留へ移り、作業中の表・フォルダ・履歴の選択が失われる。[MainWindow.xaml](../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) のライブラリのルート（1172行付近）と保留（1293行付近）が `IsSelected` を `ViewSettings.StartupSelectInstallPending` へ `Mode=OneWay` で常時結んでおり、一度も選ばれていない側のBindingが即時保存された値を反映して通常の選択として一覧を切り替える。v3.1.0.0からあるが、3.2.0.0の即時保存で起こりやすい。

この設定は次回起動時の初期選択だけに使う。2か所のBindingを削除し、起動時の初期選択は既存の `ApplyStartupInitialSelectionRequest` だけで行い、初期一覧と同じ起動時の設定値を使う。画面側の利用者がなくなる変更通知の転送の要否も見直す。[設定](../spec/runtime/settings.md)の詳細設定の節へ「変更時点の選択は変えない」を明記する。検証は、別の項目を選んだ状態から設定を両方向へ変えてもツリーの選択と一覧の表示対象が変わらないことを、WPFテストまたはデスクトップE2Eで確認する。

#### 譜面情報の補完の件数表示

ステータスバーの「譜面情報の補完」行が件数と対象パスを出さず不定ゲージだけになる。モデルは [CatalogChartInfoOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogChartInfoOwner.cs) で `ChartInfoBackfillProgressSnapshot` を通知しているが、ViewModelに購読がなく、[OperationProgressHubViewModel](../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) の `HasDedicatedPresentation` が前提とする件数行 `task:chart_info_backfill` を作る処理がない。v3.1.0.0は件数とパスを表示しており、起動と設定適用の統合で購読ごと外れた退行である。[ステータスバーの進捗](../spec/runtime/statusbar-progress.md)は補完行に件数とパスを要求している。[更新後初回の全件再解析](#更新後初回の譜面情報の全件再解析)では残り量を知る唯一の手掛かりになるため、そちらで待たされる旨を表示する案を採るなら同じ単位で扱う。

方針は、MainWindowViewModelでスナップショットを購読し、要求が起動由来なら「起動に伴う追加処理」の子、それ以外は独立行として、読込みの件数行と同じ形の件数行を作り、汎用行は既存の統合で一行にすることである。検証は、ステータスバーのWPFテストで汎用の実行通知と件数スナップショットを順に流し、一行だけで件数とパスを表示し完了で消えることを、起動由来と導入由来の両方で確認する。

#### ステータスバー仕様の段階数と子行の所属

[ステータスバーの進捗](../spec/runtime/statusbar-progress.md)の親行の段階数（初期化13、スコアの再読込み4、全再初期化14）と表示例、「順位の更新」「メンテナンス情報の読込み」「導入可能譜面のメンテナンス情報更新」を親の子とする記述がv3.1.0.0時点のまま残っている。現行の [StartupProgressWorkflowOwner](../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs) は任意処理を親の予定へ入れない[進捗](../spec/runtime/progress.md)の規則どおりで、初期化10、全再初期化9、スコアの再読込み3である。文書だけの修正で、仕様を正本に実装を直すと意図して外した処理を親へ戻す誤修正を招く。段階数・表示例・子行の所属・ハッシュ段階の記述を[進捗](../spec/runtime/progress.md)に揃え、固定数を書かず予定表を参照する形にする。

#### 導入しなかった操作でもツリー選択が移る

保留のパッケージを選び、導入先が空の「インストール」や、確認に「いいえ」と答えた「新規としてインストール」を実行すると、何も導入されないのにツリー選択が隣のパッケージへ移り、一覧の選択も消える。[MainWindow](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の一覧・操作バーとツリーの計4入口が実行前に `CaptureNextSiblingOrRoot` で移動先を決め、[MainWindowFeatureTerminals](../../BeMusicSeeker/Views/MainWindow/MainWindowFeatureTerminals.cs) が `ShouldApplyView` だけで適用する。導入先が空の正常スキップも、承認0件の新規導入も `CompletedFor` になる。資源を持つ入れ子の拒否、移動対象0件の資源のみ導入、物理失敗も同じである。v3.1.0.0からあるが、選択があれば常に有効な操作バーの「インストール」で到達しやすくなった。

選択計画の適用を、対象パッケージが保留から外れた場合に限る。4入口を揃え、保留から除去する入口も同じ形か確認する。導入先なしの無言スキップへ案内を足すかは、複数選択で一部だけ導入先なしの場合の文言とともに別に判断する。[導入先推定](../spec/library/install-estimation.md)の新規導入の確認へ拒否・スキップ時の選択保持を追記する。検証は、保留2件で上記の操作の後にツリーと一覧の選択が変わらないことと、導入成功時に次の兄弟へ移ることを対で確認する。

#### 表の削除・改名の後の出力基点のfolder.date

通常・追加通常の表を削除した直後や、出力名が表名に追従する表を改名した直後は、出力基点の `folder` 行の `date` が実際の更新時刻とずれたまま残る。[PlaylistCustomFolderOutputMaintenanceOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs) の `TryRemoveCustomFolder`、`CompletePreparedMigrationCleanup`、`CleanupReplacedCustomFolderOutputs` は表ディレクトリを消した後に同期項目のない `SyncPrunedRows` を行い、[Lr2FolderFileDbSyncService](../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderFileDbSyncService.cs) は祖先の行を同期項目からしか作らない。改名は新先の行を書いた後に旧ディレクトリを消すため、秒の境界をまたいだときだけずれる。次回起動の必要出力の判定で揃うが、それまでにLR2を起動すると更新処理の対象になり、次回起動で基点配下の全表の確認が一回余分に走る。設定画面からの再生成の経路で成立するかは未確認である。

3経路が共有する `SyncPrunedRows` で、削除した表ディレクトリを含む通常・追加通常の基点までの祖先を、整理と同じ一回の同期で時刻の更新対象に含める。配下に出力を持つ表が残らない基点の行の扱いは[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)の既存の記述に揃え、削除・移行の終端での追従を同じ仕様へ追記する。ROOT型は基点の行を持たないため対象外である。検証は、カスタムフォルダ出力の実DB・実ファイルの系列で、同じ基点の一表を削除した直後と改名した直後に基点の `date` が実更新時刻と一致し、続く必要出力の判定で再確認対象が0件であることを確認する。

#### 譜面フォルダの削除・移動後の中間親フォルダのfolder.date

`R\Cat\SongA` のような構成で `SongA` を削除または別の場所へ移動すると、兄弟の曲が残る中間の親 `R\Cat` の `folder` 行の `date` が更新されない。[BMSLibrary.Lr2SynchronizationOwner](../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs) の `SyncLr2NormalFoldersForCatalogMutation` は時刻を取り直す対象を変更後の譜面パスの祖先などから作り、[Lr2NormalFolderSyncScopeBuilder](../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2NormalFolderSyncScopeBuilder.cs) の `CreateForCatalogMutation` は削除・移動元の親を加えない。v3.1.0.0からあり、次回起動の走査で揃う。

削除・移動元の直接の親のうち、存続し登録ルート内にあるものを時刻の更新対象へ加える。既存の読取り面を使うため全件走査は増えない。この集合は[確定後にBusy失敗になる件](#通常出力先を走査ルートに含むlr2連携で確定後にbusy失敗になる)のP判定の集合と同じ変数から作るため、同時に着手する場合は定義を揃える。資源のみ導入でディレクトリが変わり局所同期が呼ばれない場合を同じ契約で扱うか、仕様で割り切るかも決める。検証は、削除と移動の既存のLR2同期テストに、兄弟が残る親の `date` が実更新時刻と一致することを加える。

#### 導入後のNEW SONGフォルダの行

直近の新曲がなく `folder` 表に `newsong.lr2folder`（type 3）の行がない状態から導入しても、同じ稼働中は行が作られず、LR2で新曲フォルダが出ない。新曲フォルダの判定 `CreateCurrentLr2BuiltinCustomFolderSettings`（[BMSLibrary.Lr2SynchronizationOwner](../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs)）は起動・差分走査と全体同期・設定変更からだけ呼ばれ、導入後の局所同期は組込みフォルダを扱わない。v3.1.0.0からあり、再起動か差分読込みで回復する。LR2が行の有無だけで表示を決めることは実機未確認である。

局所同期の確定後、今回追加した行の `adddate` が `titleflash` の範囲内で既存のtype 3行がない場合だけ、組込みの行を一件作る。全譜面を走査せず追加行だけで判定する。期限切れの行の撤去は起動時の判定に任せると[LR2楽曲DB](../spec/integration/lr2-song-db.md)で割り切る。検証は、type 3行のないDBへ導入した後に行ができることを確認する。

#### 自動整列の表でフォルダを削除すると自動が外れる

フォルダ順が自動の表で、譜面を含むフォルダを削除するか名前なしへ改名すると、[BMSTable.EntryCommit](../../BeMusicSeeker/Models/Playlist/BMSTable.EntryCommit.cs) の `PrepareFolderRename`（390行付近）が明示順へ空文字を挿入し、[PlaylistPropertySaveService](../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs) が明示順の有無で自動を判定するため手動整列として読まれる。次にプロパティや概要のセルで別の項目だけを保存すると、表示順が明示順として固定され、以後のフォルダは末尾へ並ぶ。BMT出力の `folderOrder` も同じ明示順を使う。v3.1.0.0からある。

案は、明示順が空の表では空文字を挿入しない形と、フォルダ欄を編集していない保存では `FolderOrder` を変更対象にしない形である。後者は別項目の保存で明示順が書き換わる他の場合も止められる。既に空文字だけを保存している表の扱いも決め、[保存と出力](../spec/playlist/storage-and-export.md)の名前なしの順序の記述を補う。検証は、フォルダ削除の後に表名だけを保存しても明示順と自動が保たれることを確認する。

#### 内蔵の難度推定表と推薦表の更新日時が表示言語で進む

表示言語を変えた後の最初の再読込みで、内容が同じ内蔵の難度推定表と推薦表の `last_update` が進み「更新」と判定され、BMTが再生成される。[PlaylistRecommendedTableOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistRecommendedTableOwner.cs) が表示言語のリソースから作る `org_name` を生成ヘッダーハッシュ `header_sha256` に含め、名称差が既知のハッシュ差として扱われる。[保存と出力](../spec/playlist/storage-and-export.md)と[ローカルのリコメンド](../spec/playlist/local-recommendations.md)は名称差だけでは `last_update` を進めないと定めている。v3.1.0.0からある。

生成ヘッダーハッシュから表示言語に依存する値を除き、推定の種類や推薦の方針識別子と★の数値などの言語に依存しない識別を入れる。名称の保存・公開・出力は既存の名称差の処理で維持する。保存済みハッシュとは一度だけ食い違うが、その一回の日時の前進は許容する。検証は、ja-JPで取り込んだ推定表をen-USで再読込みしても更新扱いにならず、`org_name` は英語へ保存されること、推薦表は★の変化でだけ更新されることを確認する。

#### 正規表現の中の縦線

`title:re:^(alpha|beta)$` のように正規表現の中で `|` を使うと、[GridKeywordSearchQuery](../../BeMusicSeeker/ViewModels/Search/GridKeywordSearchQuery.cs) が正規表現かどうかに関係なく `SplitUnquoted` で `|` ごとに分割し（278行付近）、断片が無効な式になって0件になるか、別の意味の式が残る。引用した `re:"..."` は分割されない。v3.1.0.0からある。案は、[検索構文ガイド](../../docs/keyword-search-syntax-guide.ja.md)の正規表現の節に引用が必要なことを例とともに書く形と、正規表現の条件では本体全体を一つの式にする形である。後者は一部の断片だけが有効な現行の式を全体無効に変える互換性変更で、保存式の同一性判定と候補の構造判定も同じ構文処理を共有するため揃える必要がある。後者では `re:alpha|beta` の結果が変わらないことも確認する。

#### メニュー項目の名前の最初の下線

文字列から作るメニュー項目で、名前の最初の `_` が表示から消える。[Simple Styles.xaml](../../BeMusicSeeker/Themes/Simple%20Styles.xaml) のメニュー項目の `HeaderHost`（569行付近）が `RecognizesAccessKey="True"` で、文字列のHeaderの最初の `_` をアクセスキーとして扱う。対象は、カスタム右クリック操作、関連文書のファイル名、移動先、重複フォルダの統合先の4か所である。実行対象は正しいが、移動先と統合先は破壊的操作の宛先確認に使う表示である。v3.1.0.0からある。既定スタイルは変えず、利用者由来の文字列をHeaderにする4か所だけをTextBlockにするか `_` を二重にする。検証は、`_` を含む操作名で生成した項目の表示文字列を確認する。

#### 最大化から最小化して終了したときの次回の表示

最大化から最小化した状態で終了すると、次回起動で通常サイズになる。[MainWindow](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) は終了時に `GetWindowPlacement`（1589行付近）の結果を保存し、起動時に `Flags` を0にして最小化を一律に通常へ置き換えるため、「元に戻すと最大化」の情報を捨てる。v3.1.0.0からある。保存値が最小化で `WPF_RESTORETOMAXIMIZED` を持つなら最大化、それ以外の最小化は通常にする。外部プレイヤーのウィンドウ復元は別仕様のため変えない。検証は、復元の変換を純粋関数として切り出し、四つの組合せを確認する。

#### 表単体の再読込みの完了時の選択

表を右クリックして再読込みし、取得中に別の項目を選んでも、完了時に [MainWindow](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の `RestorePlaylistTableSelectionAfterReload` がその表を選択・展開し直し、一覧とキーボードフォーカスを奪う。受付が拒否された場合も同じである。ViewModelの詳細選択は選択が変わっていれば追従せず、ビュー側の強制選択だけが食い違う。v3.1.0.0からある。開始時の選択と詳細選択の版を捕捉し、完了時に選択が対象の表のまま、または置換で失われて他が選ばれていない場合だけ選び直す。受付拒否では動かさない。プロパティ保存後の選択復元も同じ条件で揃えるか確認する。検証は、取得を保留できる偽の取得で、途中で選び直した場合と選び直さない場合を対にする。

#### 譜面ビューアで開く操作の無通知

未所持のプレイリスト項目を複数選択して「譜面ビューアで開く」を押すと、何も開かず通知もない。未所持用メニューは右クリックした一行だけで可否を判定して単数の文言のまま有効になり、[ScoreViewerRegistration](../../BeMusicSeeker/ViewModels/ChartOperations/ScoreViewerRegistration.cs) の `RunAsync` は2件以上ではビューアを開かず、パスのない行だけでは送信候補も失敗もないため何も表示しない。内蔵表の「ページを開く」と、全件登録済みの「譜面ビューアに登録」の無通知も同じ系統である。v3.1.0.0からある。案は、未所持用メニューも選択件数で文言と可否を決める通常メニューの規則に揃える形と、パスのない対象だけのときは右クリック行のビューアを開く形である。後者は `RunAsync` の契約変更となり、所持行で何も起きない結果を通知するかも同時に決める。検証は、パスのない2件の現状を表すケースを置いて採用した期待値へ変え、未所持2件選択時のメニューの文言と有効状態を確認する。

#### 外部エンコーダーがない音声変換

設定した形式の外部エンコーダーが見つからないと、[SelectedChartAudioConversionWorkflowOwner](../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs) がファイルごとに（806行付近）無通知でWAVEへ代替し、共有設定の形式もWAVEへ書き換える。進捗と完了の表示は選んだ形式のままである。エンコーダーのフォルダを変えたときに形式の可用性を再確認しない。代替は[音声変換](../spec/runtime/audio-conversion.md)どおりだが通知は定めておらず、設定画面が保存値を表示し続けるよう変わったため気付く手掛かりが減った。変換の実行前に一回だけ可用性を確認し、欠損なら警告して中止するか「WAVEで続行」を確認する。ファイルごとの代替と設定の書換えを事前確認へ置き換えて削除するか、続行時も設定を書き換えないかを決め、仕様と検証表を改訂する。検証は、利用不能な形式のスナップショットで変換を要求し、警告が一回届くこと、中止時に変換が呼ばれず設定が変わらないこと、続行時の拡張子と完了表示が一致することを確認する。

#### 追加出力先の複数追加の途中で拒否されたとき

プレイリストページの追加出力先の「追加」で複数のフォルダを選び、途中の1件が同じフォルダ名や検索ルートとの重なりで拒否されると、それより前に選んだ有効なフォルダも無言で外れ、後ろのものだけが保存される。[SettingsDialogViewModel](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) の `AddCustomFolderAdditionalOutputBaseDir` が1件の失敗で `RestoreUnacceptedOutputCandidates` を呼び、出力候補全体を公開済みの配置へ戻す。v3.1.0.0は拒否したものだけを除いており退行である。選択したパスをまとめて検証し、拒否の理由を一回にまとめて表示して有効なものだけを加える。単独の追加の失敗でも、その操作で加えた分だけを戻す。BMSディレクトリの複数追加も同じ契約に揃えるか確認する。検証は、有効・名前重複・有効の3件で有効な2件が保存され、通知が一回であることを確認する。

#### 追加出力先の追加・改名後の選択

追加出力先を追加または改名すると、確定直後に一覧の選択と改名欄が先頭の追加先へ移る。[SettingsDialogViewModel](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) の `RefreshCustomFolderAdditionalOutputBaseDirsFromSettings` が確定後の再同期で選択を常に先頭へ置き換える。続けて名前を打つと先頭の追加先を改名しうる。v3.1.0.0は操作した項目を選択したままで退行である。現在の選択（改名後の新パスを含む）が新しい一覧にあれば維持し、ない場合だけ先頭へ移す。BMSディレクトリ一覧の再同期も同じ形なら揃える。検証は、2件目を改名した後の選択と改名欄が改名後の項目を指すことを確認する。

#### BMTキャッシュから復元した表の出力フォルダの既定

プレイリストページの「新規プレイリストの出力フォルダ」の既定（`PlaylistDefaultIgnoreFolderOutput`）を変えても、beatorajaの表URL取込みで取得に失敗しBMTキャッシュから復元した表には適用されず、固定の既定でLR2フォルダが出力される。[BeatorajaBmtTableImportService](../../BeMusicSeeker/Models/Playlist/BeatorajaBmtTableImportService.cs) が `new BMSTable()`（115行付近）のコンストラクター既定のまま返し、[PlaylistExternalSyncOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs) の `RegistrateExternalTablesAsync` は出力除外を設定しない。設定値を使うのは取得成功時の新規表と新規作成の表だけである。既定を変えた利用者だけに影響し、プロパティで直せる。v3.1.0.0からある。[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)は新しい通常表に設定値を使うと定めている。新規表の出力除外を設定から決める処理を登録側へ寄せ、取得成功とBMT復元の両経路で同じ正規化を使い、キャッシュの変換自体は設定を知らないまま保つ。[表URL取込み](../spec/playlist/table-url-import.md)のBMT復元の節へ設定の既定に従うことを追記する。検証は、既定以外の設定のLR2連携でBMT復元した表の `ignore_folder_output` と生成された `.lr2folder` の集合を確認する。
