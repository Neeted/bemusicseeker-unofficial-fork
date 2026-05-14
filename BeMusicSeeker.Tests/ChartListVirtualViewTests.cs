using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartListVirtualViewTests
{
    [TestMethod]
    public void Count_DoesNotRealizeRowsUntilIndexed()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);

        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(3, view.RowCount);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, getCreatedCount());

        LibraryChartRow row = (LibraryChartRow)view[0];

        Assert.AreEqual("Alpha", row.Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    [TestMethod]
    public void SameIndex_ReturnsCachedRow()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);

        object first = view[1];
        object second = view[1];

        Assert.AreSame(first, second);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    [TestMethod]
    public void IndexOf_DoesNotRealizeUnvisitedRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        object realized = view[1];

        Assert.AreEqual(1, view.IndexOf(realized));
        Assert.AreEqual(-1, view.IndexOf(new object()));
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    [TestMethod]
    public void SummaryConverter_UsesMetadataWithoutEnumeratingRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        BMSFilesViewToSummaryTextConverter converter = new BMSFilesViewToSummaryTextConverter();

        object text = converter.Convert(view, typeof(string), null, CultureInfo.InvariantCulture);

        StringAssert.StartsWith(text.ToString(), "[3");
        Assert.AreEqual(2, view.DistinctFolderCount);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, getCreatedCount());
    }

    [TestMethod]
    public void DisposeRealizedRows_DoesNotRealizeUnvisitedRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        _ = view[2];

        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());

        view.DisposeRealizedRows();

        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());

        _ = view[2];

        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(2, getCreatedCount());
    }

    [TestMethod]
    public void TitleOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        List<BMSFile> files = new List<BMSFile>
        {
            CreateFile(@"folder-a\z_item10.bms", "item10", "folder-a"),
            CreateFile(@"folder-a\a_item2.bms", "item2", "folder-a"),
            CreateFile(@"folder-b\m_alpha.bms", "Alpha", "folder-b")
        };
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, null);
        ChartListOrder order = ChartListOrder.CreateTitleAscending(sourceRows);
        ChartListVirtualView view = new ChartListVirtualView(
            sourceRows,
            order,
            row => LibraryChartRow.FromBmsFile(row.BmsFile));
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Title),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> legacySorted = LibraryChartRowSortEngine.SortForMainView(
            files.Select(LibraryChartRow.FromBmsFile),
            sortParameters,
            isPlaylistDetailView: false,
            useLegacySortForDataGrid: false,
            out _);

        CollectionAssert.AreEqual(
            legacySorted.Select(row => row.path).ToArray(),
            Enumerable.Range(0, view.Count).Select(index => ((LibraryChartRow)view[index]).path).ToArray());
    }

    private static ChartListVirtualView CreateView(out Func<int> getCreatedCount)
    {
        List<BMSFile> files = new List<BMSFile>
        {
            CreateFile(@"folder-b\charlie.bms", "Charlie", "folder-b"),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a"),
            CreateFile(@"folder-a\bravo.bms", "Bravo", "folder-a")
        };
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, null);
        ChartListOrder order = ChartListOrder.CreateTitleAscending(sourceRows);
        int localCreatedCount = 0;
        ChartListVirtualView view = new ChartListVirtualView(
            sourceRows,
            order,
            row =>
            {
                localCreatedCount++;
                return LibraryChartRow.FromBmsFile(row.BmsFile);
            });
        getCreatedCount = () => localCreatedCount;
        return view;
    }

    private static BMSFile CreateFile(string path, string title, string folder)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.Apply(path, title, folder);
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void Apply(string filePath, string fileTitle, string folderName)
        {
            path = filePath;
            title = fileTitle;
            folder = folderName;
            hash = "0123456789abcdef0123456789abcdef";
        }
    }
}
