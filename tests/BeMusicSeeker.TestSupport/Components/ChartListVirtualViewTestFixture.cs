using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>分割した同機能caseが実際に共用する入力・観測のhelperだけをまとめます。</summary>
internal static class ChartListVirtualViewTestFixture
{

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static ChartFile CreateFile(
        string path,
        string title,
        string folder,
        string artist = "",
        string genre = "",
        int? level = null,
        int? mode = null,
        string tag = "",
        string hash = "0123456789abcdef0123456789abcdef",
        string sha256 = "",
        int? scoreSeed = null,
        int? chartSeed = null,
        int? maintenanceSeed = null,
        int? warningSeed = null)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Path = path, Title = title, Folder = folder, Artist = artist, Genre = genre, Level = level, Mode = mode, Tag = tag, Md5 = hash, Sha256 = sha256, RawTitle = title, RawArtist = artist, LevelText = level?.ToString() ?? "" };
        if (scoreSeed.HasValue)
        {
            int seed = scoreSeed.Value;
            file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(CreateScore(
                file.Md5,
                seed == 1 ? ClearType.HARD : seed == 2 ? ClearType.EASY : ClearType.FC,
                seed == 1 ? RankType.A : seed == 2 ? RankType.AA : RankType.B,
                perfect: 300 + seed * 100,
                great: 50 + seed * 20,
                totalNotes: 600 + seed * 100,
                maxCombo: 400 + seed * 10,
                minBp: seed == 2 ? -1 : seed * 5,
                ranking: seed * 10,
                rankingNum: 100,
                rankingLastUpdate: new DateTime(2026, 5, 15, seed, 0, 0, DateTimeKind.Local),
                stdDevVal: 40.0 + seed,
                scoreDifficulty: 70.0 + seed), file.Path));
        }
        if (maintenanceSeed.HasValue)
        {
            file = ChartFileProjection.WithMaintenance(file, CreateMaintenanceInfo(file.Path, file.Md5, maintenanceSeed.Value));
        }
        if (warningSeed.HasValue)
        {
            switch (warningSeed.Value)
            {
                case 1:
                    file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, "zero note warning")] };
                    break;
                case 2:
                    file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")] };
                    break;
                default:
                    file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ChartInfoParseFailure), ChartWarning.Create(ChartWarningKind.ChartInfoParseFailure, "chart info warning")] };
                    break;
            }
        }
        return file;
    }

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static ChartFile CreateBmsonSong()
    {
        return ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"folder-b\bmson.bmson",
            Folder = "folder-b",
            Title = "BmsonTitle Subtitle",
            RawTitle = "BmsonTitle",
            Subtitle = "Subtitle",
            Artist = "BmsonArtist",
            RawArtist = "BmsonArtist",
            Genre = "BmsonGenre",
            ModeHint = "beat-7k",
            Mode = 7,
            Level = 6,
            LevelText = "6",
            Md5 = "22222222222222222222222222222222",
            Sha256 = "2222222222222222222222222222222222222222222222222222222222222222",
            ResourceHealthMaintenanceSnapshot = CreateMaintenanceInfo(@"folder-b\bmson.bmson", "22222222222222222222222222222222", 3)
        };
    }

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static BMSScore CreateScore(
        string hash,
        ClearType clear,
        RankType rank,
        int perfect,
        int great,
        int totalNotes,
        int maxCombo,
        int minBp,
        int ranking,
        int rankingNum,
        DateTime rankingLastUpdate,
        double stdDevVal,
        double scoreDifficulty)
    {
        return new BMSScore
        {
            hash = hash,
            clear = clear,
            rank = rank,
            perfect = perfect,
            great = great,
            totalnotes = totalNotes,
            maxcombo = maxCombo,
            minbp = minBp,
            ranking = ranking,
            rankingNum = rankingNum,
            rankingLastupdate = rankingLastUpdate,
            stddevVal = stdDevVal,
            scoreDifficulty = scoreDifficulty
        };
    }

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static ResourceHealthMaintenanceSnapshot CreateMaintenanceInfo(string path, string hash, int seed)
    {
        return new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = path, Hash = hash, Encoding = seed % 2 == 0 ? "utf-8" : "shift_jis", WavFilesDefined = 100, WavFilesExisting = 20 + seed * 10, BgaFilesDefined = 50, BgaFilesExisting = 10 + seed * 5, MovieFilesDefined = 20, MovieFilesExisting = seed };
    }

}
