using System;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the chart-list filter values entered by the main window.
/// </summary>
public sealed class ChartListFilterViewModel : ViewModel
{
    private readonly object syncRoot = new();

    private ChartModeFilter modeFilter = ChartModeFilter.All;

    private string keywordFilter;

    /// <summary>
    /// Raised after the mode filter has changed so the shell can coordinate feature workflows.
    /// </summary>
    internal event EventHandler ModeFilterChanged;

    /// <summary>
    /// Raised after the keyword filter has changed so the shell can coordinate feature workflows.
    /// </summary>
    internal event EventHandler KeywordFilterChanged;

    /// <summary>
    /// Gets or sets the selected chart-key flags.
    /// </summary>
    public ChartModeFilter ModeFilter
    {
        get
        {
            lock (syncRoot)
            {
                return modeFilter;
            }
        }
        set
        {
            bool rejectedNone = false;
            lock (syncRoot)
            {
                if (value == ChartModeFilter.None)
                {
                    rejectedNone = true;
                }
                else if (modeFilter == value)
                {
                    return;
                }
                else
                {
                    modeFilter = value;
                }
            }
            if (rejectedNone)
            {
                RaisePropertyChanged(nameof(ModeFilter));
                return;
            }
            RaisePropertyChanged(nameof(ModeFilter));
            ModeFilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets the keyword expression applied to the active chart workflow.
    /// </summary>
    public string KeywordFilter
    {
        get
        {
            lock (syncRoot)
            {
                return keywordFilter;
            }
        }
        set
        {
            lock (syncRoot)
            {
                if (keywordFilter == value)
                {
                    return;
                }
                keywordFilter = value;
            }
            RaisePropertyChanged(nameof(KeywordFilter));
            KeywordFilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Captures the raw values used to construct one chart workflow request.
    /// </summary>
    internal ChartListFilterSnapshot CaptureSnapshot()
    {
        lock (syncRoot)
        {
            return new ChartListFilterSnapshot(keywordFilter, modeFilter);
        }
    }
}

/// <summary>
/// Immutable chart filter values carried across a workflow boundary.
/// </summary>
internal sealed class ChartListFilterSnapshot
{
    internal ChartListFilterSnapshot(string keywordFilter, ChartModeFilter modeFilter)
    {
        KeywordFilter = keywordFilter;
        ModeFilter = modeFilter;
    }

    internal static ChartListFilterSnapshot Default { get; } = new(null, ChartModeFilter.All);

    internal string KeywordFilter { get; }

    internal ChartModeFilter ModeFilter { get; }
}
