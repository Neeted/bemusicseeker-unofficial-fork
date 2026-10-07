using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>共通譜面が捕捉する変更不能な資源健全性と検査の由来です。</summary>
internal sealed record ResourceHealthMaintenanceSnapshot
{
    internal string Path { get; init; }

    internal string Hash { get; init; }

    internal string Encoding { get; init; }

    internal int? WavFilesExisting { get; init; }
    internal int? WavFilesDefined { get; init; }
    internal int? BgaFilesExisting { get; init; }
    internal int? BgaFilesDefined { get; init; }
    internal int? MovieFilesExisting { get; init; }
    internal int? MovieFilesDefined { get; init; }
    internal bool? StagefileExisting { get; init; }
    internal bool? StagefileDefined { get; init; }
    internal bool? BannerExisting { get; init; }
    internal bool? BannerDefined { get; init; }
    internal bool? BackbmpExisting { get; init; }
    internal bool? BackbmpDefined { get; init; }
    internal bool FilesWarningIgnored { get; init; }
    internal int? Lr2WarningFlags { get; init; }
    internal int? Lr2ResourceMaxRelativeCp932Bytes { get; init; }
    internal bool? Lr2ResourceHasParentTraversal { get; init; }

    /// <summary>保守情報がDB読込み、計算、未計算のいずれから得られたかを表します。</summary>
    internal MaintenanceInfoOrigin Origin { get; init; }

    /// <summary>既存の文字コード修正状態を保存時にも保持します。</summary>
    internal bool EncodingFixed { get; init; }

    /// <summary>資源と画像の検査件数が確定しているかを返します。</summary>
    internal bool IsInformationChecked => GetWavHealth().HasValue && GetBgaHealth().HasValue
        && GetMovieHealth().HasValue && GetStagefileHealth().HasValue
        && GetBannerHealth().HasValue && GetBackbmpHealth().HasValue;

    internal int? GetWavHealth() => CalculateCountHealth(WavFilesDefined, WavFilesExisting);

    internal int? GetBgaHealth() => CalculateCountHealth(BgaFilesDefined, BgaFilesExisting);

    internal int? GetMovieHealth() => CalculateCountHealth(MovieFilesDefined, MovieFilesExisting);

    internal bool? GetStagefileHealth() => CalculateFlagHealth(StagefileDefined, StagefileExisting);

    internal bool? GetBannerHealth() => CalculateFlagHealth(BannerDefined, BannerExisting);

    internal bool? GetBackbmpHealth() => CalculateFlagHealth(BackbmpDefined, BackbmpExisting);

    private static int? CalculateCountHealth(int? defined, int? existing)
    {
        if (!defined.HasValue || (defined > 0 && !existing.HasValue))
        {
            return null;
        }
        if (defined == 0 || existing == defined)
        {
            return 100;
        }
        return (int)(100.0 * ((double)existing.Value - Math.Sqrt(existing.Value)) / defined.Value);
    }

    private static bool? CalculateFlagHealth(bool? defined, bool? existing)
    {
        if (!defined.HasValue || (defined.Value && !existing.HasValue))
        {
            return null;
        }
        return defined == false || (defined.Value && existing.Value);
    }

}
