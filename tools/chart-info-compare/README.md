# chart-info-compare

Compares BeMusicSeeker `chart_info` against beatoraja `songdata.db` and `songinfo.db`.

```powershell
dotnet run --project tools/chart-info-compare/ChartInfoCompare.csproj -- `
  --app-db "D:\LR2beta3\LR2files\Database\song.db" `
  --beatoraja-song-db "D:\beatoraja\songdata.db" `
  --beatoraja-info-db "D:\beatoraja\songinfo.db" `
  --out "tools/chart-info-compare/reports/latest"
```

To export non-RANDOM diff fixtures when the diff count is 1000 or less:

```powershell
dotnet run --project tools/chart-info-compare/ChartInfoCompare.csproj -- `
  --export-fixture "BeMusicSeeker.Tests\TestData\chart_info_production_diff" `
  --overwrite-fixture
```

RANDOM charts (`feature & 4 != 0`) are reported separately and excluded from value-diff acceptance.
