# 残存ビルド警告の解消

状態: 完了（2026-09-13）

## 実施内容

ユーザー依頼に基づき、`26d6fef4` の残存ビルド警告683件をゼロへ整理した。開始時の作業ツリーはclean。修正・検証・コミットは承認済みであり、push・公開・version更新は対象外。

| 作業 | 状態 | 結果・反映先 |
| --- | --- | --- |
| テストのnullable整理 | 完了 | 105ファイルのnull許容型と解析情報を整合し、679警告を解消。既存assertion・入力・失敗検出を維持し、既存test method属性2,268件を保持。 |
| 旧通信設定とHTML解析 | 完了 | SgmlReaderのページ二重取得を除去し、取得済み本文のみを解析。不要になったServicePointManager設定を除去し、WPF重複分を含む4警告を解消。[外部同期と更新検知](../spec/playlist-data-and-export-flow.md#外部同期と更新検知)へ恒久契約と対応テストを反映。 |
| LR2終了処理テストの同期 | 完了 | 既存UI通知の集約で短い中間stageを取り逃す競合を解消。fixture所有gateで同じ終了要求条件を確実に作り、worker回収と設定復元を保証。 |
| 検証・独立レビュー | 完了 | 関連Quick、最終Functional、format、analyzer、差分検査に成功。凍結した全112ファイルの独立静的レビューで修正必須の指摘なし。 |

`NoWarn`、警告pragma、nullableの一括無効化、既定値へのフォールバック、不正入力除外は追加していない。`!`は既存assertionやfixture生成などで確立する保証を解析へ伝える箇所に限定した。nullable注釈と既存helper移動は判定内容を変えない機械的修正として、追加のTest Contract Packetは不要と判断した。

## 回帰テストの設計と確認

独立設計担当のPhase A（確定要件からの判定基準）とPhase B（実入口・既存fixtureの調査）を経て、外部表HTMLの単一取得・参照URL解決のTest Contract Packetをrootが承認した。

- 仕様の根拠: URL読込み・外部同期から`LoadExternalTableAsync`へ到達し、取得済みHTMLだけから表を解決するという確定判断。
- 入力と前提: 正常HTTPページ、相対header/data、取消し・redirect・認証・retryなし。ページPは`/pages/start.html`、header参照は`../headers/main.json`、data参照は`../data/rows.json`。外部DTDはcase所有loopbackを指す。
- 必須結果: 初回HTMLが指すTable Aとその譜面・URIを返し、P/H/Dは各1回、parserの再取得・DTD取得・その他通信は0回。通信の全順序、内部構造、時間は固定しない。
- 誤実装の識別: parserの迂回取得へ別表Bの正常HTMLを返す実socket serverを用意。contentをnameより前に置くmetaでregex fallbackだけの成功も避ける。既存`AppHttpClientTests`のserverを共有helperへ移し、旧コピーを退役した。
- 配置: 既存`BmsPlaylistExternalLoadTests`へ`LoadExternalTableAsync_UsesFetchedHtmlWithoutParserNetworkRequests`を1件追加。実ownerと既存HttpClient注入口を使い、production APIやprivate直接呼出しは追加しない。
- 資源: case所有handler/client/listener/task、動的port。既存helperの完了待ち・5秒cleanup watchdogを使用し、固定sleep、DB、WPF、共有設定の変更は追加しない。
- 識別力: 修正前は期待Table Aに対してTable Bを返し609msで失敗、修正後は13msで成功。compile/setup failureはredの根拠にしていない。

## 検証結果と残課題

- 通信関連Quick: 49成功・3環境スキップ。アプリとテストの再コンパイルで警告0。
- 設定・LR2・resource・性能corpus契約・process lifecycleのQuick: 207成功。
- LR2通知・終了処理の最終Quick: 3成功。
- 最終Functional: 4,769件中4,758成功・11環境スキップ・失敗0。6hostすべて成功、240.1秒で300秒上限内（180秒の報告目標は超過）。format・analyzer診断0、build警告0・エラー0。
- 初回Functionalは既存LR2 testの通知競合で失敗。input_surfaceは1ms、同期全体は131msで正常完了しており、UI通知の購読時点で中間stageを逸失していた。全体300秒timeoutではない。fixture同期を修正し、上記の最終検証で成功した。入力、DB期待値、終了要求条件、WPF経路、期限、並列度は維持した。
- 11スキップは別ボリュームの書込み先、シンボリックリンク作成権限、publish済みupdater入力の環境条件による。実Internet/TLS、大規模性能、配布・更新のFull検証は今回の確認範囲外。

作業範囲内の残課題はない。配布・更新の契約は変更していないためFullは実行していない。
