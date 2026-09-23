using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlayHistoryReadSourceSelectorTests
{
    [TestMethod]
    public void Select_UsesBeatorajaOnlyWhenEveryProviderFactMatches()
    {
        var beatorajaContext = new BeatorajaPlayHistoryScoreContext(
            17,
            new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                ["sha256"] = new BMSScore { hash = "sha256", totalnotes = 100 }
            });

        PlayHistoryReadSourceContext selected = PlayHistoryReadSourceSelector.Select(
            new PlayHistoryReadSourceSelectionInput(
                lr2ScoreDbPath: "lr2.db",
                isLr2LinkedProfile: true,
                useBeatorajaScoreDb: true,
                beatorajaScoreDbPath: "missing-but-observed.db",
                activeScoreSource: ActiveScoreSource.Beatoraja,
                beatorajaScoreDbFileExists: true,
                beatorajaScoreContext: beatorajaContext));

        Assert.AreEqual(PlayHistoryProvider.Beatoraja, selected.Provider);
        Assert.AreEqual("missing-but-observed.db", selected.ScoreDbPath);
        Assert.IsFalse(selected.IsLr2LinkedProfile);
        Assert.AreSame(beatorajaContext, selected.BeatorajaScoreContext);
    }

    [TestMethod]
    public void Select_FallsBackToExistingLr2ContextWhenAnyBeatorajaFactDoesNotMatch()
    {
        var cases = new (string Name, bool UseBeatoraja, ActiveScoreSource ActiveSource, string BeatorajaPath, bool FileExists)[]
        {
            ("setting disabled", false, ActiveScoreSource.Beatoraja, "beatoraja.db", true),
            ("different active source", true, ActiveScoreSource.Lr2, "beatoraja.db", true),
            ("blank path", true, ActiveScoreSource.Beatoraja, " ", true),
            ("file absent", true, ActiveScoreSource.Beatoraja, "beatoraja.db", false)
        };

        foreach ((string name, bool useBeatoraja, ActiveScoreSource activeSource, string beatorajaPath, bool fileExists) in cases)
        {
            PlayHistoryReadSourceContext selected = PlayHistoryReadSourceSelector.Select(
                new PlayHistoryReadSourceSelectionInput(
                    lr2ScoreDbPath: "lr2-profile.db",
                    isLr2LinkedProfile: true,
                    useBeatorajaScoreDb: useBeatoraja,
                    beatorajaScoreDbPath: beatorajaPath,
                    activeScoreSource: activeSource,
                    beatorajaScoreDbFileExists: fileExists,
                    beatorajaScoreContext: new BeatorajaPlayHistoryScoreContext(
                        23,
                        new Dictionary<string, BMSScore>())));

            Assert.AreEqual(PlayHistoryProvider.Lr2, selected.Provider, name);
            Assert.AreEqual("lr2-profile.db", selected.ScoreDbPath, name);
            Assert.IsTrue(selected.IsLr2LinkedProfile, name);
            Assert.AreSame(BeatorajaPlayHistoryScoreContext.Empty, selected.BeatorajaScoreContext, name);
        }
    }

    [TestMethod]
    public void Select_UsesFactsWithoutCheckingThePathItself()
    {
        PlayHistoryReadSourceContext selected = PlayHistoryReadSourceSelector.Select(
            new PlayHistoryReadSourceSelectionInput(
                lr2ScoreDbPath: "lr2.db",
                isLr2LinkedProfile: false,
                useBeatorajaScoreDb: true,
                beatorajaScoreDbPath: "path-that-is-not-created",
                activeScoreSource: ActiveScoreSource.Beatoraja,
                beatorajaScoreDbFileExists: true,
                beatorajaScoreContext: BeatorajaPlayHistoryScoreContext.Empty));

        Assert.AreEqual(PlayHistoryProvider.Beatoraja, selected.Provider);
        Assert.AreEqual("path-that-is-not-created", selected.ScoreDbPath);
    }

    [TestMethod]
    public void Select_NullFactsPreserveLr2FallbackInsteadOfInventingAProvider()
    {
        PlayHistoryReadSourceContext selected = PlayHistoryReadSourceSelector.Select(
            new PlayHistoryReadSourceSelectionInput(
                lr2ScoreDbPath: null,
                isLr2LinkedProfile: true,
                useBeatorajaScoreDb: true,
                beatorajaScoreDbPath: null,
                activeScoreSource: ActiveScoreSource.None,
                beatorajaScoreDbFileExists: true,
                beatorajaScoreContext: null));

        Assert.AreEqual(PlayHistoryProvider.Lr2, selected.Provider);
        Assert.AreEqual(string.Empty, selected.ScoreDbPath);
        Assert.IsTrue(selected.IsLr2LinkedProfile);
        Assert.AreSame(BeatorajaPlayHistoryScoreContext.Empty, selected.BeatorajaScoreContext);
    }

    [TestMethod]
    public void Select_RejectsNullInput()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => PlayHistoryReadSourceSelector.Select(null));
    }
}
