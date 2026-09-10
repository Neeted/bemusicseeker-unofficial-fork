# Install / delete resource-index P0 実装記録

## Scope / authority

基準は `b1fe3b83fa48fc02a4c1525f8a73bddc5a742ca0` に利用者提供の性能要件整理patchを適用したtree。依頼で承認された「テスト方針改訂版」のP0-A/B/Cを実装する。最優先の目的は処理速度だが、今回は利用者指定によりコード・テストの実装と机上確認まで。build、test、red / mutant、性能測定、アプリ起動は実行しない。本番確認・大規模benchmarkは任意の別作業であり本差分の前提ではない。

multi-agent機能は利用できないため、計画・oracle定義・実装・机上レビューを順に分離する。独立agentによるreviewを実施したとは扱わない。追加テストのauthorityは利用者が承認した処理契約とFSDB-FACTS / 世代分離であり、現行の出力や実装文字列はoracleにしない。

## Test Contract Packet: P0-RI

分類は不要な全逆引きコピーの退行修正と、成功済み削除の一括反映。既存テストはentry/候補・世代分離・例外時非公開を検証するが、不要な仕事の不在と削除結果の一括公開は不足するため、近傍へ小規模Functionalテストを追加する。時間閾値、GC・割当量、巨大fixtureは追加しない。

Production ingress: 保留の通常/強制/推定先install -> package install serviceの確定callback -> `LibraryResourceIndexOwner.AddScanDirectories` / `AddDirectories` -> directory cache。ライブラリ削除 -> `ExecuteLibraryChartRemovalAfterAdmission` -> `ExecuteLibraryChartRemovalPlan` -> 成功したDeletedFolderPaths -> resource owner。resource候補は後続の導入先推定・リソース判定へ使われる。競合するwriterは既存の操作予約とowner gateで直列化され、読取側は旧snapshotを保持できる。

| Contract ID | Outcome / invariant | Allowed variation | 落とす誤実装 / evidence |
|---|---|---|---|
| RI-A | 空リソースentryは追加するが逆引きは書かない。同値6カテゴリはno-op。最終candidate列が順序を含めて同一のbucketは書かない | 内部型、局所ノード配置、実時間 | entry追加まで省く、空カテゴリをコピーする。実際のbucket書込観測とentry/世代をassert |
| RI-C | native初期辞書を更新ごと/初回mutationに列挙・全コピーしない。実変更の最新値をkey単位で保持し旧世代は不変 | 変更keyに至るノード共有/コピー、内部表現 | 全baseコピー、世代chain、前世代への書込。列挙禁止のread-only sourceを実mapに渡し、結果/旧世代を確認 |
| RI-V | full/lazy、空候補と未cached、hash=0、大小文字、候補順序とSelfOwnedを維持 | 診断用の内部型、時間 | lazyの未cachedを空と扱う、同hashの他候補を消す、SelfOwned変更を省く。小さな独立期待値 |
| RI-B | 削除成功リストだけを一mutationで反映。変更ありは一世代、全no-opは公開なし。重複/親子で二重計上しない | 同じ結果を作る内部探索 | 予定対象/失敗分も消す、一フォルダ一公開。成功/失敗を含む小さな削除と世代を確認 |
| RI-F | input列挙失敗は新snapshot非公開、先行FS成功をrollback済みとしない | 例外の内部発生場所 | 一部だけpublish、例外をno-op化。失敗注入と保持snapshot |
| RI-I | リソース同梱packageの逐次確定、後続からの候補参照、失敗時の成功prefixを保持。ManualRecoveryRequired / DurableFinalizationFailedのsuffix停止と回復済み失敗の継続を区別する | コピー方式 | 全packageを最後に公開、停止対象の失敗後にsuffixを実行。既存serviceテストに実resource ownerの結合を補う（追加ingressは推定先/強制） |

Coverage placement: `DirectoryResourceLookupCacheTests` (RI-A/V)、`LibraryResourceIndexOwnerTests` (RI-B/F)、`ResourceReverseLookupMapTests` (RI-C、新しい内部格納部品の列挙/共有契約)、`BmsLibraryPackageInstallServiceTests` / `OwnedChartCollectionLibraryMutationTests` (RI-I/B)。既存のlazy競合、durable finalization、manual recovery、部分選択保護のassertionは維持する。seamはinstance-localの実書込/entryコピー通知とread-only初期辞書に限定し、source-text assertionを作らない。完了signalは同期methodのreturn、既存競合テストの同期点。一時ファイルはfixture所有の固有root内、実ユーザーのDB/ドライブ/ごみ箱・ネットワークは不要。

## 設計

P0-A/C: 初期逆引き辞書を所有権移転された不変baseとして共有し、最新の変更keyだけを構造共有するmapに保持する。forkはbaseを列挙せず、差分の同じkeyを上書きする。lookupは差分 -> baseの固定二層で、前世代へのchainを持たない。コピー単位は変更keyへのtree経路と置換candidate配列。初期native辞書の全変換をstartup/初回installへ移さない。最終candidate列が順序を含めて同一のbucketは差分を書かない。参照集合が同じカテゴリでも、SelfOwnedのみのentry変更等に伴う従来互換のremove-then-addで候補順序が変わる場合は書き換える。

