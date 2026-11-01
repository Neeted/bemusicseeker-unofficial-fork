using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>分割した同機能caseが実際に共用する入力・観測のhelperだけをまとめます。</summary>
internal static class MainChartColumnSettingsTestDoubles
{

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal sealed class FakeMainChartColumnSettingsStore : IMainChartColumnSettingsStore
    {
        internal CustomTableColumnSettings MainColumns { get; } =
            new(CustomTableColumnSettings.ViewKind.STANDARD);

        internal PlaylistSummaryColumnSettings PlaylistSummaryColumns { get; private set; } =
            new();

        internal bool LastReset { get; private set; }

        internal bool LastEnsurePlaylistSummary { get; private set; }

        internal int ResetPlaylistSummaryCallCount { get; private set; }

        public CustomTableColumnSettings GetMain(CustomTableColumnSettings.ViewKind viewKind, bool reset)
        {
            LastReset = reset;
            return MainColumns;
        }

        public bool IsMainReady(CustomTableColumnSettings.ViewKind viewKind)
        {
            return MainColumns != null;
        }

        public PlaylistSummaryColumnSettings GetPlaylistSummary(bool ensureCompatibility)
        {
            LastEnsurePlaylistSummary = ensureCompatibility;
            return PlaylistSummaryColumns;
        }

        public PlaylistSummaryColumnSettings ResetPlaylistSummary()
        {
            ResetPlaylistSummaryCallCount++;
            PlaylistSummaryColumns = new PlaylistSummaryColumnSettings();
            return PlaylistSummaryColumns;
        }
    }

}
