# Historical net472 / .NET 10 Symptom Evidence

この文書は、`3c000ec2e7a6e619c60d0f8c9e48ad12bd06d4f5`のnet472 buildと、performance outcome開始時の.NET 10 buildで取得した一回ログから、調査候補を見つけた履歴である。

現在の正本は[.NET 10 performance engineering evidence](./net10-performance-engineering.md)である。

## Active use

既存logから次の候補が見つかった。

- playlist summaryはcompute後のUI queue／apply／renderにblind intervalがある。
- playlist detailはowner request前のmode transitionにblind intervalがある。
- full library routeにはlarge ordered-row materialization候補がある。
- resource-health、song-table、forced GC、scanにはstage／allocation情報が不足している。

## Inactive use

今後は次を行わない。

- net472側へのmarker追加。
- net472 build／操作の再実行。
- net472と.NET 10の厳密なA/B Gate。
- .NET 10 log schemaをnet472へ合わせる変更。

この文書の数値はrelease合否やperformance completionの判定に使用しない。
