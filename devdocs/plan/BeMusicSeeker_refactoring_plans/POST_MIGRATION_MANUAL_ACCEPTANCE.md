# Post-engineering Manual Acceptance

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md)

この文書はCodex Engineering Gate完了後にユーザーが行う確認だけを持つ。未実施をCodexのactive outcome、planner停止条件、`EXTERNAL_BLOCKER`にしない。

## MANUAL-01 — Final real-data performance

final .NET 10 artifactと通常のsettings／DB／playlist／chart treeを使い、一度だけ次を確認する。

1. 起動から操作可能まで。
2. playlist summaryの初回表示と再訪。
3. small／large playlist detail。
4. detail／summaryからfull libraryおよび代表subsetへの切替。
5. install destination estimation。
6. 通常利用する場合のfile diff／scan。
7. rapid reentry、cancel、shutdown。
8. UI freeze、秒単位のblank interval、継続的なmemory増加。

net472の再build、marker追加、厳密な反復A/Bは行わない。

問題が残る場合は、final .NET 10 logのinteraction IDと次の時刻を保存する。

```text
input accepted
owner started
presentation queued
presentation started
source / schema applied
selection restored
first useful visible
```

## MANUAL-02 — Runtime-free machine

.NET Desktop Runtime未導入のclean x64 Windows／VMで、Self-contained publish folderから起動、基本操作、終了、updaterを確認する。

## RELEASE-01 — Distribution rights

BASS.NET等のproprietary／vendor componentについて、licensee、registration、redistribution証跡を公開前に確認する。
