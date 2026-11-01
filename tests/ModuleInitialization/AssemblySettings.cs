// 直接実行の既定値は一つ。runnerはrun専有runsettingsでCoreの計算済みworker数だけを上書きします。
[assembly: Parallelize(Workers = 1, Scope = ExecutionScope.MethodLevel)]
