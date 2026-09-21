using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Captures UI-independent row selection inputs for chart operation target resolution.
/// </summary>
internal sealed class ChartOperationTargetSelectionRequest
{
    /// <summary>
    /// Initializes a new selected chart operation target request.
    /// </summary>
    internal ChartOperationTargetSelectionRequest(
        IEnumerable<object> rows,
        ChartOperationSourceScope sourceScope,
        ChartOperationCapabilities requiredCapability = ChartOperationCapabilities.None)
    {
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        SourceScope = sourceScope;
        RequiredCapability = requiredCapability;
    }

    internal IEnumerable<object> Rows { get; }

    internal ChartOperationSourceScope SourceScope { get; }

    internal ChartOperationCapabilities RequiredCapability { get; }
}
