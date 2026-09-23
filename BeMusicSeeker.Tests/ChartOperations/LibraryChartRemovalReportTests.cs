using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>FSDB-C6 deletion-specific mapping; the general bounded renderer matrix remains in A.</summary>
[TestClass]
public sealed class LibraryChartRemovalReportTests
{
    [TestMethod]
    public void NormalDeletionIsSilentAndEveryUnconfirmedStateIsAnError()
    {
        var normal = new LibraryChartRemovalOutcome([new("confirmed.bms", LibraryChartRemovalState.Confirmed)], true, true);
        Assert.IsNull(LibraryChartRemovalReport.Create(normal));
        foreach (LibraryChartRemovalState state in Enum.GetValues<LibraryChartRemovalState>().Where(state => state != LibraryChartRemovalState.Confirmed))
        {
            var outcome = new LibraryChartRemovalOutcome([new("check-this.bms", state)], true, true);
            UiMessageRequest request = LibraryChartRemovalReport.Create(outcome);
            Assert.IsNotNull(request, state.ToString());
            Assert.AreEqual(System.Windows.MessageBoxImage.Error, request.Icon);
            StringAssert.Contains(request.MessageBoxText, "check-this.bms");
        }
    }

    [TestMethod]
    public async Task MixedFactsStayIntactAcrossBoundedLocalizationAndReporterFailure()
    {
        var fsFailure = new IOException(new string('f', 900));
        var catalogFailure = new IOException("catalog failure", new IOException("inner primary"));
        LibraryChartRemovalTarget[] targets = [
            new("confirmed.bms", LibraryChartRemovalState.Confirmed),
            new("missing.bms", LibraryChartRemovalState.NotExecuted),
            new(new string('p', 500), LibraryChartRemovalState.Unconfirmed, fsFailure),
            new("omitted.bms", LibraryChartRemovalState.Unconfirmed, fsFailure)];
        var outcome = new LibraryChartRemovalOutcome(targets, true, true, catalogFailure);
        foreach (string culture in new[] { "ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW" })
        {
            UiMessageRequest request = LibraryChartRemovalReport.Create(outcome, culture: CultureInfo.GetCultureInfo(culture));
            Assert.IsNotNull(request);
            Assert.AreEqual(System.Windows.MessageBoxImage.Error, request.Icon);
            Assert.IsTrue(request.MessageBoxText.Length <= 4096);
            Assert.IsFalse(request.MessageBoxText.Contains(new string('p', 241), StringComparison.Ordinal));
            Assert.IsFalse(request.MessageBoxText.Contains(new string('f', 401), StringComparison.Ordinal));
            Assert.IsFalse(request.MessageBoxText.Contains("omitted.bms", StringComparison.Ordinal));
        }
        var dialogs = new FileDbReportRecordingDialogs { MessageFailure = new IOException("report failure") };
        await LibraryChartRemovalReport.ShowAsync(dialogs, outcome);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(4, outcome.Targets.Count);
        Assert.AreEqual(1, outcome.ConfirmedChartCount);
        Assert.IsTrue(outcome.RequiredFinalizationFailed);
        Assert.AreSame(catalogFailure, outcome.CatalogFailure);
        Assert.AreSame(fsFailure, outcome.Targets[2].Failure);
    }
}