差分には初期構築以後に変更/追加/negative-cache化したkeyの最新値を保持する。baseに残る置換前のpayloadは初期baseの規模内で保持する（速度を優先して再構築/自動compactしない）。旧世代数に比例したlookupや履歴台帳は作らない。初期baseは明示scan/replacementまたは既存の全逆引きinvalidateで置き換わる。差分treeへの検索と更新の定数項、長期変更後の速度・メモリは未測定。ownerのmutation公開前とlazy結果の格納完了時に計算済み変更nodeをfreezeし、その処理を次回install/deleteへ持ち越さない。

P0-B: 既存の物理削除結果を取得した同じ場所で、成功フォルダ群を一度ownerへ渡す。gate/未公開cache/一括publishを再利用し、FS/DB/通知/停止/復旧の境界は変更しない。entry根は既存COWを維持し一batch高々1回。部分木探索の全directory列挙とmoveの全逆引きrewriteは既存のまま（今回新たな索引最適化を混ぜない）。

規模: C約21万、D約3万、K約800万を設計上認識するがfixtureの義務にしない。変更前のP×Kカテゴリコピーを、差分keyへの更新へ置換する。entry側のP×Dコピー等の既存残件、LR2全件snapshot、inline情報、起動、描画、並列度は対象外。

## Verification

実施内容と差分の机上レビュー結果は最終節へ追記する。build/test/性能比較は今回未実行。後日の標準入口は `verify-refactor.ps1 -Mode Quick` の近傍filter、続いて通常の `Functional`。本番環境と性能測定をこれらの合格条件へ追加しない。

## Test seam / coverage 補足

`LibraryResourceIndexTestSupport` は既存fixtureのowner観測方式に合わせてreadonly owner fieldをreflectionで取得する。目的は実 `BMSLibrary` commandの前後のsnapshotと世代の観測、および初期scan setupだけ。private commandを呼び出さず、writer guardを迂回したmutationをassert対象にしない。利用者が承認した世代/一括化契約を実ingressで確認するための例外で、適切なsnapshot診断surfaceが得られたら退役する。

同梱resourceはscan/存在確認/転送に必要な小さなbytesのみ。BMS parserへ渡すchartは有効なheader/referenceを持ち、audio/video decoderへ不正mediaを渡すテストではない。テストは固有rootを所有し、Everything非依存の既存test compositionと同期command完了を使う。旧snapshotのlazy生成とwriter競合は既存テストを維持し、今回新しいsleepやプロセス共有hookを追加しない。

## 机上レビュー（今回の提出範囲）

- 初期native mapは既存のownership transfer後にそのまま保持。初回fork/変更で全baseのenumeration/materializationなし。差分builderのforkはimmutable nodeを共有し、過去のmap/owner/snapshotへのchainなし。
- reverse書込は `Set` が正味変更を行った後だけobserverへ通知。entry根copyも実際のDictionaryコピー後だけ通知。期待counterを返す偽更新アルゴリズムなし。
- category更新は対象entryのhash集合だけを読み、従来のremove -> addの候補順序とtransition counterを維持する。最終値が同一なら差分treeへ書かない。empty resultはbaseへのfallbackを隠す有効なcached key。
- 入力列挙が途中で失敗した場合は次cacheが未公開のまま例外伝播。FS結果のrollback/成功への変換は追加していない。
- delete production callは計画ではなく確認済み `execution.DeletedFolderPaths` をbatchへ渡す。install codeとDB/receipt/finalizer境界は未変更。旧cacheのlazy builderと新cacheのbuilderは別instance。
- entry根の全copy、削除部分木検索、move全key rewrite、LR2全件snapshot等は今回の残存/対象外として明示。全アプリの退行解消やv2以上の実速度は宣言しない。
- build、Quick、Functional、red / negative-control、速度測定、独立agent reviewは未実施。後日テスト実行用の実装として提出し、機能テスト合格済みとは報告しない。

