using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal sealed class Lr2PlayHistorySchemaStatusPresentation
{
    private Lr2PlayHistorySchemaStatusPresentation(
        Lr2PlayHistorySchemaStatus status,
        string statusText,
        string message,
        bool canInstall,
        bool canRepair)
    {
        Status = status;
        StatusText = statusText ?? string.Empty;
        Message = message ?? string.Empty;
        CanInstall = canInstall;
        CanRepair = canRepair;
    }

    internal Lr2PlayHistorySchemaStatus Status { get; }

    internal string StatusText { get; }

    internal string Message { get; }

    internal bool CanInstall { get; }

    internal bool CanRepair { get; }

    internal static Lr2PlayHistorySchemaStatusPresentation Create(Lr2PlayHistorySchemaCheckResult result)
    {
        if (result == null)
        {
            return new Lr2PlayHistorySchemaStatusPresentation(
                Lr2PlayHistorySchemaStatus.Unreadable,
                Resources.Lr2_play_history_schema_status_unknown,
                string.Empty,
                canInstall: false,
                canRepair: false);
        }

        return new Lr2PlayHistorySchemaStatusPresentation(
            result.Status,
            GetStatusText(result.Status),
            result.Message,
            canInstall: result.Status == Lr2PlayHistorySchemaStatus.NotInstalled,
            canRepair: result.Status == Lr2PlayHistorySchemaStatus.Repairable);
    }

    private static string GetStatusText(Lr2PlayHistorySchemaStatus status)
    {
        return status switch
        {
            Lr2PlayHistorySchemaStatus.Installed => Resources.Lr2_play_history_schema_status_installed,
            Lr2PlayHistorySchemaStatus.NotInstalled => Resources.Lr2_play_history_schema_status_not_installed,
            Lr2PlayHistorySchemaStatus.Repairable => Resources.Lr2_play_history_schema_status_repairable,
            Lr2PlayHistorySchemaStatus.ManualRepairRequired => Resources.Lr2_play_history_schema_status_manual_repair_required,
            Lr2PlayHistorySchemaStatus.Unreadable => Resources.Lr2_play_history_schema_status_unreadable,
            Lr2PlayHistorySchemaStatus.SkippedProfile => Resources.Lr2_play_history_schema_status_skipped_profile,
            _ => Resources.Lr2_play_history_schema_status_unknown,
        };
    }
}
