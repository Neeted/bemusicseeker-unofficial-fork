# chartstring-dump

Reference diagnostic tool for `chart_info.charthash` compatibility work.

The tool compiles against the local `songdata-updater` sources and
`lib/jbms-parser.jar`, but does not modify either reference repository.
It decodes each chart with jbms-parser, applies `BMSPlayerRule.validate(model)`,
and writes the hash of `model.toChartString()` as JSONL.

Example:

```powershell
& .\tools\chartstring-dump\run.ps1 --list paths.txt --chart-string-dir artifacts\chartstring-dump\strings
```

If Java is not on `PATH`, set `-JavaHome` or `JAVA_HOME`.
