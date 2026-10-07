using System.Collections.Generic;
using BeMusicSeeker.Properties;

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
        Add(Lr2CompatibilityWarningFlags.PathEncodingUnsupported, ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported);
        Add(Lr2CompatibilityWarningFlags.PathTooLong, ChartWarningKind.Lr2PathTooLong, Resources.Warning_Lr2PathTooLong);
        Add(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported, ChartWarningKind.Lr2ResourcePathUnsupported, Resources.Warning_Lr2ResourcePathUnsupported);
        Add(Lr2CompatibilityWarningFlags.ResourcePathTooLong, ChartWarningKind.Lr2ResourcePathTooLong, Resources.Warning_Lr2ResourcePathTooLong);
        return warnings;
        void Add(Lr2CompatibilityWarningFlags flag, ChartWarningKind kind, string text)
        {
            if ((flags & flag) != 0)
            {
                warnings.Add(ChartWarning.Create(kind, text));
            }
        }
    }
}
