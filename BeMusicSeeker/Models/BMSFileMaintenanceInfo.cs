using System;
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
	}
}
