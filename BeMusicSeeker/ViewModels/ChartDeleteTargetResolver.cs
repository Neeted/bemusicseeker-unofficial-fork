using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal enum ChartDeleteRoute
{
    None,
    Pending,
    Library
}

internal sealed class ChartDeleteTargetResolution
{
    internal ChartDeleteRoute Route { get; set; }

    internal List<ChartOperationTarget> Targets { get; } = [];

    internal bool UsedContextFallback { get; set; }

    internal int SelectedInputCount { get; set; }

    internal int MixedScopeDroppedCount { get; set; }

    internal ChartOperationSourceScope? ContextScope { get; set; }
}

internal static class ChartDeleteTargetResolver
{
    internal static ChartDeleteTargetResolution Resolve(
        IEnumerable<ChartOperationTarget> selectedTargets,
        ChartOperationTarget contextTarget,
        MainViewOperationSection currentSection)
    {
        List<ChartOperationTarget> selected = [.. (selectedTargets ?? []).Where(IsDeleteCandidate)];
        var result = new ChartDeleteTargetResolution
        {
            SelectedInputCount = selected.Count,
            ContextScope = contextTarget?.SourceScope
        };

        List<ChartOperationTarget> candidates = selected;
        if (candidates.Count == 0 && IsDeleteCandidate(contextTarget))
        {
            candidates = [contextTarget];
            result.UsedContextFallback = true;
        }

        result.Route = ResolveRoute(candidates, contextTarget, currentSection);
        if (result.Route == ChartDeleteRoute.None)
        {
            return result;
        }

        foreach (ChartOperationTarget target in candidates)
        {
            if (MatchesRoute(target, result.Route))
            {
                result.Targets.Add(target);
            }
            else
            {
                result.MixedScopeDroppedCount++;
            }
        }

        if (result.Targets.Count == 0)
        {
            result.Route = ChartDeleteRoute.None;
        }
        return result;
    }

    private static ChartDeleteRoute ResolveRoute(
        List<ChartOperationTarget> candidates,
        ChartOperationTarget contextTarget,
        MainViewOperationSection currentSection)
    {
        if (IsPendingDeleteTarget(contextTarget))
        {
            return ChartDeleteRoute.Pending;
        }
        if (IsLibraryDeleteTarget(contextTarget))
        {
            return ChartDeleteRoute.Library;
        }
        if (currentSection == MainViewOperationSection.InstallPending
            && candidates.Any(IsPendingDeleteTarget))
        {
            return ChartDeleteRoute.Pending;
        }
        if (candidates.Any(IsLibraryDeleteTarget))
        {
            return ChartDeleteRoute.Library;
        }
        return candidates.Any(IsPendingDeleteTarget) ? ChartDeleteRoute.Pending : ChartDeleteRoute.None;
    }

    private static bool MatchesRoute(ChartOperationTarget target, ChartDeleteRoute route)
    {
        return route switch
        {
            ChartDeleteRoute.Pending => IsPendingDeleteTarget(target),
            ChartDeleteRoute.Library => IsLibraryDeleteTarget(target),
            _ => false
        };
    }

    private static bool IsDeleteCandidate(ChartOperationTarget target)
    {
        return IsPendingDeleteTarget(target) || IsLibraryDeleteTarget(target);
    }

    private static bool IsPendingDeleteTarget(ChartOperationTarget target)
    {
        return target != null
            && (target.IsPending || target.SourceScope == ChartOperationSourceScope.PendingPackage)
            && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination);
    }

    private static bool IsLibraryDeleteTarget(ChartOperationTarget target)
    {
        return target != null
            && !target.IsPending
            && target.SourceScope != ChartOperationSourceScope.PendingPackage
            && target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary);
    }
}