後日実行する近傍filter（今回未実行）:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ResourceReverseLookupMapTests|FullyQualifiedName~DirectoryResourceLookupCacheTests|FullyQualifiedName~LibraryResourceIndexOwnerTests|FullyQualifiedName~OwnedChartCollectionLibraryMutationTests|FullyQualifiedName~BmsLibraryPackageInstallServiceTests'
```

その後の通常Functionalと、任意の実速度評価/本番運用確認は別扱いとする。今回の未実行は利用者指定によるものであり、テスト失敗や成功の結果ではない。


## 2026-09-10 非blockingレビュー指摘への追補

基準はbundleの `d6eed9e974d1289a6b73abb5f07d4bf88cbffc53` に `BeMusicSeeker-P0-implementation.patch` を適用したtree（`bba46a186f759ccc5c39a6dcc582dc2d3a1cc439`）。利用者の「非blockingの指摘について、推奨される修正を行いpatchにする」という依頼をauthorityとする。P0適用後のFunctional合格は利用者報告であり、今回の追補テストの合格を意味しない。

分類・必要性判断: 本体の挙動修正ではなく、説明の限定と既存契約のcoverage補強（既存更新＋近傍への追加）。単一候補の無書込だけでは複数候補の順序変更を表せず、batch return後の世代数だけでは公開タイミングを区別できない。失敗時のFS/DB/pending/installedのassertionもresource候補の成功prefixを直接は保証しないため、この不足だけを補う。productionの変更はコメントのみで、候補順序・処理量・公開・DB/receipt/finalizer・通常失敗後の継続は変更しない。

### Test Contract Packet: P0-RI-REVIEW（追補編集前に固定）

root decisionは依頼された2件の修正を上記P0-RIとFSDB-FACTSの範囲で行うこと。RI-A/V-SELFの正確な順序は、前レビューで本体を変えず維持するとしたremove-then-append互換契約のcharacterizationであり、一般的な最適順序の証明ではない。順序仕様を意図して変更するときに見直す。current outputや新実装から期待値を導出しない。

| Contract ID | 固定したoutcome / allowed variation | 既存coverageへの配置・落とす誤実装 |
|---|---|---|
| RI-A/V-SELF | 3カテゴリの候補が `[A, B]` のとき、SelfOwnedだけを変えてAを置換すると `[B, A]` を書く。Bを置換すると `[A, B]` のまま無書込。どちらもentry変更と旧cache不変を保持。内部表現・カテゴリ間の書込順は固定しない | `DirectoryResourceLookupCacheTests` に先頭/末尾の2行を追加。SelfOwned変更の無条件skip、末尾でも無条件write、旧cacheへの変更を検出するassertion |
| RI-I-TIMING | 推定先/強制の2 package導入で、2件目のsource copy時点に1件目の候補を公開済みとする。その時点の候補列と保持したsnapshotを別々に観測し、全体完了後も初期/中間snapshotは不変。保存先名・内部コピー方式は固定しない | 既存 `PendingResourcePackages_InstallThroughLibraryAndPreserveEarlierResourceSnapshot` の両行を拡張。末尾で2回publishして世代数だけ合わせる誤実装を、中間世代とその時点の候補列で区別 |
| RI-I-PREFIX | 2件目のManualRecoveryRequired後、3カテゴリとも1件目だけの候補を保持。失敗分・未実行分は非混入、旧snapshotは不変。失敗packageの物理残存物の消失は要求しない | 既存の推定先と強制の `AppliesDurablePrefixBeforeManualRecovery...` を拡張。package別の異なるkeyで、成功先と同じdestinationを使う推定先suffixの混入も識別。既存のreceipt/FS/DB/collection assertionは維持 |

Production route / assumption: 保留の推定先/強制commandから既存package service、`FileDbMutationBoundary` のstage/promote/durable callback、library state callback、resource ownerへ到達する単一batch。既存のwriter所有を維持し、外部同時変更を注入しない。成功分の候補は後続lookup/推定が利用するため、private callabilityだけを根拠にした状態ではない。

Coverage / safety: canonical fixtureは移動・新設せず上記2クラスをextendする。`CreateBmsFile`、実FS fake、既存のDB abortとpath限定delete失敗、`LibraryResourceIndexTestSupport` を再利用する。source copyのobserverは既存fakeのinstance-local・未設定時無作用であり、callback内ではsnapshotと候補列の捕捉だけを行い、assertionをexecutor外に置く。reflection例外は従来通り初期setup/観測だけで、診断surface導入時に退役する。GUID一時root/SQLiteと既存schedulerを使い、完了signalは同期commandのreturn。既存Functionalの `remaining` / `remaining-bms-library` に属し、sleep、task、static hook、DNP、実Everything、新しいproduction seamを追加しない。退役test/routeはなし。

検証方針: 誤実装に対する各assertionの識別力を机上で確認する。利用者指定によりbuild、Quick、Functional、red / runtime mutant、性能測定は実行しない。これは既知の本体bugの修正ではなく、旧実装をredにすることを目的にしない。multi-agent機能は利用できないため、計画/authorityとoracle固定、repository-fit、実装、凍結差分の再読を順に実施する。独立agent reviewを実施済みとは扱わない。

追補の静的確認: `git diff --check`、UTF-8/LF、productionのコメント以外の行が同一であることを確認した。bundle基準から元P0 patchと追補patchを隔離ディレクトリへ順に適用し、全12対象ファイルが作業結果とbyte単位で一致すること、および追補の逆適用でP0適用後へ戻ることを確認した。C#のcompile/type-checkやテスト実行の代替とは扱わない。

追補後に実行する近傍filter（今回未実行）:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~DirectoryResourceLookupCacheTests|FullyQualifiedName~BmsLibraryPackageInstallServiceTests'
```

その後の通常Functionalと任意の実速度評価/本番運用確認は分離したままとする。追補patchは元P0 patch適用後への差分として提出する。
