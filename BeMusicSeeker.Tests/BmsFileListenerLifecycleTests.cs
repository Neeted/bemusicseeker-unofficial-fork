using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsFileListenerLifecycleTests
{
    [TestMethod]
    public void NotifyMaintenanceInfoChanged_RaisesMaintenanceInfoProperty()
    {
        TestableBmsFile? file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BMSFileMaintenanceInfo info = CreateMaintenanceInfo(file, "shift_jis");
        int maintenanceChangedCount = 0;
        file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BMSFile.maintenanceInfo))
            {
                maintenanceChangedCount++;
            }
        };

        file.SetMaintenanceInfo(info, suppressPropertyChanged: true);
        maintenanceChangedCount = 0;

        info.encoding = "utf-16";
        file.NotifyMaintenanceInfoChanged(encodingChanged: true, healthChanged: false);

        Assert.AreEqual(1, maintenanceChangedCount);
        Assert.AreEqual("utf-16", file.maintenanceInfo.encoding);
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
