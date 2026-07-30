# Historical net472 / .NET 10 Symptom Note

現在の正本は[.NET 10 performance engineering evidence](./net10-performance-engineering.md)である。

この文書は、既存の次のlogを修正優先度の参考に使ったことだけを記録する。

```text
.tmp/20260731_net472_log
.tmp/20260731_.NET 10_log
```

用途:

- playlist summaryのcompute後UI applyが長いこと。
- playlist detail自体はcurrent .NET 10で高速なこと。
- detail→libraryのpresentation blind intervalが長いこと。
- startup全体は一覧ほど優先度が高くないこと。

今後は次を行わない。

- net472側へのmarker追加。
- net472 build／操作の再実行。
- net472と.NET 10の厳密なp50／p90 A/B Gate。
- .NET 10 log schemaをnet472へ合わせる変更。
- net472比の数値がないことを理由にcurrent .NET 10の高速化を延期すること。
