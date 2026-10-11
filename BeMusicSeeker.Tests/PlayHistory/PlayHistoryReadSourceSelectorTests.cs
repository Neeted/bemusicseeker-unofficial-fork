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
    /// <summary>
    /// 利用設定・実選択元・パス・存在情報が揃った時だけbeatorajaを採用し、LR2連携の混在を防いで指定スコア版とノーツ数を渡す。
    /// </summary>
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
        Assert.AreEqual(17, selected.BeatorajaScoreContext.ScoreSnapshotVersion);
        Assert.AreEqual(100, selected.BeatorajaScoreContext.ScoresBySha256["sha256"].totalnotes);
    }

    /// <summary>
    /// 設定無効・選択元違い・空パス・不在の各条件ではLR2の既存連携情報へ戻し、成立していないbeatoraja入力を使わない。
    /// </summary>
    [TestMethod]
    public void Select_FallsBackToExistingLr2ContextWhenAnyBeatorajaFactDoesNotMatch()
    {
        var cases = new (string Name, bool UseBeatoraja, ActiveScoreSource ActiveSource, string BeatorajaPath, bool FileExists)[]
        {
            ("利用設定無効", false, ActiveScoreSource.Beatoraja, "beatoraja.db", true),
            ("別取得元を選択", true, ActiveScoreSource.Lr2, "beatoraja.db", true),
            ("空白パス", true, ActiveScoreSource.Beatoraja, " ", true),
            ("ファイル不在", true, ActiveScoreSource.Beatoraja, "beatoraja.db", false)
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

    /// <summary>
    /// パス・スコア面がnullの要求は空パスのLR2連携情報として残し、架空の取得元を補わない。
    /// </summary>
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

    /// <summary>
    /// 要求全体がnullなら引数不正を返し、既定取得元へ黙って置き換えない。
    /// </summary>
    [TestMethod]
    public void Select_RejectsNullInput()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => PlayHistoryReadSourceSelector.Select(null));
    }
}
