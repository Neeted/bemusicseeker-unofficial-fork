using System;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ChartInfoDisplayFormatter
{
    internal const int FeatureUndefinedLongNote = 1;
    internal const int FeatureMineNote = 2;
    internal const int FeatureRandom = 4;
    internal const int FeatureLongNote = 8;
    internal const int FeatureChargeNote = 16;
    internal const int FeatureHellChargeNote = 32;
    internal const int FeatureStopSequence = 64;
    internal const int FeatureScroll = 128;

    internal static string FormatOptionalInt(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    internal static string FormatOptionalDouble(double? value)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }
        return FormatDouble(value.Value);
    }

    internal static string FormatFixedTwo(double? value)
    {
        return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty;
    }

    internal static string FormatDouble(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.000000001)
        {
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static string FormatDuration(int? milliseconds)
    {
        if (!milliseconds.HasValue)
        {
            return string.Empty;
        }
        return ((double)milliseconds.Value / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s";
    }

    internal static string FormatDifficulty(int? difficulty)
    {
        return (difficulty ?? 0) switch
        {
            1 => "BEGINNER",
            2 => "NORMAL",
            3 => "HYPER",
            4 => "ANOTHER",
            5 => "INSANE",
            _ => difficulty.HasValue ? difficulty.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
        };
    }

    internal static string GetDifficultyColorKey(int? difficulty)
    {
        return (difficulty ?? 0) switch
        {
            1 => "Beginner",
            2 => "Normal",
            3 => "Hyper",
            4 => "Another",
            5 => "Insane",
            _ => string.Empty,
        };
    }

    internal static string FormatJudge(int? judge)
    {
        if (!judge.HasValue)
        {
            return string.Empty;
        }
        if (judge.Value < 35)
        {
            return "VERYHARD";
        }
        if (judge.Value < 60)
        {
            return "HARD";
        }
        if (judge.Value < 85)
        {
            return "NORMAL";
        }
        if (judge.Value < 110)
        {
            return "EASY";
        }
        return "VERYEASY";
    }

    internal static string GetJudgeColorKey(int? judge)
    {
        if (!judge.HasValue)
        {
            return string.Empty;
        }
        if (judge.Value < 35)
        {
            return "VeryHard";
        }
        if (judge.Value < 60)
        {
            return "Hard";
        }
        if (judge.Value < 85)
        {
            return "Normal";
        }
        if (judge.Value < 110)
        {
            return "Easy";
        }
        return "VeryEasy";
    }

    internal static string FormatFeature(int feature)
    {
        string[] names =
        [
            HasFeature(feature, FeatureUndefinedLongNote) ? "LN" : null,
            HasFeature(feature, FeatureMineNote) ? "MINE" : null,
            HasFeature(feature, FeatureRandom) ? "RANDOM" : null,
            HasFeature(feature, FeatureLongNote) ? "LN(#LNMODE)" : null,
            HasFeature(feature, FeatureChargeNote) ? "CN" : null,
            HasFeature(feature, FeatureHellChargeNote) ? "HCN" : null,
            HasFeature(feature, FeatureStopSequence) ? "STOP" : null,
            HasFeature(feature, FeatureScroll) ? "SCROLL" : null
        ];
        return string.Join(" ", names.Where(item => !string.IsNullOrEmpty(item)));
    }

    internal static int? GetScratchNotes(LR2SongDBExtended.chart_info chartInfo)
    {
        return chartInfo == null ? (int?)null : chartInfo.s + chartInfo.ls;
    }

    internal static double? GetTotalPerNote(LR2SongDBExtended.chart_info chartInfo)
    {
        if (chartInfo == null || !chartInfo.total.HasValue || chartInfo.notes <= 0)
        {
            return null;
        }
        return chartInfo.total.Value / chartInfo.notes;
    }

    internal static bool HasFeature(int feature, int bit)
    {
        return (feature & bit) != 0;
    }
}
