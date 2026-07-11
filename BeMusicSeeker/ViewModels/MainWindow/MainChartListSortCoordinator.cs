using System;
using System.ComponentModel;

namespace BeMusicSeeker.ViewModels;

internal sealed class MainChartListSortCoordinator
{
    private readonly RegularChartListOwner regularChartListOwner;
    private readonly Func<ChartListSortParameters> playHistorySortProvider;
    private readonly Action<ChartListSortParameters> applyRegularSort;
    private readonly Action<ChartListSortParameters> applyPlayHistorySort;
    private readonly Func<bool> isPlayHistoryViewActive;
    private readonly Action<MainViewUpdateMode> refreshChartRowsView;

    internal MainChartListSortCoordinator(
        RegularChartListOwner regularChartListOwner,
        Func<ChartListSortParameters> playHistorySortProvider,
        Action<ChartListSortParameters> applyRegularSort,
        Action<ChartListSortParameters> applyPlayHistorySort,
        Func<bool> isPlayHistoryViewActive,
        Action<MainViewUpdateMode> refreshChartRowsView)
    {
        this.regularChartListOwner = regularChartListOwner ?? throw new ArgumentNullException(nameof(regularChartListOwner));
        this.playHistorySortProvider = playHistorySortProvider ?? throw new ArgumentNullException(nameof(playHistorySortProvider));
        this.applyRegularSort = applyRegularSort ?? throw new ArgumentNullException(nameof(applyRegularSort));
        this.applyPlayHistorySort = applyPlayHistorySort ?? throw new ArgumentNullException(nameof(applyPlayHistorySort));
        this.isPlayHistoryViewActive = isPlayHistoryViewActive ?? throw new ArgumentNullException(nameof(isPlayHistoryViewActive));
        this.refreshChartRowsView = refreshChartRowsView ?? throw new ArgumentNullException(nameof(refreshChartRowsView));
    }

    internal void Apply(MainChartListSortRequestedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        Apply(request.ColumnName, request.Direction, request.Target);
    }

    internal void Apply(
        string columnName,
        ListSortDirection direction,
        MainChartListSortTarget target)
    {
        if (target == MainChartListSortTarget.PlayHistory)
        {
            ChartListSortParameters current = playHistorySortProvider();
            if (current == null || current.ColumnsName != columnName || current.Direction != direction)
            {
                applyPlayHistorySort(new ChartListSortParameters
                {
                    ColumnsName = columnName,
                    Direction = direction
                });
                if (isPlayHistoryViewActive())
                {
                    refreshChartRowsView(MainViewUpdateMode.SortUpdated);
                }
            }
            return;
        }

        if (regularChartListOwner.TryChangeSort(columnName, direction))
        {
            applyRegularSort(new ChartListSortParameters
            {
                ColumnsName = columnName,
                Direction = direction
            });
            if (!isPlayHistoryViewActive())
            {
                refreshChartRowsView(MainViewUpdateMode.SortUpdated);
            }
        }
    }
}
