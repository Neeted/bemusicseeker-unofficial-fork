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
        int scoreChangedCount = 0;
        file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BMSFile.bmsScore))
            {
                scoreChangedCount++;
            }
        };

        file.bmsScore = score1;
        file.bmsScore = score2;
        scoreChangedCount = 0;

        score1.ranking = 1;
        score2.ranking = 2;

        Assert.AreEqual(1, scoreChangedCount);
        Assert.AreEqual(2, file.bmsScore.ranking);
    }

    [TestMethod]
    public void ReleaseTransientListeners_AllowsFileToBeCollectedWhileRootsRemainAlive()
    {
        WeakReference weakReference = CreateWeakReferenceAfterRelease(out BMSScore score, out BMSFileMaintenanceInfo info);

        ForceGc();

        GC.KeepAlive(score);
        GC.KeepAlive(info);
        Assert.IsFalse(weakReference.IsAlive);
    }

    private static WeakReference CreateWeakReferenceAfterRelease(out BMSScore score, out BMSFileMaintenanceInfo info)
    {
        TestableBmsFile? file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        score = CreateScore(file.hash);
        info = CreateMaintenanceInfo(file, "shift_jis");

        file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: true);
        file.bmsScore = score;
        file.ReleaseTransientListeners();

        var weakReference = new WeakReference(file);
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
