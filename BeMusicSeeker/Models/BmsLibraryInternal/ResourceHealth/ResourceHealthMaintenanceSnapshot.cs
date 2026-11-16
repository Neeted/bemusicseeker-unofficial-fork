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

    /// <summary>メンテナンス情報がDB読込み、計算、未計算のいずれから得られたかを表します。</summary>
    internal MaintenanceInfoOrigin Origin { get; init; }

    /// <summary>既存の文字コード修正状態を保存時にも保持します。</summary>
    internal bool EncodingFixed { get; init; }

    /// <summary>補助画像3役割と件数ペアの保存形から検査完了を導きます。未解決null/nullと旧0/nullも完了として保持します。</summary>
    internal bool IsInformationChecked => IsCountChecked(WavFilesDefined, WavFilesExisting)
        && IsCountChecked(BgaFilesDefined, BgaFilesExisting) && IsCountChecked(MovieFilesDefined, MovieFilesExisting)
        && GetStagefileHealth().HasValue && GetBannerHealth().HasValue && GetBackbmpHealth().HasValue;

    private static bool IsCountChecked(int? defined, int? existing)
        => existing.HasValue ? defined.HasValue : !defined.HasValue || defined == 0;

    /// <summary>使用音声の存在数/要求数を整数率へ変換します。0件と未判定はnullで、不足は100へ丸めません。</summary>
    internal int? GetWavHealth() => CalculateCountHealth(WavFilesDefined, WavFilesExisting);

    /// <summary>使用画像の存在数/要求数を整数率へ変換します。0件と未判定はnullで、不足は100へ丸めません。</summary>
    internal int? GetBgaHealth() => CalculateCountHealth(BgaFilesDefined, BgaFilesExisting);

    /// <summary>使用動画の存在数/要求数を整数率へ変換します。0件と未判定はnullで、不足は100へ丸めません。</summary>
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
        if (defined == 0)
        {
            return null;
        }
        if (existing == defined)
        {
            return 100;
        }
        return (int)((long)existing.Value * 100 / defined.Value);
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
