# R2b PathCleanup のDB処理量確認

## 対象・条件

- 対象: mergeから到達する`CatalogMutationOwner.ApplyCatalogMutation`の実gateway処理。PathCleanupの対象限定化であり、merge全体の速度測定ではない。
- 基準版: `474fbf6b81fe77153fec8d4d8ef813f2786b4b74`。候補は同版にR2b差分を適用した作業ツリー。
- 実行環境: Windows 11 Pro `10.0.22635`、AMD Ryzen 5 2600（12 logical processors）、物理メモリ34,288,893,952 bytes、.NET SDK `10.0.303`、Release / x64 / net10.0-windows。
- 入力: GUIDごとの一時SQLite。BMS背景16／128行、BMSON背景16／128行。対象はBMS 2行、BMSON 1行、maintenance-only 1行の固定4 path。BMSの一つは背景の別配置とMD5を共有する。DB、storage、ownedの初期membershipを一致させる。
- 既存path主キー、`hashidx`等を準備した後、実gatewayの書込み接続だけを観測する。setupと独立接続の検証readbackは計数外。native Everything、実ユーザーDB、UI操作、resource逆引き800万keyのfixtureは使用しない。

## 観測と結果

SQLiteの[trace event](https://www.sqlite.org/c3ref/c_trace.html)から結果行とstatement完了を取得し、完了時の[`FULLSCAN_STEP`](https://www.sqlite.org/c3ref/c_stmtstatus_counter.html)と[`VM_STEP`](https://www.sqlite.org/c3ref/c_stmtstatus_counter.html)を集計する。`VM_STEP`はprepared statementの仮想機械命令数で、FULLSCAN_STEPが数えないindex range traversalやROW callbackが0のINSERT SELECTも含むstatement仕事量のproxyである。返却行数はmanaged object生成へ渡る入力行の観測であり、allocationの測定値ではない。

| 条件 | catalog SQL返却行 | 全statementのFULLSCAN_STEP | 全statementのVM_STEP | PROFILEを得たstatement |
| --- | ---: | ---: | ---: | ---: |
| 背景BMS / BMSON各16行 | 0 | 188 | 1980 | 39 |
| 背景BMS / BMSON各128行 | 0 | 188 | 1980 | 39 |

全statementのscanとVMにはschema／temp集合の仕事も含む。188、1980という値やstatement総数を実装契約として固定せず、背景の増加に処理量が連動しないことを確認する。対象だけが除去され、DB / storage / ownedの残存exact集合が背景集合と一致し、共有digest保持・最後のownerのdigest除去・BMSON分離も同時に確認した。

実SQLを使う補助query planでは、hash収集が`SCAN d`と`song`のpath主キー検索になり、削除・digest確認も既存のpath/hash index検索になった。これは操作終了後の別接続に空temp表を再構成した結果であり、操作中のquery planそのものや大規模での同一planを保証しない。処理量の判定は実接続の返却行、FULLSCAN_STEP、VM_STEPを使う。

識別力確認では旧全表materializationを一時的に戻し、背景16行の入力で`song` 20行、`maintenance` 36行、`bmson_song` 17行、計73行を観測して対象集合の返却行上限により失敗した。復元後のfocused testは再成功。ログは`artifacts/verification/r2b-negative-legacy-materialization-20260912.log`。compile／setup failureを判定根拠にしていない。

さらに旧形のhash収集SQL（`song`を外側にした`INNER JOIN`）へ一時的に戻し、16／128背景の同一Δを実行した。catalog返却行は0、hash収集statementのFULLSCAN_STEPも0のまま、全statementのVM_STEPだけが2100から2996へ増加し、VM_STEP観測の識別力を確認した。旧形の補助planは`SEARCH s USING INDEX hashidx (hash>?)`で、hash索引の広い範囲を読む形だった。負の対照ログは`artifacts/verification/r2b-negative-hash-index-vm-20260912.log`で、SQLはpath temp外側固定へ復元した。復元後focused testのログは`artifacts/verification/r2b-focused-restored-vm-20260912.log`である。

## 検証範囲と制限

- 恒久検証: [`CatalogMutationOwnerTests`](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs)の`ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership`。接続単位の観測は[`SqliteStatementObservation`](../../BeMusicSeeker.Tests/Helpers/SqliteStatementObservation.cs)で、trace_v2 PROFILEからstatement別のROW、FULLSCAN_STEP、VM_STEPを記録する。既存exact identity／destination保護／transaction failure／file diff bulkのcoverageも維持する。
- 関連Quick: `FullyQualifiedName~CatalogMutationOwnerTests|FullyQualifiedName~BmsLibraryDuplicateServiceTests|FullyQualifiedName~BmsLibraryInitializationInlineChartInfoTests|FullyQualifiedName~BmsLibraryInitializationLoadTests`、103件成功（`tests-quick-20260912-192712`、test execution 14.2394秒、script 46.6秒）。
- Functional: `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional`成功。書式・Roslynator診断0件・buildを通過し、4,755件成功／11件スキップ。review補正前は239.5秒（`tests-functional-20260912-190455`）。共用bulk SQL変更を受けて最終snapshotで再実行し230.4秒（`tests-functional-20260912-193244`）。どちらも180秒目安超過・300秒以内であり、timeout再試行ではない。
- 約21万譜面のDB、約3万directory、800万規模resource reverse keyを含む実ライブラリのterminal wall-clockは未測定。小規模の処理量確認を、大規模速度向上率や操作全体の性能合格へ読み替えない。
- storage / canonical listの全件再構築等は後続R5a、その他の費用は[全体計画](../plan/BeMusicSeeker-library-mutation-performance.md)へ残る。
