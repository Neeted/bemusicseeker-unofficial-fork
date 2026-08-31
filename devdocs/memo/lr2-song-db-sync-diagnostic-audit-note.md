# LR2 song.db 同期診断の監査メモ

## この資料の位置づけ

この資料は、LR2 `song.db` 同期末尾の startup scan diagnostic と `Completed` 判定について行った静的調査の記録である。

ただちに修正する課題ではなく、LR2 `song.db` 同期を後日再計画するときの入力として残す。現行仕様を変更する正本ではない。採用する修正方針は、再計画時に `devdocs/spec/lr2-song-db-generation.md`、failure contract、再開方式、test contract と合わせて決定する。

調査対象の中心は次の実装である。

- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncStatusService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbWriter.cs`
- `BeMusicSeeker/Models/BMSLibrary.Lr2SynchronizationOwner.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/CatalogMutationOwner.cs`

## 結論

現行の `Lr2StartupScanDiagnosticResult` には、次の異なる性質の結果が混在している。

1. 同期完了後には成立してはならないDB整合性違反。
2. discovery、directory metadata、file readなどの入力不完全。
3. 別ownerが管理する領域や設定状態に関する警告。
4. 正当な値を異常とみなす可能性がある不健全なheuristic。

このため、現在の `IsClean` / `TotalBlockerCount` をそのまま `Completed` のgateに使ってはならない。一方、`MissingCurrentSongRowCount` のような明確な不整合を残したまま `Completed` にする現在の処理も、`Completed` が期待 `song` / `folder` 集合への収束を表すという仕様と一致しない。

後日の修正では、startup scan diagnostic全体を成功判定へ昇格させるのではなく、次を分離する必要がある。

- authoritativeな同期完了条件。
- 自動修復可能な差分。
- 入力不完全による `Incomplete`。
- completionを妨げないadvisory diagnostic。

なお、診断が残っていてもsourceがcurrentなら `Completed` を記録する基本挙動と、`date == 0` をmissing扱いする曲診断は、少なくとも前回安定版 `3c000ec2e7a6e619c60d0f8c9e48ad12bd06d4f5` にも存在する。したがって、この中心的な挙動だけを理由にv3で新しく発生した回帰とは分類しない。ただし、安全な再開や現在入力への収束という観点では潜在的な問題である。

## 期待する同期結果

LR2 `song.db` の生成列について期待する結果は、空DBから現在入力を使って構築した状態と同等である。ただし、既存rowにあるLR2ユーザーデータ列は維持する。

### song table

- 現在の `SongRows` に含まれるBMS pathが期待集合である。
- bmsonは期待集合に含めない。
- `hash`、title等の解析列、`date`、`folder`、`parent` などの生成列は現在入力から生成した値に一致する。
- 既存rowの `favorite`、`adddate`、`tag` など、維持対象のユーザー列は上書きしない。
- 期待集合にないstale rowは削除する。
- chart fileを同期時に読めない場合でも、現在のcatalog rowの永続化copyを使うfallbackがあり、rowそのものを欠落させない。

### folder table

- 完全なscan surface、directory metadata、`.lr2folder` discoveryから導かれる期待rowを生成する。
- 同期scope内で期待集合にないstale rowを削除する。
- playlist materializationなど別ownerの管理領域は、そのownerのfailure contractに従い、汎用LR2同期が勝手に修復・削除しない。
- discoveryやmetadataが不完全な場合は、完全な入力で構築したと偽らず、不完全状態として扱う。

## `MissingCurrentSongRowCount` が発生し得る経路

通常の非再開full song stageでは、各 `SongRows` rowをUpsertし、SQLite書込み失敗は例外として処理を中断する。その直後に期待rowが存在しない状態は、通常のデータ状態ではなく同期前提の破綻である。

現実的な発生候補は次のとおり。

### file diff由来のskipを誤って信用した

自動file diff follow-upでは、新規挿入済みと記録されたpathを `TransientSongRowsSkipPaths` として受け取り、LR2 song row stageで書込みを省略する経路がある。全行がfresh new insertと判断された場合はsong stage全体をskipできる。

この判定はfile diff freshness snapshotを信用し、対象 `song.db` にrowが実在して生成列がcurrentであることを再照合しない。通常のfile diff commit barrierが正しく働けばrowは存在するはずだが、receiptと実DB状態の対応が崩れた場合に安全側へ倒れない。

`SyncService_TransientSongRowSkipPathsSkipOnlyMatchingRows` は、存在しないrowをskip対象として渡すと、そのrowを書かないまま処理が完了することを示している。これはservice単体へcaller contractを満たさない入力を与えたtestではあるが、service側にpostcondition enforcementがないことも示す。

### durable cursorを異なる入力prefixへ適用した

resume candidateは主に次の条件で再利用される。

- stored statusがresumableである。
- signatureが一致する。
- total countが一致する。
- processed cursorが正である。

現在のsignatureはschema/parser/generator versionとoperation mode等を含むが、曲path集合、その順序、folder target集合のmanifestは含まない。

例えば、前回入力が `[A, B]` でAまでcommitした後に中断し、次回入力が `[C, B]` へ変化しても、total countが同じならCを処理済みprefixとしてskipできる。その後Aはstale pruneで削除され、Cが欠落して `MissingCurrentSongRowCount` となり得る。

source current probeは、今回捕捉したinputが実行中に変化していないことは確認するが、durable cursorが過去に処理したinput prefixと今回のprefixが同一であることは証明しない。

### 外部変更、破損、またはpath identityの不整合

skipまたはcursor更新後に、LR2、別process、手動操作、DB復元などがrowを削除した場合にも発生する。アプリ内mutation gateは外部processを拘束しない。

Upsertが正常終了したにもかかわらず同じpathを検索できない場合は、DB破損、path canonicalization、writer実装など、より重大な不整合も候補になる。

### 通常は原因にならないもの

- chart file read failure。
- chart parse failure。
- Shift_JIS非互換path。

chart read/parse failureではcatalog rowのcopyをUpsertする。Shift_JIS非互換pathもBeMusicSeeker管理対象である限りsong row自体は維持し、LR2互換列を `NULL` として警告する設計である。

## date診断の問題

### song date

`DateMissingSongRowCount` は期待mtimeとDB値を比較せず、DBの `song.date` がnon-nullかつ厳密に `0` の場合だけ加算する。

この条件には次の問題がある。

- mtimeがUnix epoch `1970-01-01T00:00:00Z` の実ファイルは正しく変換しても `0` になる。
- 空DBから生成した正しいrowも同じ値になり、常に診断される。
- epoch以前の負数は診断しない。
- `NULL` も診断しない。
- 正の値であっても期待mtimeと異なるstale値は診断しない。

したがって、`0` をmissing sentinelとする診断は、空DBからの再生成との同値性を検証しない。期待projectionの `date` とactual `date` をnullable valueとして比較する必要がある。`0` と負数も、mtime変換結果として得られた値なら正当値として扱う。

### folder date

`DateMissingFolderRowCount` は `folder.date` が `NULL` または `<= 0` の場合に加算する。さらに、folder date repairは解決したUnix secondsが `> 0` の場合だけ採用する。

このため、mtimeがepochまたはepoch以前のdirectory / `.lr2folder` では次の非収束・破壊的経路があり得る。

1. 空DB生成と同じ処理で `0` または負数のrowを生成する。
2. startup diagnosticがdate missingと判定する。
3. 正当なmtimeとしてrepairできず、cleanup対象としてrowを削除する。
4. 次回同期で再生成しても、同じ診断と削除を繰り返す。

これは診断表示だけの問題ではなく、正しいfolder rowの誤削除につながる。

## 各診断項目の分類

| 診断 | 現在検査しているもの | 発生候補 | 再計画時の分類 |
|---|---|---|---|
| `NoRootSetBlockerCount` | 現在は常に0 | なし。値を増やす実装がない | dead field。削除または意味を再定義 |
| `MissingCurrentSongRowCount` | current `SongRows.path` に対応するDB rowの不在 | skip contract破綻、resume prefix不一致、外部削除、DB/path不整合 | hard completion failure |
| `DateMissingSongRowCount` | `song.date == 0` | 正当なepoch、legacy row、skip/resumeで残った値 | heuristicを廃止しexpected value比較へ変更 |
| `UnknownRootSongRowCount` | DB song pathがroot外 | current catalog自体にroot外rowがある、root設定との不一致 | source/config advisory。song DB収束失敗とは限らない |
| `MissingExpectedFolderRowCount` | 期待normal folder rowの不在 | resume skip、metadata不足、directory read失敗、生成不能 | complete inputならfailure。不完全入力なら明示的 `Incomplete` |
| `MissingExpectedLr2FolderRowCount` | 発見済み `.lr2folder` / parent rowの不在 | definition read失敗、metadata不足、resume skip、projection不能 | complete inputならfailure。不完全入力・unsupported理由を分離 |
| `DateMissingFolderRowCount` | `folder.date` が `NULL` または `<= 0` | malformed rowに加え、正当なepoch/pre-epoch | 現条件は不健全。expected value比較へ変更 |
| `DateStaleFolderRowCount` | 正のactual dateと解決できた正のmtimeの不一致 | in-scope stale row、別owner領域のstale row | owned rowならrepair対象。prune-excluded領域はadvisory |
| `UnknownRootFolderRowCount` | normal/LR2 discovery scope外のfolder row | stale/foreign cache row、root変更、path分類差 | complete scopeならcleanup対象。不完全scopeでは削除を抑止 |

## `IsClean` を成功条件にできない理由

現行testには、診断を意図的に残して `Completed` とする契約がある。

- current song rowがroot外でもsong rowを保持し、`UnknownRootSongRowCount = 1` のまま `Completed` とする。
- playlist等の管理対象としてprune除外された `.lr2folder` のdateがstaleでも、汎用同期は修復せず `DateStaleFolderRowCount = 1` のまま `Completed` とする。
- incomplete `.lr2folder` discoveryでは既存rowを安全のためcleanupしない。

これらを一律blockerにすると、状態変化のない再試行を繰り返しても収束しない。別ownerの仕事を汎用同期が奪う危険もある。

一方で現在の `TotalBlockerCount` はこれらadvisory項目とhard failureを合算する。名称と利用可能な意味が一致していない。

## 後日の再計画で優先して決める事項

### authoritative completion contract

`Completed` の判定はstartup scan diagnosticの総数ではなく、少なくとも次の独立した契約から構成する。

- 今回のsource snapshotがcurrentである。
- 必要なdiscovery / enumeration surfaceがcompleteである。
- expected song path集合とactual song path集合が一致する。
- 今回書いた、またはskip/resumeしたsong rowの生成列が現在projectionと一致する。
- completeなowner scope内のexpected folder rowが存在する。
- owner scope内のstale row pruneとdate repairが完了している。
- 別owner / prune-excluded領域の状態を汎用同期の成功条件に混ぜない。

### skip verification

file diffのreceiptだけを信用してskipするか、対象DBでcurrentnessを確認するかを決める。

既存の `Lr2SongDbWriter.VerifyGeneratedSongsCurrent` は、expected generated columns、path case、digestをactual DBと比較できるが、現在production callerがない。全曲を毎回照合するのではなく、次の限定scopeで使う案を検討する。

- `TransientSongRowsSkipPaths` の対象。
- song stage全体skipの候補。
- resume cursorより前のskipped prefix。

確認に失敗した場合は意味の変わるfallbackで成功扱いせず、該当stageを安全な境界から再実行する。

### resume identity

total countとcursorだけではinput identityを証明できない。次のいずれかを選ぶ。

- stageごとのordered input manifest hashをstatusへ保存する。
- cursor再利用前にskipped prefixのkey/currentnessを検証する。
- song stageはkey-based checkpointへ変更する。
- 入力同一性を証明できない場合はstage開始位置へ巻き戻す。

folder target count、song row count、ordered targetの境界が一つの整数cursorへ連結されているため、stage構成が変化した場合の意味も明文化する。

### retry and convergence contract

診断が残ったときに単に `Incomplete` を保存するだけでは、同じend cursorから再開して欠落rowを再処理しない可能性がある。

- missing/mismatched song rowはtargeted upsertまたはsong stage rewindを行う。
- missing expected folder rowは完全なinputがある場合だけ該当folder stageを再実行する。
- discovery/read/metadata failureは原因を保存して `Incomplete` とし、入力が変わらない無限retryを避ける。
- 一度の自動repair後も同じdiagnostic fingerprintが残る場合は、明示的failureとしてUIに出す。
- root/config advisoryや別owner領域は自動retry対象にしない。

### timestamp contract

- Unix secondsの全 `int` 値を正当な変換結果として扱うかを明記する。
- `0` / negativeをsentinelにしない。
- expected metadataが存在する場合は値の等価性を検査する。
- metadataが存在しない場合は、DB row corruptionとsource incompletenessを区別する。

## 想定するregression test観点

再計画時には実装前に独立したtest contractを作成する。少なくとも次のnegative controlが必要である。

1. file diff skip対象として宣言されたrowが対象DBに存在しない場合、`Completed` にならない。
2. `[A, B]` のAで中断後、同数の `[C, B]` へ変わったretryでCを処理済み扱いしない。
3. resume prefixにrowはあるが生成列がstaleの場合、current扱いしない。
4. Unix epochのchart mtimeを持つsong rowがdiagnostic failureにならない。
5. Unix epochおよびepoch以前のdirectory / `.lr2folder` rowをcleanupで削除しない。
6. positive timestampのstale owned folder rowは期待値へrepairされる。
7. prune-excluded / playlist-owned folder rowのadvisory diagnosticが汎用同期の無限retryを起こさない。
8. incomplete discovery / missing metadataを完全な同期成功として記録しない一方、未知rowをbroad pruneしない。
9. chart file read failure時もcatalog copyからsong rowが存在し、失敗を欠落rowとして隠さない。
10. existing `favorite` / `adddate` / `tag` を維持しながら生成列だけがempty-DB projectionと一致する。

## 参照する現行test

- `BeMusicSeeker.Tests/Lr2SongDbSyncServiceTests.cs`
  - `SyncService_CompletesWhenCurrentSongRowIsOutsideRootAndKeepsDiagnostic`
  - `SyncService_PruneExcludedLr2FolderDateStaleRemainsDiagnosticOnly`
  - `SyncService_IncompleteLr2FolderDiscoveryDoesNotCleanupExistingLr2FolderRows`
  - `SyncService_TransientSongRowSkipPathsSkipOnlyMatchingRows`
  - `SyncService_ResumesInsideSongRowsFromDurableCursor`
  - `SyncService_DoesNotTreatNegativeSongDateAsMissing`
  - `SyncService_DoesNotTreatNullSongDateAsMissing`
- `BeMusicSeeker.Tests/Lr2SongDbWriterTests.cs`
  - `VerifyGeneratedSongsCurrent_*`

既存testの一部は現在挙動を固定しているが、それが将来の正しいcompletion contractを意味するとは限らない。再計画時には、維持すべきowner境界と、置換すべき不健全な期待値をtest contractで区別する。
