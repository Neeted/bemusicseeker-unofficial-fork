# scripts Codex instructions

この directory 以下の build、verification、publish、update script を変更するときは、root `AGENTS.md` に加えて `../devdocs/spec/testing-strategy.md` と、テストを伴う場合は `../devdocs/spec/test-authoring-contract.md` を読む。

## Executable contract first

- script の自己申告 metadata だけをテストしない。実際に実行側が消費する plan、helper、manifest、process lifecycle seam をテストする。
- contract metadata を残す場合、実行側が同じ object を正本として消費するか、actual execution から生成する。metadata と metadata test だけを同時に更新して green にできる構造を作らない。
- Full / Functional の phase 順、budget、artifact identity、failure precedence を変える場合は、plan へ observable execution delta を示す。

## Process lifecycle

redirected process は次を一つの bounded lifecycle として扱う。

1. process start / exit wait
2. process timeout 時の runner-owned tree 停止
3. stdout / stderr の bounded drain
4. completed output と cleanup diagnostic の保存
5. runner-owned PID lineage の残留確認
6. primary failure、cleanup failure、diagnostic write failure の優先順位

- incomplete な stream task へ無期限の `.Result` / `GetAwaiter().GetResult()` を行わない。
- Functional の execution deadline（既定・上限 300 秒）は、runsettings 等の準備後、portable `dotnet test` の起動直前に一度だけ作り、portable とその成功後に fanout する全 Functional testhost の実際の終了時刻を判定する。host や phase ごとに deadline をリセットしない。script startup、restore、build、preflight、output / artifact 回収、fingerprint、環境復元、whitespace 確認はこの test execution budget に含めない。短い値は timeout seam として許可するが、caller が 300 秒を超える値を指定することは許可しない。
- Functional が execution deadline を超えた場合は、runner-owned process cleanup と stream drain だけが、execution deadline + 10 秒の一つの failure-cleanup cutoff まで継続できる。この追加 window は失敗 invocation の cleanup 専用で、deadline 後に終了した testhost を成功へ救済しない。runner の poll 時刻ではなく retained process handle の終了時刻で deadline 内完了を判定する。
- retained `ExitTime` から求めた elapsed が 180 秒以下なら通常成功として扱う。180 秒を超えても 300 秒以下なら成功を維持し、通常の elapsed 出力に加えて 180 秒 target 超過、actual retained-`ExitTime` elapsed、およびその実時間を user-facing report に必ず含める指示を warning / diagnostic へ出力する。180 秒ちょうどにはこの特別な warning を出さず、retry は真の 300 秒 timeout に限る。
- timeout / nonzero exit / orchestration failure を stream cleanup failure で上書きしない。成功 process の cleanup failure は成功扱いにしない。
- process 名だけで無関係な `dotnet` / `testhost` / `vstest` を kill しない。起動した root PID と追跡した descendant だけを対象にする。
- production lifecycle の検証は shared helper または guarded execution probe を通し、実行側と同じ lifecycle owner を使う。

## Verification

- runner、lane、parallelization、fixture placement、shared infrastructure を変えた場合は focused contract を実行し、最終 acceptance lane は `testing-strategy.md` に従って統合 owner が実行する。
- 各 run で portable testhost 開始直前から全 Functional testhost の実際の process `ExitTime` までの test-execution elapsed、diagnostics root、tracked fingerprint、runner-owned residual process を記録する。restore、build、preflight、postflight、artifact 回収、fingerprint、環境復元、whitespace確認等の時間は test-execution elapsed と混同しない。180 秒 target を超えた成功 run は、出力された actual elapsed を user-facing report へ転記する。
- timeout / failure の分類と retry は `testing-strategy.md` を正本とし、runner は判断に必要な process / artifact evidence を保持する。timeout 延長、worker 低下、unbounded retry で flake を隠さない。
