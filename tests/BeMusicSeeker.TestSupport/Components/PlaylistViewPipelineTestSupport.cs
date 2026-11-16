using System.Collections;
using System.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PlaylistViewPipelineTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static PlaylistDetailSourceRow CreateSourceRow(string hash, string title, int? mode, string memo = "", string comment = "", double? entryLevel = null, string? sha256 = null)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = hash, Path = title + ".bms", Title = title, RawTitle = title, Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = mode };
        if (sha256 != null)
        {
            file = file with { Sha256 = sha256 };
        }
        var entry = new TestablePlaylistEntry(file)
        {
            memo = memo,
            comment = comment,
            level = entryLevel
        };
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        return new PlaylistDetailSourceRow(entry, (file));
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static PlaylistRequestIdentity CreatePlaylistIdentity(string folderName, long libraryIndexVersion = 3)
    {
        return PlaylistRequestFactory.CreateIdentity(
            new BMSTable(),
            PlaylistDetailSelectionScope.Folder,
            folderName,
            PlaylistDetailFilter.PlaylistFilter,
            keywordFilter: null,
            ChartModeFilter.All,
            sortParameters: null,
            libraryIndexVersion: libraryIndexVersion,
            playlistRevision: 4,
            scoreSnapshotVersion: 5,
            chartInfoIndexVersion: 6,
            hasResolvedSelection: true);
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static PlaylistDetailTerminalRequest CreatePlaylistTerminalRequest(IList rows, int requestVersion)
    {
        PlaylistRequestIdentity identity = CreatePlaylistIdentity("terminal", libraryIndexVersion: 0);
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
        var columnSelection = new MainChartListColumnSelection(
            settings,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.PlaylistFilterSelected,
            System.Windows.Visibility.Collapsed,
            new PlaylistSummaryColumnSettings());
        return new PlaylistDetailTerminalRequest
        {
            BuildRequest = new PlaylistBuildRequest
            {
                RequestVersion = requestVersion,
                Mode = MainViewUpdateMode.PlaylistFilterSelected,
                RequestedMode = MainViewUpdateMode.PlaylistFilterSelected,
                Identity = identity
            },
            ReplaceSource = false,
            CurrentFilterType = identity.FilterType,
            ViewRows = rows,
            ColumnSelection = columnSelection,
            MainRowsRequest = new MainChartListRowsApplyRequest
            {
                Rows = rows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Reset,
                Summary = MainChartListSummaryUpdate.NormalRows(rows),
                Stopwatch = stopwatch
            }
        };
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public TestablePlaylistEntry()
        {
        }

        public TestablePlaylistEntry(ChartFile bmsFile)
            : base(bmsFile)
        {
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetMd5(string value)
        {
            md5 = value;
        }

        public void SetTitle(string value)
        {
            title = value;
        }

        public void SetArtist(string value)
        {
            artist = value;
        }
    }

}
