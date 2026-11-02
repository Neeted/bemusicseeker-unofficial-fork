# 保留中の追加シナリオ

`Install/MissingDestination*` の3ファイルは追加SC-05fの保留入力です。テストproject外に置き、通常・Fullの発見対象へ含めません。PassedやSkippedとして扱いません。

起動の必須処理完了と追加仕事の終端は異なります。現行の `ScenarioStartupCompleted` は、必須初期化の成功後に起動追加仕事の実終端を待つよう修正済みです。過去の初回Drop Busyを現在も未解消とは断定しませんが、SC-05f自体の最新成功は未確認です。

[再開計画](../../../devdocs/plan/test-platform-migration.md)に従い、現行の受付条件と実到達を確認します。明示導入先の消失時に宛先を再作成せず元bytesとpending/install/songを保全し、実再読込・別の実在宛先への再選択・新しい手動導入要求の成功まで検証します。回復を成立させるためのcatalog/DB直接書込みや、Busyの無視・skipは行いません。

再開時に期待・実装・検証・選択範囲を揃えるまでは、この3ファイルをCompile対象外で保持します。共用の起動fixture・driver・製品と、既存MissingDSTのCore2メソッド・3行は維持します。
