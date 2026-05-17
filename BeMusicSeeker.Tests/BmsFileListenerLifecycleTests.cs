using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsFileListenerLifecycleTests
{
    [TestMethod]
    public void SetMaintenanceInfo_ReplacesPreviousListener()
    {
        TestableBmsFile? file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BMSFileMaintenanceInfo info1 = CreateMaintenanceInfo(file, "shift_jis");
        BMSFileMaintenanceInfo info2 = CreateMaintenanceInfo(file, "utf-8");
        int encodingChangedCount = 0;
        file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BMSFile.encoding))
            {
                encodingChangedCount++;
            }
        };

        file.SetMaintenanceInfo(info1, suppressPropertyChanged: true, registerEventHandlers: true);
        file.SetMaintenanceInfo(info2, suppressPropertyChanged: true, registerEventHandlers: true);
        encodingChangedCount = 0;

        info1.encoding = "euc-jp";
        info2.encoding = "utf-16";

        Assert.AreEqual(1, encodingChangedCount);
        Assert.AreEqual("utf-16", file.encoding);
    }

    [TestMethod]
    public void BmsScoreSetter_ReplacesPreviousListener()
    {
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BMSScore score1 = CreateScore(file.hash);
        BMSScore score2 = CreateScore(file.hash);
        int rankingChangedCount = 0;
        file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BMSFile.ranking))
            {
                rankingChangedCount++;
            }
        };

        file.bmsScore = score1;
        file.bmsScore = score2;
        rankingChangedCount = 0;

        score1.ranking = 1;
        score2.ranking = 2;

        Assert.AreEqual(1, rankingChangedCount);
        Assert.AreEqual(2, file.ranking);
    }

    [TestMethod]
    public void RemoveRefTable_UpdatesReferenceDisplay()
    {
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var table = new BMSTable
        {
            name = "Before",
            symbol = "A"
        };
        int symbolsChangedCount = 0;
        int namesChangedCount = 0;
        file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BMSFile.RefTablesSymbols))
            {
                symbolsChangedCount++;
            }
            if (e.PropertyName == nameof(BMSFile.RefTablesNames))
            {
                namesChangedCount++;
            }
        };

        file.AddRefTable(table);
        Assert.AreEqual("A", file.RefTablesSymbols);
        Assert.AreEqual("Before", file.RefTablesNames);

        file.RemoveRefTable(table);

        Assert.AreEqual(2, symbolsChangedCount);
        Assert.AreEqual(2, namesChangedCount);
        Assert.AreEqual(0, file.RefTables.Count);
        Assert.AreEqual(string.Empty, file.RefTablesSymbols);
        Assert.IsNull(file.RefTablesNames);
    }

    [TestMethod]
    public void RefreshRefTablesDisplayCache_UpdatesAfterTableRename()
    {
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var table = new BMSTable
        {
            name = "Before",
            symbol = "A"
        };
        int symbolsChangedCount = 0;
        int namesChangedCount = 0;
        file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BMSFile.RefTablesSymbols))
            {
                symbolsChangedCount++;
            }
            if (e.PropertyName == nameof(BMSFile.RefTablesNames))
            {
                namesChangedCount++;
            }
        };

        file.AddRefTable(table);
        symbolsChangedCount = 0;
        namesChangedCount = 0;

        table.symbol = "B";
        table.name = "After";

        Assert.AreEqual("A", file.RefTablesSymbols);
        Assert.AreEqual("Before", file.RefTablesNames);
        Assert.AreEqual(0, symbolsChangedCount);
        Assert.AreEqual(0, namesChangedCount);

        file.RefreshRefTablesDisplayCache();

        Assert.AreEqual("B", file.RefTablesSymbols);
        Assert.AreEqual("After", file.RefTablesNames);
        Assert.AreEqual(1, symbolsChangedCount);
        Assert.AreEqual(1, namesChangedCount);
    }

    [TestMethod]
    public void ReleaseTransientListeners_AllowsFileToBeCollectedWhileRootsRemainAlive()
    {
        WeakReference weakReference = CreateWeakReferenceAfterRelease(out BMSTable table, out BMSScore score, out BMSFileMaintenanceInfo info);

        ForceGc();

        GC.KeepAlive(table);
        GC.KeepAlive(score);
        GC.KeepAlive(info);
        Assert.IsFalse(weakReference.IsAlive);
    }

    [TestMethod]
    public void AddRefTable_DoesNotKeepFileAliveWhileTableRemainsAlive()
    {
        var table = new BMSTable
        {
            name = "Table",
            symbol = "T"
        };
        WeakReference weakReference = CreateWeakReferenceAfterAddingRefTable(table);

        ForceGc();

        GC.KeepAlive(table);
        Assert.IsFalse(weakReference.IsAlive);
    }

    private static WeakReference CreateWeakReferenceAfterRelease(out BMSTable table, out BMSScore score, out BMSFileMaintenanceInfo info)
    {
        TestableBmsFile? file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        table = new BMSTable
        {
            name = "Table",
            symbol = "T"
        };
        score = CreateScore(file.hash);
        info = CreateMaintenanceInfo(file, "shift_jis");

        file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: true);
        file.bmsScore = score;
        file.AddRefTable(table);
        file.ReleaseTransientListeners(clearRefTables: true);

        Assert.AreEqual(0, file.RefTables.Count);
        var weakReference = new WeakReference(file);
        file = null;
        return weakReference;
    }

    private static WeakReference CreateWeakReferenceAfterAddingRefTable(BMSTable table)
    {
        TestableBmsFile? file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.AddRefTable(table);
        var weakReference = new WeakReference(file);
        file = null;
        return weakReference;
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static TestableBmsFile CreateFile(string hash)
    {
        var file = new TestableBmsFile();
        file.SetHash(hash);
        file.path = @"C:\Library\chart.bms";
        return file;
    }

    private static BMSFileMaintenanceInfo CreateMaintenanceInfo(BMSFile file, string encoding)
    {
        return new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            encoding = encoding
        };
    }

    private static BMSScore CreateScore(string hash)
    {
        return new BMSScore
        {
            hash = hash
        };
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }
}
