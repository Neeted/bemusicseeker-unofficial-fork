using System;
using BeMusicSeeker.Models.Utils;
using NLog;
using Ribbit.Logging;

namespace BeMusicSeeker.Diagnostics;

/// <summary>
/// Writes aggregate current-.NET performance stages without introducing per-item logging.
/// </summary>
internal static class Net10PerformanceLog
{
    private static readonly Logger logger = NLogWrapper.GetLogger("Performance.Net10");
    private static readonly Net10PerformanceEventWriter writer =
        new(IsLogEnabled, message => logger.Info(message));

    internal static bool IsEnabled => IsLogEnabled();

    internal static void Write(
        in PerformanceInteraction interaction,
        string stage,
        string fields = null)
    {
        writer.Write(interaction, stage, () => fields);
    }

    private static bool IsLogEnabled() =>
        CommandLineSwitches.IsInfoLoggingEnabled
        && logger?.IsInfoEnabled == true;
}

/// <summary>
/// Testable disabled-path boundary used by the current-runtime performance logger.
/// </summary>
internal sealed class Net10PerformanceEventWriter
{
    private readonly Func<bool> isEnabled;
    private readonly Action<string> sink;

    internal Net10PerformanceEventWriter(Func<bool> isEnabled, Action<string> sink)
    {
        this.isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    internal void Write(
        in PerformanceInteraction interaction,
        string stage,
        Func<string> fieldsFactory)
    {
        if (!isEnabled())
        {
            return;
        }
        string fields = fieldsFactory?.Invoke();
        string message = "net10_perf"
            + " route=" + interaction.Route
            + " interactionId=" + interaction.InteractionId
            + " generation=" + interaction.Generation
            + " stage=" + (stage ?? "unknown");
        if (!string.IsNullOrWhiteSpace(fields))
        {
            message += " " + fields;
        }
        sink(message);
    }
}
