# LR2 startup 手続き化計画

Status: Planned; not implemented

この文書は、現在 scheduler、共有 receipt、version 判定へ分散している startup／reload から
LR2 `song.db` 同期までの順序制御を、将来 application-level procedure owner へ移すための
作業計画である。現行仕様の正本ではなく、現在の one-shot 契約と narrow guard は
`devdocs/spec/lr2-song-db-generation.md` を正とする。

## Goal

startup の required work を一つの owner が明示的に await し、file diff の commit 結果を
LR2 terminal まで immutable な typed result として直接渡す。正しさを scheduler idle、
notification timing、broad version の一致から推測しない。

最終形の手続きは次の順序とする。

```text
preflight / user decision
  -> input acquisition
  -> catalog + file-diff commit
  -> LR2 に必要な chart-info 等の生成
  -> LR2 input / committed-path proof construction
  -> LR2 durable sync
  -> composite terminal publication
```

各 stage 内の独立した read／解析は並行実行してよいが、authoritative mutation と stage 間の
順序は直列かつ明示的に await する。

## Ownership and admission

- 最後の user decision 後から LR2 terminal まで、競合する app 内 mutation を開始させない
  logical mutation admission を一つ保持する。
- logical admission は DB transaction、model lock、UI thread の同期 block ではない。各 DB
  transaction／collection lock は、その stage 内の commit／apply に必要な期間だけ保持する。
- read-only の navigation、検索、表示は利用可能にする。catalog、playlist output、LR2 DB を
  変更する command は新しい pending queue を作らず、既存の busy contract で明示的に拒否する。
- dialog、Dispatcher、event subscriber、別 owner の同期完了を lock／transaction 保持中に
  待たない。

## Direct phase results

file diff は global slot へ receipt を publish せず、たとえば次の immutable result を直接返す。

```text
StartupCatalogCommitResult
  - captured scan surface
  - final storage-row snapshot
  - committed BMS paths
  - required prepared surfaces
```

後続 stage はこの result から必要な値だけを持つ typed result を返し、LR2 owner は直前の
result を request として受け取る。ambient state、service locator、巨大 callback interface、
version restamp／carry-forward は導入しない。

一つの logical admission と direct handoff が成立した startup route では、次を退役させる。

- startup 用 global committed-path receipt の publish／take／discard
- receipt／scan surface／LR2 startup input の version equality による因果関係の推測
- scheduler idle を観測して LR2 を登録する間接順序
- startup stage 間の required callback sequencing

`OwnedChartCollectionVersion` は UI cache や独立 snapshot の invalidation、operation identity は
late UI publication／shutdown の識別など、別の実需がある場所では残してよい。

## Failure and filesystem contract

- required prerequisite が失敗した場合は LR2 へ入らず、typed terminal result として通知する。
- LR2 が失敗しても、それ以前に正常 commit 済みの file diff／chart-info を巻き戻さない。
  global receipt や途中位置は保存せず、次の明示 request は fresh input の item zero から始める。
- retry、replay、rollback coordinator、fallback scan、resume manifest、新しい persistent state は
  追加しない。
- filesystem は acquisition 時点の point-in-time surface を今回の入力とする。その後の外部変更は
  次回 reload の入力であり、consumer 側の live lookup で新旧 surface を混在させない。
- shutdown は既存 cancellation／durable status 契約へ接続し、遅延完了を成功として publish しない。

## Migration units

1. **Typed stage adapters**
   - 現行の observable ordering を変えず、startup の各 required stage を `Task<StageResult>` と
     typed terminal へ包む。
   - property version、string reason、notification を completion signal として使う箇所を owner
     内へ閉じ込める。
2. **Startup direct chain**
   - file diff、required chart-info、LR2 preparation／sync を application-level startup owner の
     direct await chain へ移す。
   - logical mutation admission を LR2 terminal まで渡せる explicit capability-aware entry を設け、
     nested acquire や ambient ownership は使わない。
3. **Startup token／global slot retirement**
   - direct result が同じ事実を運ぶことを確認後、startup route の global receipt、scan／receipt
     version equality、scheduler-idle enrollment を削除する。
4. **Reload direct handoff**
   - reload を `FileDiffReloadResult -> LR2 request` の同じ direct handoff へ移す。manual resync は
     fresh capture route のまま維持し、startup proof を共有しない。
5. **Shared legacy cleanup**
   - startup／reload の双方が global receipt を使わなくなった後に storage と残る version consumer
     を削除する。UI cache、independent snapshot、schema／generator signature の token は対象外とする。

各 unit は独立した Test Contract Packet、production-ingress timeline、focused Quick、Functional、
static review を持ち、同じ巨大 file の同時編集は行わない。

## Completion criteria

- startup は file diff から LR2 terminal まで一つの明示的な procedure ordering を持つ。
- mutation command は同じ logical admission で拒否され、read-only UI は応答可能である。
- required stage の completion／failure／shutdown が typed result として一度だけ伝播する。
- startup correctness に global receipt、broad version equality、scheduler idle を使わない。
- DB／model lock、transaction、operation gate を保持した sync-over-async がない。
- persisted `song`／`folder`、status、user-owned columns、point-in-time filesystem contract が
  現行仕様と互換である。

## Non-goals

- この計画は今回の `LR2-OWNED-NARROW-20260904` 局所修正では実装しない。
- startup 完了を LR2 成功と同一視しない。
- external filesystem watcher、automatic retry／replay、resume cursor、persistent manifest、
  compatibility fallback を追加しない。
- playlist、settings、catalog、file-diff の durable writer ownership を別 decision なしに変更しない。
