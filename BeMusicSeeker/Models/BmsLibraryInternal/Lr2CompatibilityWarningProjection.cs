using System.Collections.Generic;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Projects persisted LR2 compatibility facts from maintenance rows into structured chart warnings.
/// </summary>
internal static class Lr2CompatibilityWarningProjection
{
    private const Lr2PathWarningFlags PathEncodingFlags =
        Lr2PathWarningFlags.PathEncodingUnsupported
        | Lr2PathWarningFlags.FolderScanPathEncodingUnsupported;

    private const Lr2PathWarningFlags PathLengthFlags =
        Lr2PathWarningFlags.PathTooLong
        | Lr2PathWarningFlags.FolderScanPathTooLong;

    private const Lr2ResourceWarningFlags ResourceUnsupportedFlags =
        Lr2ResourceWarningFlags.RawPathEncodingUnsupported
        | Lr2ResourceWarningFlags.ResolvedPathEncodingUnsupported
        | Lr2ResourceWarningFlags.ParentTraversalUnsupported;

    private const Lr2ResourceWarningFlags ResourceLengthFlags =
        Lr2ResourceWarningFlags.RawPathTooLong
        | Lr2ResourceWarningFlags.ResolvedPathTooLong;

    internal static void ApplyTo(BMSFile file, BMSFileMaintenanceInfo maintenanceInfo)
    {
        if (file == null || !HasFacts(maintenanceInfo))
        {
            return;
        }
        file.ReplaceWarningsByCategory(ChartWarningCategory.Lr2Compatibility, BuildWarnings(maintenanceInfo));
    }

    internal static IReadOnlyList<ChartWarning> BuildWarnings(BMSFileMaintenanceInfo maintenanceInfo)
    {
        if (!HasFacts(maintenanceInfo))
        {
            return [];
        }

        List<ChartWarning> warnings = [];
        var pathFlags = (Lr2PathWarningFlags)(maintenanceInfo.lr2_path_warning_flags ?? 0);
        if ((pathFlags & PathEncodingFlags) != 0)
        {
            warnings.Add(ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported));
        }
        if ((pathFlags & PathLengthFlags) != 0)
        {
            warnings.Add(ChartWarning.Create(ChartWarningKind.Lr2PathTooLong, Resources.Warning_Lr2PathTooLong));
        }

        var resourceFlags = (Lr2ResourceWarningFlags)(maintenanceInfo.lr2_resource_warning_flags ?? 0);
        if ((resourceFlags & ResourceUnsupportedFlags) != 0)
        {
            warnings.Add(ChartWarning.Create(ChartWarningKind.Lr2ResourcePathUnsupported, Resources.Warning_Lr2ResourcePathUnsupported));
        }
        if ((resourceFlags & ResourceLengthFlags) != 0)
        {
            warnings.Add(ChartWarning.Create(ChartWarningKind.Lr2ResourcePathTooLong, Resources.Warning_Lr2ResourcePathTooLong));
        }
        return warnings;
    }

    private static bool HasFacts(BMSFileMaintenanceInfo maintenanceInfo)
    {
        return maintenanceInfo != null
            && (maintenanceInfo.lr2_path_warning_flags.HasValue
                || maintenanceInfo.lr2_chart_path_cp932_bytes.HasValue
                || maintenanceInfo.lr2_folder_scan_cp932_bytes.HasValue
                || maintenanceInfo.lr2_resource_warning_flags.HasValue
                || maintenanceInfo.lr2_resource_max_raw_cp932_bytes.HasValue
                || maintenanceInfo.lr2_resource_max_resolved_cp932_bytes.HasValue
                || maintenanceInfo.lr2_resource_unsupported_count.HasValue);
    }
}
