using BeMusicSeeker.Models;
using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable resource-health facts carried by a detached chart projection.
/// </summary>
internal sealed class ResourceHealthMaintenanceSnapshot
{
    private ResourceHealthMaintenanceSnapshot(BMSFileMaintenanceInfo source)
    {
        Path = source?.path;
        Hash = source?.hash;
        Encoding = source?.encoding;
        WavFilesExisting = source?.wav_files_existing;
        WavFilesDefined = source?.wav_files_defined;
        BgaFilesExisting = source?.bga_files_existing;
        BgaFilesDefined = source?.bga_files_defined;
        MovieFilesExisting = source?.movie_files_existing;
        MovieFilesDefined = source?.movie_files_defined;
        StagefileExisting = source?.is_stagefile_existing;
        StagefileDefined = source?.is_stagefile_defined;
        BannerExisting = source?.is_banner_existing;
        BannerDefined = source?.is_banner_defined;
        BackbmpExisting = source?.is_backbmp_existing;
        BackbmpDefined = source?.is_backbmp_defined;
        FilesWarningIgnored = source?.is_files_warning_ignored == true;
        Lr2WarningFlags = source?.lr2_warning_flags ?? 0;
        Lr2ResourceMaxRelativeCp932Bytes = source?.lr2_resource_max_relative_cp932_bytes;
        Lr2ResourceHasParentTraversal = source?.lr2_resource_has_parent_traversal ?? false;
    }

    internal string Path { get; }

    internal string Hash { get; }

    internal string Encoding { get; }

    internal int? WavFilesExisting { get; }
    internal int? WavFilesDefined { get; }
    internal int? BgaFilesExisting { get; }
    internal int? BgaFilesDefined { get; }
    internal int? MovieFilesExisting { get; }
    internal int? MovieFilesDefined { get; }
    internal bool? StagefileExisting { get; }
    internal bool? StagefileDefined { get; }
    internal bool? BannerExisting { get; }
    internal bool? BannerDefined { get; }
    internal bool? BackbmpExisting { get; }
    internal bool? BackbmpDefined { get; }
    internal bool FilesWarningIgnored { get; }
    internal int Lr2WarningFlags { get; }
    internal int? Lr2ResourceMaxRelativeCp932Bytes { get; }
    internal bool Lr2ResourceHasParentTraversal { get; }

    internal static ResourceHealthMaintenanceSnapshot From(BMSFileMaintenanceInfo source)
    {
        return source == null ? null : new ResourceHealthMaintenanceSnapshot(source);
    }

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
        return (int)(100.0 * ((double)existing!.Value - Math.Sqrt(existing.Value)) / defined.Value);
    }

    private static bool? CalculateFlagHealth(bool? defined, bool? existing)
    {
        if (!defined.HasValue || (defined.Value && !existing.HasValue))
        {
            return null;
        }
        return defined == false || (defined.Value && existing!.Value);
    }

    internal BMSFileMaintenanceInfo ToMutable()
    {
        return new BMSFileMaintenanceInfo
        {
            path = Path,
            hash = Hash,
            encoding = Encoding,
            wav_files_existing = WavFilesExisting,
            wav_files_defined = WavFilesDefined,
            bga_files_existing = BgaFilesExisting,
            bga_files_defined = BgaFilesDefined,
            movie_files_existing = MovieFilesExisting,
            movie_files_defined = MovieFilesDefined,
            is_stagefile_existing = StagefileExisting,
            is_stagefile_defined = StagefileDefined,
            is_banner_existing = BannerExisting,
            is_banner_defined = BannerDefined,
            is_backbmp_existing = BackbmpExisting,
            is_backbmp_defined = BackbmpDefined,
            is_files_warning_ignored = FilesWarningIgnored,
            lr2_warning_flags = Lr2WarningFlags,
            lr2_resource_max_relative_cp932_bytes = Lr2ResourceMaxRelativeCp932Bytes,
            lr2_resource_has_parent_traversal = Lr2ResourceHasParentTraversal
        };
    }
}
