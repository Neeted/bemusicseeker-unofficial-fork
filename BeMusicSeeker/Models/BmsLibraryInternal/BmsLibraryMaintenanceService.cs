using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryMaintenanceService
{
    public bool ApplyNeedToBeFixedWarnings(BMSFile bmsFile, BMSFileMaintenanceInfo maintenanceInfo = null, bool strictCheck = false)
    {
        if (bmsFile == null)
        {
            return false;
        }
        _ = strictCheck;
        bool hasAnyWarning = false;
        maintenanceInfo ??= bmsFile.maintenanceInfo;
        if (!string.IsNullOrWhiteSpace(bmsFile.warning))
        {
            bmsFile.warning = null;
        }
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetWAVHealth(), maintenanceInfo.wav_files_defined, maintenanceInfo.wav_files_existing, Resources.Warning_WavFilesNotFound);
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetBGAHealth(), maintenanceInfo.bga_files_defined, maintenanceInfo.bga_files_existing, Resources.Warning_BgaFilesNotFound);
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetMovieHealth(), maintenanceInfo.movie_files_defined, maintenanceInfo.movie_files_existing, Resources.Warning_MovieFilesNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetStagefileHealth(), Resources.Warning_StagefileNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetBackbmpHealth(), Resources.Warning_BackbmpNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetBannerHealth(), Resources.Warning_BannerNotFound);
        return hasAnyWarning;
    }

    public List<BMSFile> GetGarbledFiles(IEnumerable<BMSFile> bmsFiles, bool isInFixedList)
    {
        return (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => !string.IsNullOrWhiteSpace(file?.maintenanceInfo?.encoding)
                && isInFixedList == file.maintenanceInfo.is_encoding_fixed
                && (file.maintenanceInfo.encoding.EndsWith("?") || file.maintenanceInfo.encoding != "unknown")
                && !file.maintenanceInfo.encoding.StartsWith("shift_jis"))
            .ToList();
    }

    public List<BMSFile> GetZeroNoteFiles(IEnumerable<BMSFile> bmsFiles)
    {
        return (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && file.notes == 0).ToList();
    }

    private static bool AppendHealthWarning(BMSFile bmsFile, int? health, int? defined, int? existing, string warningFormat)
    {
        if (!health.HasValue || health.Value >= 100)
        {
            return false;
        }
        AppendWarningLine(bmsFile, string.Format(warningFormat, health, defined - existing, defined));
        return true;
    }

    private static bool AppendFlagWarning(BMSFile bmsFile, bool? isHealthy, string warningText)
    {
        if (isHealthy != false)
        {
            return false;
        }
        AppendWarningLine(bmsFile, warningText);
        return true;
    }

    private static void AppendWarningLine(BMSFile bmsFile, string warningText)
    {
        if (!string.IsNullOrWhiteSpace(bmsFile.warning))
        {
            bmsFile.warning += Environment.NewLine;
        }
        bmsFile.warning += warningText;
    }
}
