# テスト用データ

合成入力だけでは確認しづらい互換性や実データ由来の挙動を確認するための固定入力を置きます。実行区分・環境変数・コマンドは[テスト検証](../../devdocs/spec/development/testing.md#解析大規模データ外部エンコーダー)、入力を追加する判断は[テスト作成](../../devdocs/spec/development/test-authoring.md)を正本とします。

## データの用途

| ディレクトリ | 確認する内容 | 実行条件 |
| --- | --- | --- |
| `archives` | 小規模な圧縮ファイルの処理。 | 通常検証で使用する。 |
| `chart_info_real` | BMS実譜面1,000件とbeatoraja / jbms-parserの参照値の比較。 | `BMS_TEST_CHART_INFO_FULL=1` で全件を検証する。 |
| `chart_info_bmson_real` | BMSON実譜面1,034件と参照値の比較。 | `BMS_TEST_CHART_INFO_FULL=1` で全件を検証する。 |
| `chart_info_edge_cases` | 巨大な時系列、`RANDOM` の数値範囲超過、初期BPMの境界条件。 | `BMS_TEST_CHART_INFO_FULL=1` で実譜面を検証する。小さい合成入力による同種の確認は通常検証に残す。 |
| `chart_info_production_diff` | 実データと参照実装の解析結果の差分。 | `BMS_TEST_PRODUCTION_DIFF_FULL=1` で全件を検証する。 |
| `chart_info_production_latest_diff` | 更新された参照値との差分。対象は少数でも入力が大容量のもの。 | `BMS_TEST_PRODUCTION_DIFF_FULL=1` で検証する。 |
| `lr2_builtin_custom_folder_real` | LR2組込みカスタムフォルダの実ファイルとの互換性。 | 通常検証で使用する。 |

## 大容量入力の追加と使用

実譜面・実データ差分は `LargeFixture` として扱い、用途に応じて `ParserCompatibilityFull`、`ProductionDiffFull`、`ParserCompatibilitySlow` を指定します。通常実行で全件解析や巨大DBのコピーが始まらないよう、対応する環境変数による明示的な有効化を要求します。

`chart_info_real` と `chart_info_edge_cases` は、ビルド開始前に `BMS_TEST_CHART_INFO_FULL=1` が設定されている場合だけテスト出力先へコピーします。環境変数が無効なら対象テストは `Inconclusive`、有効なのに必要な入力がなければ失敗とします。必要な入力の欠落を検証成功として扱いません。

回帰テストは、まず最小の合成譜面や小さいDBで再現します。実データが必要な場合も、全件入力に依存させる前に該当する入力だけを切り出せるか確認します。カテゴリの追加や大容量データの保存だけで、期待結果の根拠を代替しません。

## 外部音声エンコーダー

`ExternalAudioEncoderSmokeTests` が使う `lame.exe`、`neroAacEnc.exe`、`opusenc.exe`、`flac.exe`、`oggenc2.exe` は保存しません。明示的な実行時に利用者が用意した実行ファイルを使い、入力WAVはテスト中に再現可能な方法で生成します。一時ファイルとエンコーダーの実行資源は終了時に解放します。

有効化、検索先、対象形式、実行ファイル欠落時の判定は[外部エンコーダーの検証条件](../../devdocs/spec/development/testing.md#解析大規模データ外部エンコーダー)に従います。テスト用の実行ファイルや生成音声を、このディレクトリへ追加しません。
