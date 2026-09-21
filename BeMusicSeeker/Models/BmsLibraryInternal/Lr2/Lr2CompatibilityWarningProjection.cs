using System.Collections.Generic;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Projects persisted LR2 compatibility facts from maintenance rows into structured chart warnings.
/// </summary>
internal static class Lr2CompatibilityWarningProjection
{
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
        var flags = (Lr2CompatibilityWarningFlags)(maintenanceInfo.lr2_warning_flags ?? 0);
        if ((flags & Lr2CompatibilityWarningFlags.PathEncodingUnsupported) != 0)
        {
            warnings.Add(ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported));
        }
        if ((flags & Lr2CompatibilityWarningFlags.PathTooLong) != 0)
        {
            warnings.Add(ChartWarning.Create(ChartWarningKind.Lr2PathTooLong, Resources.Warning_Lr2PathTooLong));
        }

        if ((flags & Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported) != 0)
        {
            warnings.Add(ChartWarning.Create(ChartWarningKind.Lr2ResourcePathUnsupported, Resources.Warning_Lr2ResourcePathUnsupported));
        }
        if ((flags & Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0)
        {
            warnings.Add(ChartWarning.Create(ChartWarningKind.Lr2ResourcePathTooLong, Resources.Warning_Lr2ResourcePathTooLong));
        }
        return warnings;
    }

    private static bool HasFacts(BMSFileMaintenanceInfo maintenanceInfo)
    {
        return maintenanceInfo != null
            && maintenanceInfo.lr2_warning_flags.HasValue;
    }
}
