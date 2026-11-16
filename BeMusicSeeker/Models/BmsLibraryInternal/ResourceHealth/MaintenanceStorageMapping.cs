using BeMusicSeeker.Models.LR2;
namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>保守テーブルの既存入出力境界で、保存列と共通の不変値を変換します。</summary>
internal static class MaintenanceStorageMapping
{
    /// <summary>保存列を捕捉し、読込み元が持つ可変状態を共通値へ持ち込みません。</summary>
    internal static ResourceHealthMaintenanceSnapshot ToCommon(LR2SongDBExtended.maintenance source,
        MaintenanceInfoOrigin origin = MaintenanceInfoOrigin.Calculated) => source == null ? null : new()
        {
            Path = source.path,
            Hash = source.hash,
            Encoding = source.encoding,
            WavFilesExisting = source.wav_files_existing,
            WavFilesDefined = source.wav_files_defined,
            BgaFilesExisting = source.bga_files_existing,
            BgaFilesDefined = source.bga_files_defined,
            MovieFilesExisting = source.movie_files_existing,
            MovieFilesDefined = source.movie_files_defined,
            StagefileExisting = source.is_stagefile_existing,
            StagefileDefined = source.is_stagefile_defined,
            BannerExisting = source.is_banner_existing,
            BannerDefined = source.is_banner_defined,
            BackbmpExisting = source.is_backbmp_existing,
            BackbmpDefined = source.is_backbmp_defined,
            FilesWarningIgnored = source.is_files_warning_ignored == true,
            Lr2WarningFlags = source.lr2_warning_flags,
            Lr2ResourceMaxRelativeCp932Bytes = source.lr2_resource_max_relative_cp932_bytes,
            Lr2ResourceHasParentTraversal = source.lr2_resource_has_parent_traversal,
            Origin = origin,
            EncodingFixed = source.is_encoding_fixed
        };

    /// <summary>既存のDB書込みへ渡す保守列だけを生成します。</summary>
    internal static LR2SongDBExtended.maintenance ToStorage(ResourceHealthMaintenanceSnapshot source) => source == null ? null : new()
    {
        path = source.Path,
        hash = source.Hash,
        encoding = source.Encoding,
        wav_files_existing = source.WavFilesExisting,
        wav_files_defined = source.WavFilesDefined,
        bga_files_existing = source.BgaFilesExisting,
        bga_files_defined = source.BgaFilesDefined,
        movie_files_existing = source.MovieFilesExisting,
        movie_files_defined = source.MovieFilesDefined,
        is_stagefile_existing = source.StagefileExisting,
        is_stagefile_defined = source.StagefileDefined,
        is_banner_existing = source.BannerExisting,
        is_banner_defined = source.BannerDefined,
        is_backbmp_existing = source.BackbmpExisting,
        is_backbmp_defined = source.BackbmpDefined,
        is_files_warning_ignored = source.FilesWarningIgnored,
        lr2_warning_flags = source.Lr2WarningFlags,
        lr2_resource_max_relative_cp932_bytes = source.Lr2ResourceMaxRelativeCp932Bytes,
        lr2_resource_has_parent_traversal = source.Lr2ResourceHasParentTraversal,
        is_encoding_fixed = source.EncodingFixed
    };
}
