using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>捕捉したLR2互換性の保守事実だけから表示警告を生成します。</summary>
internal static class Lr2CompatibilityWarningProjection
{
    internal static IReadOnlyList<ChartWarning> BuildWarnings(ResourceHealthMaintenanceSnapshot value)
    {
        if (value?.Lr2WarningFlags == null)
        {
            return [];
        }

        List<ChartWarning> warnings = [];
        var flags = (Lr2CompatibilityWarningFlags)value.Lr2WarningFlags.Value;
        Add(Lr2CompatibilityWarningFlags.PathEncodingUnsupported, ChartWarningKind.Lr2PathEncodingUnsupported);
        Add(Lr2CompatibilityWarningFlags.PathTooLong, ChartWarningKind.Lr2PathTooLong);
        Add(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported, ChartWarningKind.Lr2ResourcePathUnsupported);
        Add(Lr2CompatibilityWarningFlags.ResourcePathTooLong, ChartWarningKind.Lr2ResourcePathTooLong);
        return warnings;
        void Add(Lr2CompatibilityWarningFlags flag, ChartWarningKind kind)
        {
            if ((flags & flag) != 0)
            {
                warnings.Add(ChartWarning.Create(kind));
            }
        }
    }
}
