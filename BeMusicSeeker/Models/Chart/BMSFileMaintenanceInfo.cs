using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

public class BMSFileMaintenanceInfo : LR2SongDBExtended.maintenance
{
    private sealed class BulkLoadNotificationScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                if (suppressPropertyChangedDepth > 0)
                {
                    suppressPropertyChangedDepth--;
                }
            }
        }
    }

    [ThreadStatic]
    private static int suppressPropertyChangedDepth;

    private static bool IsPropertyChangedSuppressed => suppressPropertyChangedDepth > 0;

    public static IDisposable SuppressPropertyChangedScope()
    {
        suppressPropertyChangedDepth++;
        return new BulkLoadNotificationScope();
    }

    public override string encoding
    {
        get
        {
            return base.encoding;
        }
        set
        {
            base.encoding = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged("encoding");
            }
        }
    }

    public override int? wav_files_existing
    {
        get
        {
            return base.wav_files_existing;
        }
        set
        {
            base.wav_files_existing = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => WAVHealth);
            }
        }
    }

    public override int? wav_files_defined
    {
        get
        {
            return base.wav_files_defined;
        }
        set
        {
            base.wav_files_defined = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => WAVHealth);
            }
        }
    }

    public override int? bga_files_existing
    {
        get
        {
            return base.bga_files_existing;
        }
        set
        {
            base.bga_files_existing = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => BGAHealth);
            }
        }
    }

    public override int? bga_files_defined
    {
        get
        {
            return base.bga_files_defined;
        }
        set
        {
            base.bga_files_defined = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => BGAHealth);
            }
        }
    }

    public override int? movie_files_existing
    {
        get
        {
            return base.movie_files_existing;
        }
        set
        {
            base.movie_files_existing = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => MovieHealth);
            }
        }
    }

    public override int? movie_files_defined
    {
        get
        {
            return base.movie_files_defined;
        }
        set
        {
            base.movie_files_defined = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => MovieHealth);
            }
        }
    }

    public override bool? is_stagefile_existing
    {
        get
        {
            return base.is_stagefile_existing;
        }
        set
        {
            base.is_stagefile_existing = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => StagefileHealth);
            }
        }
    }

    public override bool? is_stagefile_defined
    {
        get
        {
            return base.is_stagefile_defined;
        }
        set
        {
            base.is_stagefile_defined = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => StagefileHealth);
            }
        }
    }

    public override bool? is_banner_existing
    {
        get
        {
            return base.is_banner_existing;
        }
        set
        {
            base.is_banner_existing = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => BannerHealth);
            }
        }
    }

    public override bool? is_banner_defined
    {
        get
        {
            return base.is_banner_defined;
        }
        set
        {
            base.is_banner_defined = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => BannerHealth);
            }
        }
    }

    public override bool? is_backbmp_existing
    {
        get
        {
            return base.is_backbmp_existing;
        }
        set
        {
            base.is_backbmp_existing = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => BackbmpHealth);
            }
        }
    }

    public override bool? is_backbmp_defined
    {
        get
        {
            return base.is_backbmp_defined;
        }
        set
        {
            base.is_backbmp_defined = value;
            if (!IsPropertyChangedSuppressed)
            {
                RaisePropertyChanged(() => BackbmpHealth);
            }
        }
    }

    public int? WAVHealth => GetWAVHealth();

    public int? BGAHealth => GetBGAHealth();

    public int? MovieHealth => GetMovieHealth();

    public bool? StagefileHealth => GetStagefileHealth();

    public bool? BannerHealth => GetBannerHealth();

    public bool? BackbmpHealth => GetBackbmpHealth();

    public int? GetWAVHealth()
    {
        if (!wav_files_defined.HasValue || (wav_files_defined > 0 && !wav_files_existing.HasValue))
        {
            return null;
        }
        if (wav_files_defined == 0 || wav_files_existing == wav_files_defined)
        {
            return 100;
        }
        return (int)(100.0 * ((double)wav_files_existing.Value - Math.Sqrt(wav_files_existing.Value)) / (double)wav_files_defined.Value);
    }

    public int? GetBGAHealth()
    {
        if (!bga_files_defined.HasValue || (bga_files_defined > 0 && !bga_files_existing.HasValue))
        {
            return null;
        }
        if (bga_files_defined == 0 || bga_files_existing == bga_files_defined)
        {
            return 100;
        }
        return (int)(100.0 * ((double)bga_files_existing.Value - Math.Sqrt(bga_files_existing.Value)) / (double)bga_files_defined.Value);
    }

    public int? GetMovieHealth()
    {
        if (!movie_files_defined.HasValue || (movie_files_defined > 0 && !movie_files_existing.HasValue))
        {
            return null;
        }
        if (movie_files_defined == 0 || movie_files_existing == movie_files_defined)
        {
            return 100;
        }
        return (int)(100.0 * ((double)movie_files_existing.Value - Math.Sqrt(movie_files_existing.Value)) / (double)movie_files_defined.Value);
    }

    public bool? GetOptIMGHealth()
    {
        if (!GetStagefileHealth().HasValue || !GetBannerHealth().HasValue || !GetBackbmpHealth().HasValue)
        {
            return null;
        }
        return GetStagefileHealth().Value && GetBannerHealth().Value && GetBackbmpHealth().Value;
    }

    public bool? GetStagefileHealth()
    {
        if (!is_stagefile_defined.HasValue || (is_stagefile_defined.Value && !is_stagefile_existing.HasValue))
        {
            return null;
        }
        return is_stagefile_defined == false || (is_stagefile_defined.Value && is_stagefile_existing.Value);
    }

    public bool? GetBannerHealth()
    {
        if (!is_banner_defined.HasValue || (is_banner_defined.Value && !is_banner_existing.HasValue))
        {
            return null;
        }
        return is_banner_defined == false || (is_banner_defined.Value && is_banner_existing.Value);
    }

    public bool? GetBackbmpHealth()
    {
        if (!is_backbmp_defined.HasValue || (is_backbmp_defined.Value && !is_backbmp_existing.HasValue))
        {
            return null;
        }
        return is_backbmp_defined == false || (is_backbmp_defined.Value && is_backbmp_existing.Value);
    }

    public bool IsInformationChecked()
    {
        if (GetWAVHealth().HasValue && GetBGAHealth().HasValue && GetMovieHealth().HasValue)
        {
            return GetOptIMGHealth().HasValue;
        }
        return false;
    }

    public BMSFileMaintenanceInfo()
    {
    }

    public BMSFileMaintenanceInfo(LR2SongDB.song song)
    {
        base.hash = song.hash;
        base.path = song.path;
        if (!string.IsNullOrWhiteSpace(song.stagefile))
        {
            is_stagefile_defined = true;
        }
        if (!string.IsNullOrWhiteSpace(song.banner))
        {
            is_banner_defined = true;
        }
        if (!string.IsNullOrWhiteSpace(song.backbmp))
        {
            is_backbmp_defined = true;
        }
    }

    public static BMSFileMaintenanceInfo CreateForBmson(string path, string md5)
    {
        return new BMSFileMaintenanceInfo
        {
            hash = md5,
            path = path,
            encoding = "utf-8",
            is_encoding_fixed = false
        };
    }

    public void NormalizeForBmson(string path, string md5)
    {
        base.path = path;
        base.hash = md5;
        encoding = "utf-8";
        is_encoding_fixed = false;
        lr2_warning_flags = null;
        lr2_resource_max_relative_cp932_bytes = null;
        lr2_resource_has_parent_traversal = null;
    }

    internal BMSFileMaintenanceInfo CreatePersistenceCopy(string nextPath = null, string nextHash = null)
    {
        return new BMSFileMaintenanceInfo
        {
            hash = nextHash ?? hash,
            path = nextPath ?? path,
            encoding = encoding,
            is_encoding_fixed = is_encoding_fixed,
            wav_files_existing = wav_files_existing,
            wav_files_defined = wav_files_defined,
            bga_files_existing = bga_files_existing,
            bga_files_defined = bga_files_defined,
            movie_files_existing = movie_files_existing,
            movie_files_defined = movie_files_defined,
            is_stagefile_existing = is_stagefile_existing,
            is_stagefile_defined = is_stagefile_defined,
            is_banner_existing = is_banner_existing,
            is_banner_defined = is_banner_defined,
            is_backbmp_existing = is_backbmp_existing,
            is_backbmp_defined = is_backbmp_defined,
            is_files_warning_ignored = is_files_warning_ignored,
            lr2_warning_flags = lr2_warning_flags,
            lr2_resource_max_relative_cp932_bytes = lr2_resource_max_relative_cp932_bytes,
            lr2_resource_has_parent_traversal = lr2_resource_has_parent_traversal
        };
    }

    internal void ApplyPersistenceCopyFrom(BMSFileMaintenanceInfo source)
    {
        if (source == null)
        {
            return;
        }
        hash = source.hash;
        path = source.path;
        encoding = source.encoding;
        is_encoding_fixed = source.is_encoding_fixed;
        wav_files_existing = source.wav_files_existing;
        wav_files_defined = source.wav_files_defined;
        bga_files_existing = source.bga_files_existing;
        bga_files_defined = source.bga_files_defined;
        movie_files_existing = source.movie_files_existing;
        movie_files_defined = source.movie_files_defined;
        is_stagefile_existing = source.is_stagefile_existing;
        is_stagefile_defined = source.is_stagefile_defined;
        is_banner_existing = source.is_banner_existing;
        is_banner_defined = source.is_banner_defined;
        is_backbmp_existing = source.is_backbmp_existing;
        is_backbmp_defined = source.is_backbmp_defined;
        is_files_warning_ignored = source.is_files_warning_ignored;
        lr2_warning_flags = source.lr2_warning_flags;
        lr2_resource_max_relative_cp932_bytes = source.lr2_resource_max_relative_cp932_bytes;
        lr2_resource_has_parent_traversal = source.lr2_resource_has_parent_traversal;
    }

    internal void ApplyLr2CompatibilityEvaluation(
        Lr2ChartPathEvaluation pathEvaluation,
        Lr2ResourceReferenceEvaluation resourceEvaluation)
    {
        lr2_warning_flags = (int)(pathEvaluation.WarningFlags | resourceEvaluation.WarningFlags);
        lr2_resource_max_relative_cp932_bytes = resourceEvaluation.MaxRelativeCp932Bytes;
        lr2_resource_has_parent_traversal = resourceEvaluation.HasParentTraversal;
    }

    internal void ApplyLr2CompatibilityFactsFrom(BMSFileMaintenanceInfo source)
    {
        if (source == null)
        {
            return;
        }

        lr2_warning_flags = source.lr2_warning_flags;
        lr2_resource_max_relative_cp932_bytes = source.lr2_resource_max_relative_cp932_bytes;
        lr2_resource_has_parent_traversal = source.lr2_resource_has_parent_traversal;
    }

    internal bool HasSameLr2CompatibilityFacts(BMSFileMaintenanceInfo source)
    {
        if (source == null)
        {
            return false;
        }

        return lr2_warning_flags == source.lr2_warning_flags
            && lr2_resource_max_relative_cp932_bytes == source.lr2_resource_max_relative_cp932_bytes
            && lr2_resource_has_parent_traversal == source.lr2_resource_has_parent_traversal;
    }
}
